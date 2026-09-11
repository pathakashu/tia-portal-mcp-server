# TIA Engineering Copilot — Engineer-PC Platform (TIA Portal V19 Only)

An Engineer-PC execution platform that lets a remote AI agent propose Siemens TIA Portal
engineering changes, while a local deterministic runtime validates, gates, approves and
executes them. The AI proposes intent; it never touches Siemens Openness.

## Scope

This solution is explicitly limited to **Siemens TIA Portal V19 / TIA Portal Openness V19**.

The platform covers:

- Secure HTTPS Streamable HTTP / mTLS boundary
- Engineer-PC MCP Server
- Engineering Engine
- Engineering IR v1
- Policy / Approval
- Deterministic Validation
- Transaction management
- Project Model
- Project Graph
- Project Search
- TIA V19 adapter
- TIA Openness V19 integration boundary
- Audit trail
- Mock TIA adapter for development and tests

## Explicit version rule

**TIA Portal V19 is the only supported TIA version in this repository.**

Do not introduce:

- TIA Portal V18
- TIA Portal V20
- TIA Portal V21
- multi-version adapter factories
- version-neutral Siemens API claims
- Siemens API calls invented from memory

The architecture may contain an abstract `ITiaAdapter`, but the only concrete Siemens adapter is `TiaV19Adapter`.

## Implementation languages

The platform exists in two forms while the Python port is being validated:

- **Python** (`python/`) — the go-forward implementation of everything above the Siemens
  boundary. See `python/README.md`.
- **C# solution** (`EngineerPc.sln`) — the original implementation, retained until parity is
  signed off.

**The Siemens Openness adapter stays C# in both.** `Siemens.Engineering.dll` is a .NET
Framework 4.8 assembly with no Python binding, so `EngineerPc.Tia.V19` and
`EngineerPc.Tia.V19.Worker` remain the only concrete Siemens code. The Python host drives
that same worker executable over its unchanged JSON stdin/stdout protocol, which keeps
Siemens types out of the host process exactly as `AGENTS.md` requires.

Both hosts expose the same endpoint, tools, configuration keys and dev console. Parity on
the byte-sensitive behaviours (operation hashing, JSON escaping, deterministic request ids,
snapshot hashing, rendered SCL, worker wire format) is enforced by golden vectors generated
from the C# implementation via `tools/ParityVectors` and asserted in the Python suite.

## Status

Verified end-to-end against a real TIA Portal V19 project (1 400+ blocks, real S7 controller):

| Capability | Status |
|---|---|
| MCP handshake, sessions, tool discovery | ✅ verified |
| `plan_create_block` / `preview_scl_block` | ✅ verified |
| `get_project_context` | ✅ verified against real project |
| `get_block_catalog` incl. multi-page continuation | ✅ verified (3 pages, stable snapshot) |
| `approve_create_block` → `execute_create_block` | ✅ **verified — real block created in a real project** |
| mTLS / client-certificate transport | ⚠️ implemented, **not yet exercised against real certificates** |

## Architecture

From the cloud agent down to the controller. Note that the MCP host and the Siemens worker
are **separate OS processes** — that boundary is what keeps `Siemens.Engineering` types out
of the host, and it is also what allows the host to be Python while the adapter stays .NET
Framework.

```text
┌────────────────────────────────────────────────────────────────────────┐
│  CLOUD (e.g. Azure AI Foundry agent, Azure OpenAI, any MCP client)     │
│    proposes intent only · holds engineering.plan / engineering.read    │
└───────────────────────────────┬────────────────────────────────────────┘
                                │  MCP 2025-06-18 Streamable HTTP
                                │  HTTPS + mutual TLS (client certificate)
                                │  JSON-RPC 2.0 POST /mcp
╔═══════════════════════════════▼════════════════════════════════════════╗
║  ENGINEER-PC  (Windows workstation with TIA Portal V19 installed)      ║
║                                                                        ║
║  ┌──────────────────────────────────────────────────────────────────┐  ║
║  │ PROCESS 1 — MCP Host   (Python: engineerpc.host                  │  ║
║  │                         C#:     EngineerPc.Mcp.Host)             │  ║
║  │                                                                  │  ║
║  │  Transport      TLS 1.2/1.3 · client-cert → principal allow-list │  ║
║  │                 origin check · body/time limits · /health · UI   │  ║
║  │        │                                                         │  ║
║  │  MCP   ▼        JSON-RPC · session lifecycle · replay protection │  ║
║  │                 tool router · role+scope authorization (deny by  │  ║
║  │        │        default)                                         │  ║
║  │  Engineering Engine                                              │  ║
║  │        ├── Engineering IR v1     typed CreateBlock intent        │  ║
║  │        ├── Validation            identifiers, params, data types │  ║
║  │        ├── Planner               deterministic operation hash    │  ║
║  │        ├── SCL renderer          constrained source generation   │  ║
║  │        ├── Policy                risk + approval requirement     │  ║
║  │        ├── Approvals             human approval, fully bound     │  ║
║  │        ├── Transactions          immutable state machine         │  ║
║  │        ├── Audit                 append-only JSON Lines          │  ║
║  │        └── Project intelligence  model · graph · search          │  ║
║  │        │                                                         │  ║
║  │  ITiaAdapter  ──┬── MockTiaAdapter         (tests, no Siemens)   │  ║
║  │                 ├── PlanningOnlyTiaAdapter (fails closed)        │  ║
║  │                 └── TiaV19WorkerClient ───┐                      │  ║
║  └───────────────────────────────────────────┼──────────────────────┘  ║
║                                              │ one JSON line per       ║
║                                              │ request over stdin,     ║
║                                              │ one line back on stdout ║
║                                              │ (process per request)   ║
║  ┌───────────────────────────────────────────▼──────────────────────┐  ║
║  │ PROCESS 2 — Siemens worker   EngineerPc.Tia.V19.Worker.exe       │  ║
║  │              .NET Framework 4.8 · C# ONLY · the sole component   │  ║
║  │              permitted to reference Siemens.Engineering          │  ║
║  │                                                                  │  ║
║  │   TiaV19Adapter: open project · snapshot hash · enumerate blocks │  ║
║  │                  locate controller · generate block from SCL     │  ║
║  └───────────────────────────────┬──────────────────────────────────┘  ║
╚══════════════════════════════════┼═════════════════════════════════════╝
                                   │ TIA Openness V19 API
                                   ▼
                        TIA Portal V19 (headless session)
                                   │
                                   ▼
                        .ap19 project  →  PLC / HMI / Hardware
```

**Why the process split matters.** `AGENTS.md` forbids Siemens types outside the adapter
boundary. Making that boundary a *process* boundary with a framework-neutral JSON protocol
means the host can be any language, the adapter can stay on .NET Framework 4.8 (which
Openness requires), and a crash or hang inside TIA cannot take the host down — the worker is
spawned per request and killed on timeout.

## Engineering principle

The remote AI can propose intent. It must never directly invoke Siemens Openness objects.

The Engineer-PC runtime remains deterministic and treats remote requests as untrusted input.

## Low-level design

### Module map

| Layer | Python | C# | Responsibility |
|---|---|---|---|
| Contracts | `engineerpc/contracts.py` | `EngineerPc.Contracts` | `ProjectContext`, `AuthenticatedIdentity`, `AuthenticatedPrincipal` |
| IR | `engineerpc/ir.py` | `EngineerPc.Engineering.Ir` | `CreateBlockOperation`, block interface, SCL assignments, structural validation |
| Validation | `engineerpc/validation.py` | `…Engineering.Validation` | Identifier rules, duplicate/missing parameter detection |
| Planning | `engineerpc/engine.py` | `…Engineering.Engine` | Operation hash, human-readable preview |
| SCL rendering | `engineerpc/engine.py` | `…Engineering.Engine` | Constrained `FUNCTION`/`FUNCTION_BLOCK` source generation |
| Policy | `engineerpc/policy.py` | `…Engineering.Policy` | Risk classification, approval requirement |
| Approvals | `engineerpc/approvals.py` | `…Engineering.Approvals` | Human approval binding and expiry |
| Transactions | `engineerpc/transactions.py` | `…Engineering.Transactions` | Immutable lifecycle state machine |
| Workflow | `engineerpc/engine.py` | `…Engineering.Engine` | submit → approve → execute orchestration |
| Audit | `engineerpc/audit.py` | `EngineerPc.Audit` | Append-only JSON Lines engineering events |
| Security | `engineerpc/security.py` | `EngineerPc.Security` | Role/scope authorization, security events |
| Project intelligence | `engineerpc/project_intelligence.py` | `ProjectModel`/`Graph`/`Search` | Snapshots, relationship graph, deterministic search |
| TIA abstraction | `engineerpc/tia/abstractions.py` | `EngineerPc.Tia.Abstractions` | `ITiaAdapter`, catalog reader, result types |
| Mock adapter | `engineerpc/tia/mock.py` | `EngineerPc.Tia.Mock` | Siemens-free adapter for tests |
| Worker protocol | `engineerpc/tia/v19_protocol.py` | `EngineerPc.Tia.V19.Protocol` | Wire DTOs, project snapshot hashing |
| Worker client | `engineerpc/tia/v19_client.py` | `EngineerPc.Tia.V19.Client` | Subprocess transport, response validation |
| **Siemens adapter** | *(stays C#)* | `EngineerPc.Tia.V19` + `.Worker` | Real Openness calls |
| MCP | `engineerpc/mcp/` | `EngineerPc.Mcp` | Sessions, tool router, request DTOs |
| Host | `engineerpc/host/` | `EngineerPc.Mcp.Host` | JSON-RPC processing, transport, config, dev console |

### Request lifecycle

Every `tools/call` passes the same gate sequence before any engineering logic runs. Failing
any step returns an error result and never reaches the Engine:

1. **Transport** — TLS/mTLS, client certificate mapped to a principal (or the fixed dev
   principal under `AllowInsecureLocalhost`), origin check, body-size and deadline limits.
2. **Protocol** — `jsonrpc: "2.0"`, `MCP-Protocol-Version: 2025-06-18` header on every call
   after `initialize`.
3. **Session** — `Mcp-Session-Id` resolves to a session in `Ready` state and not expired.
4. **Authorization** — the principal holds the tool's required role *and* scope. Unknown
   operations are denied by default. Every decision is written to the security audit log.
5. **Replay protection** — the internal request id (derived deterministically from the
   session id and the JSON-RPC id) has not been seen before on this session.
6. **Engine** — the tool executes.

### The write pipeline

Writing a block takes three separate authenticated calls, bound together cryptographically:

```text
plan_create_block ──► validate → policy → hash operation → create transaction
                      state: Created → Validating → AwaitingApproval
                      returns: plan (operationHash) + policyDecision + transaction

approve_create_block ─► binds approval to: transaction id, operationHash,
                        project snapshotHash, authenticated approver identity,
                        expiry.  ANY mismatch rejects instead of approving.
                        state: AwaitingApproval → Approved

execute_create_block ─► RE-PLANS the submitted operation and refuses unless the
                        recomputed hash still equals the approved hash, the
                        operation id matches, and the project context matches.
                        Then renders SCL and calls the worker.
                        state: Approved → Executing → ValidatingResult → Committed
                                              └────────────► Failed (on adapter error)
```

There is **no server-side transaction store**. The client round-trips the
`EngineeringTransaction` object between calls. This is deliberate: the server holds no
mutable cross-request engineering state, so a restart cannot strand a half-approved write,
and a tampered transaction simply fails the hash comparison.

The approver identity is always taken from the authenticated session — never from the
request payload.

### Determinism contracts

These are load-bearing and byte-sensitive. Both implementations are pinned to them by
golden vectors in `tools/ParityVectors` / `python/tests/test_parity.py`:

- **Operation hash** — SHA-256 over the operation serialized with .NET `JsonSerializerDefaults.Web`.
  Enums serialize as **integers** here; property order is derived-record members first, then
  the `operationType` override, then base-record members. Upper-case hex.
- **String escaping** — .NET's default `JavaScriptEncoder` escapes `" & ' + < >` backtick and all
  non-ASCII as `\uXXXX` with **upper-case** hex; non-BMP characters become surrogate pairs.
  `DateTimeOffset` is written by a dedicated converter, so the `+` in its offset is *not* escaped.
- **Deterministic request id** — SHA-256 of `{sessionId:N}:{raw JSON-RPC id}`, read back as a
  .NET GUID (mixed-endian: `uuid.UUID(bytes_le=…)` in Python, not `bytes=`).
- **Project snapshot hash** — length-prefixed fields joined by `\n`, upper-case hex, over
  `projectId`, project name, project path, `LastModified` ticks and `Version`.
  **`Project.Size` is deliberately excluded** — TIA appends a log entry on every Openness
  open, so the reported size grows on each read even when nothing changed. Including it made
  the snapshot differ on every read, which permanently broke catalog continuation and made
  every approved write fail its stale-context check.
- **Rendered SCL** — CRLF line endings, four-space indentation, no trailing newline.

### Worker protocol

One JSON object per line over stdin, one response line on stdout, one process per request.
Methods: `get_project_context`, `get_block_catalog`, `create_block`. Protocol version `1.3`.

The worker only ever accepts a **configured project id** — never a path. Project paths live
in a local worker configuration file owned by the deployment, so no MCP input can select
which project is opened. The client validates the response's request id, project id and
pagination arithmetic before trusting it, and kills the worker on timeout.

### Error scrubbing

Failures return generic messages over MCP (`"Configured project context was not found."`).
The underlying Siemens exception type appears only in the local engineering audit log, and
the full message only if you invoke the worker directly. This is intentional: worker
diagnostics must not cross the network boundary. It also means **you cannot diagnose real
Openness failures from the MCP response** — check the audit log or run the worker by hand.

## MCP tools

The host advertises tools through `tools/list`; which ones appear depends on deployment configuration (see the "Available when" column). Every tool requires the authenticated principal to hold the listed role and scope — there is no anonymous or partially-authorized access. Full JSON shapes and worked examples are in `docs/mcp/protocol.md`.

| Tool | Purpose | Requires | Available when | Touches TIA? |
|---|---|---|---|---|
| `plan_create_block` | Validates a typed `CreateBlock` intent (name, block type, language, interface, optional controller), evaluates it against policy, and — if allowed — creates a transaction in the `AwaitingApproval` state with a deterministic operation hash. This is the entry point to every write; it never executes anything itself. | `Engineer` role, `engineering.plan` scope | Always | No |
| `preview_scl_block` | Deterministically renders and validates the constrained SCL source text (`FUNCTION`/`FUNCTION_BLOCK` … `END_FUNCTION`/`END_FUNCTION_BLOCK`) for a `Function`/`FunctionBlock` intent with `language: "Scl"`. Same rendering logic `execute_create_block` uses internally. Read-only — no transaction, no TIA call. | `Engineer` role, `engineering.plan` scope | Always | No |
| `get_project_context` | Reads the current snapshot (`projectId` + `snapshotHash`) of the deployment-configured TIA V19 project, for stale-context detection before planning or continuing a paginated catalog read. | `Engineer` role, `engineering.read` scope | `TiaV19Worker:Enabled=true` | Yes (read-only) |
| `get_block_catalog` | Reads a paginated, read-only catalog of PLC blocks (controller name, block name, namespace, number, programming language). Bounded to 500 blocks per page; continuation passes the prior page's `expectedSnapshotHash`, and a genuinely changed project forces a restart from `startIndex: 0`. | `Engineer` role, `engineering.read` scope | `TiaV19Worker:Enabled=true` and `EnableBlockCatalogRead=true` | Yes (read-only) |
| `approve_create_block` | Records human approval for a transaction that is `AwaitingApproval`, binding it to the authenticated identity (not a client-supplied value), the transaction id, the operation hash, the project snapshot hash, and an expiration. Any mismatch invalidates the approval instead of transitioning the transaction. | `Engineer` role, `engineering.execute` scope | `TiaV19Worker:Enabled=true` and `EnableBlockWrite=true` | No |
| `execute_create_block` | Executes an `Approved` transaction: re-plans the operation and refuses unless the recomputed hash still matches what was approved, then renders the SCL source and writes the block to the named controller via TIA Openness (`GenerateBlocksFromSource`), returning the updated project snapshot. This is the only tool that can change a real TIA project. | `Engineer` role, `engineering.execute` scope | `TiaV19Worker:Enabled=true` and `EnableBlockWrite=true` | **Yes (write)** |

`approve_create_block` and `execute_create_block` both take the full `EngineeringTransaction` object returned by `plan_create_block` — there is no server-side transaction store, so the caller round-trips it. The dev console does this hand-off for you.

## Running and testing the MCP server

### Prerequisites

| For | Requirement |
|---|---|
| Python host | Python 3.11+ |
| C# host / Siemens worker | .NET 8 SDK |
| Building the worker | TIA Portal Openness V19 assemblies at `C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19` |
| Talking to a real project | TIA Portal V19 installed, **and the Windows account running the worker must be a member of the local `Siemens TIA Openness` group** |

> The Openness group membership is not optional. Without it every call fails with
> `EngineeringSecurityException` — *"Owner … of this process is not member of the windows
> group 'Siemens TIA Openness'"*. Add the account via `lusrmgr.msc` → Groups →
> `Siemens TIA Openness`, then log off and back on.

### Build

```powershell
# Siemens worker (+ the C# host, if you want it)
dotnet build EngineerPc.sln -c Debug
dotnet test  EngineerPc.sln -c Debug        # 16 test projects, no TIA required

# Python host
cd python
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -e ".[dev]"
.\.venv\Scripts\python.exe -m pytest         # 169 tests, no TIA required
```

The Python suite runs without .NET. To regenerate the C#-derived parity vectors after
changing any hashed behaviour:

```powershell
dotnet run --project tools/ParityVectors -- python/tests/parity_vectors.json
```

### Step 1 — describe your project to the worker

Create a local JSON file (never commit it — it names real project paths), e.g.
`C:\Engineering\tia-v19-worker.json`:

```json
{
	"projects": [
		{ "projectId": "test-project", "projectFilePath": "C:\\Path\\To\\YourProject.ap19" }
	]
}
```

`projectId` is the only handle MCP clients ever use; the path never crosses the network.

### Step 2 — start the host

Configuration binds like ASP.NET Core: `Section__Key`, arrays as `Section__Key__0`. Both
hosts read the same keys.

```powershell
$env:McpTransport__AllowInsecureLocalhost = "true"   # dev only, localhost plain HTTP
$env:McpTransport__RequestTimeoutSeconds  = "300"
$env:McpTransport__SessionDurationSeconds = "1800"   # see note below; default 300 is tight
$env:TiaV19Worker__Enabled                = "true"
$env:TiaV19Worker__EnableBlockCatalogRead = "true"
$env:TiaV19Worker__EnableBlockWrite       = "false"  # keep false until you mean it
$env:TiaV19Worker__WorkerExecutablePath   = "C:\git\tia-portal-mcp-server\src\EngineerPc.Tia.V19.Worker\bin\Debug\net48\EngineerPc.Tia.V19.Worker.exe"
$env:TiaV19Worker__ConfigurationPath      = "C:\git\tia-portal-mcp-server\tia-v19-worker.json"
$env:TiaV19Worker__RequestTimeoutSeconds  = "300"
$env:McpTransport__SessionDurationSeconds = "1800"

# EITHER the Python host (go-forward) …
cd C:\git\tia-portal-mcp-server\python
.\.venv\Scripts\python.exe -m engineerpc.host

# … OR the C# host — note it must run from the repo root, not from python\
cd C:\git\tia-portal-mcp-server
dotnet run --no-build --project src\EngineerPc.Mcp.Host -c Debug
```

Run **one** of them, not both — they bind the same port. Set every variable above in the
**same terminal** you start the host from: environment variables are per-session, so a host
started elsewhere will silently come up with no TIA-backed tools.

If startup fails with `[Errno 10048] … only one usage of each socket address` (Python) or an
equivalent bind error (C#), a previous host is still running. Find and stop it:

```powershell
Get-NetTCPConnection -LocalPort 7443 -State Listen |
    ForEach-Object { Get-Process -Id $_.OwningProcess } |
    Stop-Process -Force
```

Use generous timeouts. Two separate settings matter, and they are easy to confuse:

- **`RequestTimeoutSeconds`** (max 300) — how long a *single* call may take. A cold headless
  TIA session can take over a minute to open, and because the worker is spawned **per
  request**, every TIA-backed call pays that cost — expect 40–65 s each, not just the first.
- **`SessionDurationSeconds`** (max 3600) — how long the MCP session lives after
  `initialize`. It is **absolute, not sliding**. At the 300 s default, four or five TIA calls
  will exhaust it and the next call fails with *"MCP session is not ready for tool calls."*
  Raise it to 1800 for interactive work, or just click **Connect** again in the dev console
  to start a fresh session.

Confirm the host is up:

```powershell
curl http://localhost:7443/health     # {"status":"ready"}
```

Leave this terminal running — it *is* the server. Use a second terminal for everything below.

### Step 3 — dev console (browser)

Open `http://localhost:7443/`. A single static page served by the host itself (same origin as
`/mcp`, so no CORS or certificate setup in dev mode):

- **Connect** runs the handshake and lists the advertised tools.
- Selecting a tool generates an argument skeleton from its JSON Schema, pre-filling a fresh
  `operationId`/`idempotencyKey` and the most recently seen snapshot hash.
- **Insert last transaction / Insert last operation** carry the plan → approve → execute
  hand-off so you don't copy-paste objects between calls.
- A collapsible log shows the raw JSON-RPC traffic.

It's a development tool with no authentication of its own — don't expose the host to an
untrusted network while relying on it.

### Step 4 — test manually over raw JSON-RPC

Sequence: `initialize` (capture the `Mcp-Session-Id` response header) → `notifications/initialized`
(send the header back, no request id) → `tools/list` / `tools/call` (send both `Mcp-Session-Id`
and `MCP-Protocol-Version: 2025-06-18` on every subsequent call).

```powershell
$baseUrl = "http://localhost:7443/mcp"
$protocolVersion = "2025-06-18"

$init = Invoke-WebRequest -UseBasicParsing -Uri $baseUrl -Method Post -ContentType "application/json" -Body (@{
    jsonrpc = "2.0"; id = "1"; method = "initialize"
    params = @{ protocolVersion = $protocolVersion; capabilities = @{}; clientInfo = @{ name = "manual-test"; version = "1.0.0" } }
} | ConvertTo-Json -Depth 10)
$sessionId = $init.Headers["Mcp-Session-Id"]
$headers = @{ "Mcp-Session-Id" = $sessionId; "MCP-Protocol-Version" = $protocolVersion }

Invoke-WebRequest -UseBasicParsing -Uri $baseUrl -Method Post -ContentType "application/json" -Headers $headers -Body (@{
    jsonrpc = "2.0"; method = "notifications/initialized" } | ConvertTo-Json) | Out-Null

Invoke-WebRequest -UseBasicParsing -Uri $baseUrl -Method Post -ContentType "application/json" -Headers $headers -Body (@{
    jsonrpc = "2.0"; id = "2"; method = "tools/list"; params = @{} } | ConvertTo-Json)
```

Windows PowerShell 5.1 needs `-UseBasicParsing`; without it a machine without IE first-run
configured throws `NullReferenceException` instead of an HTTP error.

```json
{
  "jsonrpc": "2.0", "id": "3", "method": "tools/call",
  "params": { "name": "get_project_context", "arguments": { "projectId": "test-project" } }
}
```

### Step 5 — creating a real block (the write path)

`EnableBlockWrite=true` lets `execute_create_block` write into the configured project via
`GenerateBlocksFromSource`. Before enabling it:

> **Work on a copy.** Copy the project folder (e.g. to `C:\Engineering\MyProject_WriteTest`),
> point the worker configuration at the copy, and write there first. This is exactly how the
> write path was validated for this repo.

Restart the host with `TiaV19Worker__EnableBlockWrite = "true"`, then run the three calls in
order, feeding each result into the next:

1. **`plan_create_block`** with a `projectContext.snapshotHash` from `get_project_context`, a
   `controllerName` matching a real controller (take one from `get_block_catalog`), and an
   `interface`/`statements` using only declared identifiers. Returns a transaction in
   `AwaitingApproval`.
2. **`approve_create_block`** with that transaction plus an `expiresAtUtc`. Returns the
   transaction in `Approved`.
3. **`execute_create_block`** with the approved transaction and the *same* operation object.
   Returns `isCommitted: true` and the updated project snapshot.

Verify by re-reading `get_block_catalog` — the total block count increases and the new block
appears with `programmingLanguage: "SCL"` under the named controller. Blocks sort by
controller → namespace → name → number, so a new `FB_*` will not be on the first page of a
project full of `DB_*` blocks; page through with `startIndex`/`nextStartIndex`.

### Troubleshooting

| Symptom | Cause |
|---|---|
| `EngineeringSecurityException` | Account not in the `Siemens TIA Openness` group |
| HTTP 504 on a TIA-backed call | The call exceeded `RequestTimeoutSeconds`. Every call starts a fresh TIA session, so raise it (max 300) |
| `"MCP session is not ready for tool calls."` | The session expired — it lives `SessionDurationSeconds` from `initialize`, absolute. Re-run `initialize` (**Connect** in the console) or raise the setting |
| `"Configured project context was not found."` | Generic by design. Check `%LOCALAPPDATA%\EngineerPc\audit\engineering-events.jsonl`, or pipe one JSON line into `EngineerPc.Tia.V19.Worker.exe --configuration <path>` for the full error |
| `"…snapshot has changed."` on a write | The project genuinely changed between plan and execute — re-read the context and re-plan |
| Tool missing from `tools/list` | Its feature switch is off (`Enabled` / `EnableBlockCatalogRead` / `EnableBlockWrite`) — or the variables were set in a different terminal from the one that started the host |
| `[Errno 10048]` / bind error on startup | A previous host is still listening on the port; stop it (see Step 2) |
| `'src\EngineerPc.Mcp.Host' is not a valid project file` | You are in `python\`; the C# host must be started from the repo root |

## Integrating an AI agent deployed in Azure

The Engineer-PC sits in an engineering/OT network with TIA Portal installed; the agent runs in
Azure. Three things have to be solved: **network reachability**, **authentication**, and
**how much authority the agent gets**.

### Authority model — decide this first

The approval gate only means something if the agent cannot approve its own work. Scopes are
what enforce that:

| Principal | Scopes | Can do |
|---|---|---|
| **Azure agent** | `engineering.plan`, `engineering.read` | Read project context and block catalog, plan changes, preview SCL. **Cannot approve or execute.** |
| **Human engineer** | `engineering.execute` (+ the above) | Review the plan and preview, then approve and execute |

Map the agent's client certificate to the read/plan scopes **only**. If you grant the agent
`engineering.execute`, it can approve its own transactions and the human-in-the-loop control
is gone. The recommended flow is: the agent plans and presents the SCL preview; a human
approves and executes via the dev console or an operator client.

### Network reachability

The host listens on the Engineer-PC. Pick whichever fits your network policy — none of these
require changing the server:

- **Azure Relay Hybrid Connections** — a listener on the Engineer-PC dials *out* to Azure, so
  no inbound firewall rule or public IP is needed. Usually the easiest fit for an OT network.
- **Site-to-site VPN or ExpressRoute** — the agent's VNet routes privately to the Engineer-PC.
- **Reverse proxy in a DMZ** — terminate or pass through TLS to the Engineer-PC. If the proxy
  terminates TLS, it must forward the client certificate, or the thumbprint mapping cannot work.

### Authentication

Production requires mTLS — the host rejects any client whose certificate is not issued by a
trusted issuer *and* explicitly allow-listed by SHA-1 thumbprint. On the host:

```powershell
$env:McpTransport__AllowInsecureLocalhost = "false"
$env:McpTransport__ServerCertificatePath  = "C:\certs\engineer-pc.pfx"
$env:McpTransport__ServerCertificatePassword = "<from a secret store>"
$env:McpTransport__TrustedClientIssuers__0   = "CN=Your Issuing CA, O=Your Org"

$env:McpTransport__ClientPrincipalMappings__0__CertificateThumbprint = "<agent cert SHA-1, 40 hex>"
$env:McpTransport__ClientPrincipalMappings__0__Roles__0  = "Engineer"
$env:McpTransport__ClientPrincipalMappings__0__Scopes__0 = "engineering.plan"
$env:McpTransport__ClientPrincipalMappings__0__Scopes__1 = "engineering.read"
```

Roles, scopes, subjects and thumbprints are **only** ever read from this local configuration —
never from an MCP payload. See `docs/deployment/security.md`.

> **Check this early:** many managed agent runtimes cannot present a client TLS certificate.
> If yours can't, put a thin broker inside your network that holds the certificate and
> forwards to `/mcp`, and authenticate the agent to the broker by whatever means Azure gives
> you (managed identity, Entra token). Don't work around it by enabling
> `AllowInsecureLocalhost` on a reachable host.

Store the client certificate in **Azure Key Vault** and load it at runtime via managed
identity. Never bake it into an image or repository.

### Agent-side MCP client (Python)

The transport is standard MCP Streamable HTTP, so any MCP-compliant client works.

```python
import asyncio
from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client

MCP_URL = "https://engineer-pc.internal.example:7443/mcp"

async def main():
    async with streamablehttp_client(MCP_URL) as (read, write, _):
        async with ClientSession(read, write) as session:
            await session.initialize()

            tools = await session.list_tools()
            print([t.name for t in tools.tools])

            context = await session.call_tool(
                "get_project_context", arguments={"projectId": "test-project"}
            )

            # Plan only — approving and executing belong to a human principal.
            plan = await session.call_tool("plan_create_block", arguments={...})
            print(plan)

asyncio.run(main())
```

For mTLS the client needs the certificate on its HTTP transport, which the `mcp` SDK builds
on `httpx`:

```python
import httpx

http_client = httpx.AsyncClient(
    cert=("client-cert.pem", "client-key.pem"),   # from Key Vault at runtime
    verify="ca-bundle.pem",
)
```

> These snippets are illustrative, **not pinned to a verified `mcp` SDK version**. Check the
> installed package for the exact `streamablehttp_client` / `ClientSession` signatures and how
> to inject a custom `httpx.AsyncClient` — the parameter names have changed across releases.
> Also confirm the SDK sends `MCP-Protocol-Version: 2025-06-18`; pass it via `headers` if not.

### Agent design notes

- **Tool availability is deployment-controlled.** The agent should call `tools/list` and adapt,
  not assume `get_block_catalog` exists.
- **Snapshot discipline.** Fetch `get_project_context` immediately before planning. A plan
  built on a stale snapshot will be rejected at execution.
- **Pagination.** `get_block_catalog` returns at most 500 blocks; follow `nextStartIndex` and
  pass the prior page's `expectedSnapshotHash`.
- **Errors are deliberately vague.** The agent cannot self-diagnose Openness failures — surface
  the message to a human rather than retrying blindly.
- **Latency.** A cold TIA session can take minutes. Set generous client timeouts and don't
  treat a slow first call as failure.

## Recommended implementation sequence

1. Architecture and contracts
2. Engineering IR
3. Engineering Engine
4. Policy / approval / validation
5. Transactions
6. Mock TIA V19 adapter
7. Project model / graph / search
8. MCP server
9. HTTPS Streamable HTTP / mTLS
10. Real TIA V19 Openness adapter
11. Integration tests on a dedicated TIA V19 engineering workstation

See `docs/roadmap/implementation-sequence.md`.

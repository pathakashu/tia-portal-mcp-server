# TIA Engineering Copilot — Engineer-PC Platform (TIA Portal V19 Only)

This repository is the GitHub Copilot workspace for building the Engineer-PC execution platform for an AI-assisted Siemens TIA Portal engineering system.

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

## Current implementation

The buildable foundation currently includes:

- canonical project context contracts
- Engineering IR v1 for `CreateBlock` intent and deterministic structural validation
- deterministic naming, parameter, and declared-data-type validation for block creation
- deterministic planning with preview and operation hashing
- deterministic constrained SCL source preview generation from typed block intent
- default policy evaluation for medium-risk, approval-required block creation
- human approval validation bound to trusted identity, transaction, operation, project snapshot, and expiration
- immutable transaction lifecycle state transitions
- Siemens-free Mock TIA adapter with per-project write serialization, duplicate detection, and stale-context protection
- isolated .NET Framework 4.8 `EngineerPc.Tia.V19` adapter and sequential worker compiled against the installed Siemens Openness V19 assembly, with framework-neutral project-context, read-only PLC block-catalog, and create-block protocols plus a controlled .NET 8 worker client bridge
- controlled V1 `CreateBlock` workflow from validation through human approval, typed execution, post-execution state transition, and commit, able to drive either the Siemens-free mock adapter or the real V19 worker
- canonical project artifacts and deterministic V19 project snapshots for change and stale-context detection
- canonical project graph with validated relationship edges and deterministic local project search
- typed in-process MCP `plan_create_block`, `approve_create_block`, and `execute_create_block` routing with trusted session identity, request/correlation IDs, expiration, and replay protection
- trusted-principal role and scope authorization with default deny and structured in-process security events
- append-only, write-through local JSON Lines persistence for security authorization events and scrubbed engineering lifecycle outcomes
- fail-closed Kestrel host configuration for TLS 1.2/1.3, mandatory client certificates, trusted-issuer, expiry, and local thumbprint-to-principal validation, message limits, and request deadlines
- a bounded MCP `2025-06-18` Streamable HTTP endpoint at `/mcp`, with JSON-RPC initialization, tool discovery, an approval-gated `plan_create_block` tool, an authorization-gated `preview_scl_block` tool, optional authorization-gated `get_project_context` and `get_block_catalog` tools backed by the V19 worker, and optional authorization-gated `approve_create_block`/`execute_create_block` tools that can write to the configured V19 project
- unit tests for valid and rejected planning, policy, transaction, and worker-client paths

The network endpoint is non-writing by default. It always supports the allow-listed create-block planning and constrained SCL source preview tools; both require the `Engineer` role and `engineering.plan` scope, and produce scrubbed audit events. SCL preview input is structured: it can declare inputs and outputs, then assign a declared input or output identifier to a declared output identifier; it cannot submit arbitrary source text or expressions. It advertises `get_project_context` only when deployment configuration explicitly enables the V19 worker with trusted local paths, and `get_block_catalog` only when the separate default-off catalog feature switch is also enabled. Catalog responses are bounded to 500 blocks and use snapshot-bound `startIndex` / `nextStartIndex` continuation with explicit total-count and truncation state; a changed project snapshot requires restarting the catalog read. Both read-only tools require the `Engineer` role and `engineering.read` scope, and produce scrubbed audit events. Remote execution is possible only when the separate default-off `EnableBlockWrite` switch is also enabled, in which case `approve_create_block` and `execute_create_block` (both requiring the `Engineer` role and `engineering.execute` scope) become available; execution only ever creates the constrained `Function`/`FunctionBlock` Scl surface `preview_scl_block` already generates, and the real write call in `TiaV19Adapter` is a best-effort implementation that must be verified against the installed V19 assembly before that switch is turned on (see `docs/tia/adapter-architecture.md` and `docs/deployment/real-tia-v19.md`). The development HTTP endpoint is unavailable unless `McpTransport__AllowInsecureLocalhost=true`; production clients must use mTLS and a certificate explicitly mapped to trusted roles and scopes in local deployment configuration.

## Core architecture

```text
Cloud MCP Client
      |
      | HTTPS Streamable HTTP / mTLS
      v
Engineer-PC MCP Server
      |
      v
Engineering Engine
      |
      +--> Engineering IR
      +--> Policy / Approval
      +--> Validation
      +--> Transactions
      +--> Audit
      +--> Project Intelligence
      |
      v
TIA V19 Adapter
      |
      v
TIA Portal Openness V19
      |
      v
TIA Portal V19
      |
      +--> PLC
      +--> HMI
      +--> Hardware
```

## Engineering principle

The remote AI can propose intent. It must never directly invoke Siemens Openness objects.

The Engineer-PC runtime remains deterministic and treats remote requests as untrusted input.

## MCP tools

The host advertises tools through `tools/list`; which ones appear depends on deployment configuration (see the "Available when" column). Every tool requires the authenticated principal to hold the listed role and scope — there is no anonymous or partially-authorized access. Full JSON shapes and worked examples are in `docs/mcp/protocol.md`.

| Tool | Purpose | Requires | Available when | Touches TIA? |
|---|---|---|---|---|
| `plan_create_block` | Validates a typed `CreateBlock` intent (name, block type, language, interface, optional controller), evaluates it against policy, and — if allowed — creates a transaction in the `AwaitingApproval` state with a deterministic operation hash. This is the entry point to every write; it never executes anything itself. | `Engineer` role, `engineering.plan` scope | Always | No |
| `preview_scl_block` | Deterministically renders and validates the constrained SCL source text (`FUNCTION`/`FUNCTION_BLOCK` … `END_FUNCTION`/`END_FUNCTION_BLOCK`) for a `Function`/`FunctionBlock` intent with `language: "Scl"`. Same rendering logic `execute_create_block` uses internally. Read-only — no transaction, no TIA call. | `Engineer` role, `engineering.plan` scope | Always | No |
| `get_project_context` | Reads the current snapshot (`projectId` + a content-derived `snapshotHash`) of the deployment-configured TIA V19 project, for stale-context detection before planning or continuing a paginated catalog read. | `Engineer` role, `engineering.read` scope | `TiaV19Worker:Enabled=true` | Yes (read-only) |
| `get_block_catalog` | Reads a paginated, read-only catalog of PLC blocks (controller name, block name, namespace, number, programming language) from the configured project. Bounded to 500 blocks per page; a changed project snapshot forces the client to restart from `startIndex: 0`. | `Engineer` role, `engineering.read` scope | `TiaV19Worker:Enabled=true` and `EnableBlockCatalogRead=true` | Yes (read-only) |
| `approve_create_block` | Records human approval for a transaction that is `AwaitingApproval`, binding the approval to the authenticated identity (not a client-supplied value), the transaction ID, the operation hash, the project snapshot hash, and an expiration. Any mismatch — including a stale snapshot — invalidates the approval instead of transitioning the transaction. | `Engineer` role, `engineering.execute` scope | `TiaV19Worker:Enabled=true` and `EnableBlockWrite=true` | No |
| `execute_create_block` | Executes an `Approved` transaction: re-plans the original operation and refuses to proceed unless the recomputed hash still matches what was approved, then — for the constrained `Scl` `Function`/`FunctionBlock` surface — renders the SCL source and writes the block to the named controller via TIA Openness (`GenerateBlocksFromSource`), returning the updated project snapshot. This is the only tool that can change a real TIA project. | `Engineer` role, `engineering.execute` scope | `TiaV19Worker:Enabled=true` and `EnableBlockWrite=true` | **Yes (write)** |

`approve_create_block` and `execute_create_block` both take the full `EngineeringTransaction` object returned by `plan_create_block` (and, for approval, `approve_create_block`'s own response) — there is no server-side transaction store, so the caller round-trips it. See "Dev console" below for a tool that does this hand-off for you.

## Running and testing the MCP server

### Build

```
dotnet build EngineerPc.sln -c Debug
```

This builds all 38 projects, including the .NET Framework 4.8 `EngineerPc.Tia.V19` adapter and `EngineerPc.Tia.V19.Worker`. Those two require the real Siemens Openness V19 `Siemens.Engineering.dll` (default path `C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19`) to be present at build time; every other project builds without it.

```
dotnet test EngineerPc.sln -c Debug
```

runs the full xUnit suite (Mock-adapter-backed; no TIA Portal required).

### Start the server locally (development mode)

Local testing without mTLS certificates uses the explicit development-only insecure listener. Set configuration through environment variables (`Section__Key` binds to `appsettings.json`'s `Section.Key`) and run the host:

```powershell
$env:McpTransport__AllowInsecureLocalhost = "true"
$env:McpTransport__RequestTimeoutSeconds = "300"      # generous; a cold TIA Portal Openness session can take a while to open
$env:TiaV19Worker__Enabled = "true"                   # omit/false to run with no TIA-backed tools at all
$env:TiaV19Worker__EnableBlockCatalogRead = "true"    # read-only; safe default
$env:TiaV19Worker__EnableBlockWrite = "false"         # only set true once you intend to write to the configured project
$env:TiaV19Worker__WorkerExecutablePath = "C:\git\tia-portal-mcp-server\src\EngineerPc.Tia.V19.Worker\bin\Debug\net48\EngineerPc.Tia.V19.Worker.exe"
$env:TiaV19Worker__ConfigurationPath = "C:\Engineering\tia-v19-worker.json"
$env:TiaV19Worker__RequestTimeoutSeconds = "300"
dotnet run --no-build --project src\EngineerPc.Mcp.Host -c Debug
```

`TiaV19Worker__ConfigurationPath` points at a small local JSON file (never committed — it names real project paths) that the worker reads on startup:

```json
{
	"projects": [
		{ "projectId": "test-project", "projectFilePath": "C:\\Path\\To\\YourProject.ap19" }
	]
}
```

With `AllowInsecureLocalhost=true` the host listens on plain HTTP at `http://localhost:7443/mcp` (port from `McpTransport__Port`, default `7443`) and authenticates every request as a fixed local development principal (`Engineer` role, `engineering.plan`/`engineering.read`/`engineering.execute` scopes) — this mode is for local testing only and must stay off in any deployment reachable from outside the machine. Confirm the host is up with:

```
curl http://localhost:7443/health
```

### Dev console (browser UI)

Once the host is running, open `http://localhost:7443/` (or wherever `McpTransport__Port` points) in a browser for a built-in dev/test console — no separate install or process. It's a single static page (`src/EngineerPc.Mcp.Host/wwwroot/index.html`) served by the host itself, so it shares the same origin as `/mcp` and needs no CORS or certificate setup in local dev mode:

- **Connect** runs `initialize` + `notifications/initialized` and lists the currently advertised tools (which tools appear depends on `TiaV19Worker__Enabled`/`EnableBlockCatalogRead`/`EnableBlockWrite`, same as the raw JSON-RPC section below).
- Selecting a tool auto-generates an argument skeleton from its JSON Schema, with a few smart defaults filled in (a fresh `operationId`/`idempotencyKey` UUID, `projectId: "test-project"`, the most recently observed snapshot hash).
- **Insert last transaction** / **Insert last operation** solve the plan → approve → execute hand-off: the console remembers the `transaction` object returned by `plan_create_block`/`approve_create_block` and the `operation` object submitted to `plan_create_block`, so you don't have to copy-paste them between calls by hand.
- A collapsible request/response log on the right shows the raw JSON-RPC traffic for every call, in case the summarized response view isn't enough.

This is a local development tool, not a production UI — it has no authentication of its own beyond whatever the `/mcp` endpoint itself enforces (the fixed development principal under `AllowInsecureLocalhost`, or a real client certificate in production). Don't expose the host to an untrusted network while relying on it.

### Test manually over raw JSON-RPC

The server speaks MCP `2025-06-18` JSON-RPC 2.0 over HTTP POST (see `docs/mcp/protocol.md`). A manual session is: `initialize` (capture the `Mcp-Session-Id` response header) → `notifications/initialized` (send that header back, no request id) → `tools/list` / `tools/call` (send both `Mcp-Session-Id` and `MCP-Protocol-Version: 2025-06-18` on every subsequent call).

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
    jsonrpc = "2.0"; method = "notifications/initialized"
} | ConvertTo-Json) | Out-Null

Invoke-WebRequest -UseBasicParsing -Uri $baseUrl -Method Post -ContentType "application/json" -Headers $headers -Body (@{
    jsonrpc = "2.0"; id = "2"; method = "tools/list"; params = @{}
} | ConvertTo-Json)
```

Windows PowerShell 5.1's `Invoke-WebRequest` needs `-UseBasicParsing` here — without it, a fresh machine without IE first-run configured throws a `NullReferenceException` instead of an HTTP error. `curl.exe`/`Invoke-RestMethod`/any HTTP client works the same way; the only two contract points are the `Mcp-Session-Id` header round trip and the `MCP-Protocol-Version` header on every call after `initialize`.

`tools/call` bodies look like:

```json
{
  "jsonrpc": "2.0", "id": "3", "method": "tools/call",
  "params": { "name": "get_project_context", "arguments": { "projectId": "test-project" } }
}
```

`get_project_context` and `get_block_catalog` only appear in `tools/list` when `TiaV19Worker__Enabled`/`EnableBlockCatalogRead` are true; `approve_create_block`/`execute_create_block` only appear when `EnableBlockWrite` is also true. `plan_create_block` and `preview_scl_block` are always available and never touch TIA Portal. The write path (`plan_create_block` → `approve_create_block` → `execute_create_block`) needs the full `EngineeringTransaction` object returned by `plan_create_block` round-tripped into the later calls — there is no server-side transaction store — see `docs/mcp/protocol.md` for the exact shapes.

### Testing against a real TIA Portal V19 project

See `docs/deployment/real-tia-v19.md` for the full checklist. In short:

- The Windows account running the worker process must be a member of the local **`Siemens TIA Openness`** group (created by the TIA Portal installer), or every call fails with `EngineeringSecurityException`.
- A cold `TiaPortal(TiaPortalMode.WithoutUserInterface)` session can take well over a minute to open on first use — set both `McpTransport__RequestTimeoutSeconds` and `TiaV19Worker__RequestTimeoutSeconds` generously (up to 300, the validator's max) or reads will 504 before TIA finishes starting.
- Test against a disposable project first, not one you care about, especially before ever setting `EnableBlockWrite=true` — that flag lets `execute_create_block` write a real `FUNCTION`/`FUNCTION_BLOCK` into the configured project via `GenerateBlocksFromSource`.
- If a call fails, the MCP response is deliberately scrubbed (e.g. `"Configured project context was not found."`). The real Siemens exception type/message is not sent over the network; it only appears in the local engineering audit log (`%LOCALAPPDATA%\EngineerPc\audit\engineering-events.jsonl` by default) at the level of an exception *type name*, or by invoking `EngineerPc.Tia.V19.Worker.exe --configuration <path>` directly and piping it a single JSON request line, which returns the full `error` text.

### Integrating a Python MCP client

The transport is standard MCP Streamable HTTP, so any MCP-compliant Python client works. Using the official `mcp` SDK (`pip install mcp`):

```python
import asyncio
from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client

MCP_URL = "http://localhost:7443/mcp"  # or your mTLS endpoint, see below

async def main():
    async with streamablehttp_client(MCP_URL) as (read, write, _):
        async with ClientSession(read, write) as session:
            await session.initialize()
            tools = await session.list_tools()
            print([t.name for t in tools.tools])

            result = await session.call_tool(
                "get_project_context",
                arguments={"projectId": "test-project"},
            )
            print(result)

asyncio.run(main())
```

This is illustrative rather than pinned/verified against a specific `mcp` package version — check the installed SDK's docs for the exact `streamablehttp_client`/`ClientSession` signatures, and confirm it sends `MCP-Protocol-Version: 2025-06-18` (pass it via the client's `headers` parameter if not).

**Cloud client → this host, in production (mTLS, not `AllowInsecureLocalhost`):** matching the "Cloud MCP Client" box in the architecture diagram above, a remote client authenticates with a client certificate, not a bearer token. That requires, on the host side:

- `McpTransport__ServerCertificatePath` / `McpTransport__ServerCertificatePassword` — the host's own TLS server certificate.
- `McpTransport__TrustedClientIssuers__0` (and `__1`, …) — issuer(s) the host will accept client certificates from.
- `McpTransport__ClientPrincipalMappings__0__CertificateThumbprint`, `__0__Roles__0`, `__0__Scopes__0` — an explicit allow-list binding one client certificate's SHA-1 thumbprint to roles/scopes; unmapped or untrusted client certificates are rejected. See `docs/deployment/security.md`.
- The Engineer-PC reachable from wherever the cloud client runs (network/firewall/VPN — deployment-specific, not covered here), with `AllowInsecureLocalhost` left `false`.

On the Python side, present the mapped client certificate and pin the CA, e.g. via `httpx` (which the `mcp` SDK's Streamable HTTP transport is built on):

```python
import httpx

client = httpx.AsyncClient(
    cert=("client-cert.pem", "client-key.pem"),
    verify="ca-bundle.pem",
)
```

Consult the installed `mcp` SDK version for how to inject a custom `httpx.AsyncClient`/transport into `streamablehttp_client` (parameter names have changed across SDK releases). Never put certificate paths, thumbprints, roles, or scopes in MCP tool arguments — the host only accepts them from local transport-level configuration, by design.

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

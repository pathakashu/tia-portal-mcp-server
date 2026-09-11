# Engineer-PC MCP Server — Python implementation

A behaviour-preserving Python port of the C# Engineer-PC platform. It runs side by side
with the C# solution so the two can be compared before the C# host is retired.

## What is Python and what stays C#

**Ported to Python** (everything above the Siemens boundary):

| C# project | Python module |
|---|---|
| `EngineerPc.Contracts` | `engineerpc/contracts.py` |
| `EngineerPc.Engineering.Ir` | `engineerpc/ir.py` |
| `EngineerPc.Engineering.Validation` | `engineerpc/validation.py` |
| `EngineerPc.Engineering.Policy` | `engineerpc/policy.py` |
| `EngineerPc.Engineering.Transactions` | `engineerpc/transactions.py` |
| `EngineerPc.Engineering.Approvals` | `engineerpc/approvals.py` |
| `EngineerPc.Engineering.Engine` | `engineerpc/engine.py` |
| `EngineerPc.Audit` | `engineerpc/audit.py` |
| `EngineerPc.Security` | `engineerpc/security.py` |
| `EngineerPc.ProjectModel` / `ProjectGraph` / `ProjectSearch` | `engineerpc/project_intelligence.py` |
| `EngineerPc.Tia.Abstractions` | `engineerpc/tia/abstractions.py` |
| `EngineerPc.Tia.Mock` | `engineerpc/tia/mock.py` |
| `EngineerPc.Tia.V19.Protocol` | `engineerpc/tia/v19_protocol.py` |
| `EngineerPc.Tia.V19.Client` | `engineerpc/tia/v19_client.py` |
| `EngineerPc.Mcp` | `engineerpc/mcp/` |
| `EngineerPc.Mcp.Host` | `engineerpc/host/` |

**Deliberately still C#:** `EngineerPc.Tia.V19` and `EngineerPc.Tia.V19.Worker`.
`Siemens.Engineering.dll` is a .NET Framework 4.8 assembly with no Python binding, and
`AGENTS.md` forbids Siemens types outside the adapter boundary. Python drives the existing
worker executable over its unchanged JSON stdin/stdout protocol — the boundary was already
designed to be framework-neutral, so it doubles as a language boundary.

## Behavioural parity

Several behaviours are byte-sensitive; getting them wrong silently breaks approval
matching between planning and execution. Rather than assume, golden vectors are generated
from the running C# implementation and asserted in `tests/test_parity.py`:

```
dotnet run --project ../tools/ParityVectors -- tests/parity_vectors.json
```

The vectors pin, and the Python port reproduces exactly:

- **Operation hash** — SHA-256 over the operation serialised with .NET's Web defaults. That
  means enums as **integers**, and System.Text.Json's member order for a derived record:
  derived-declared properties first, then the `operationType` override, then the base
  record's properties last.
- **String escaping** — .NET's default `JavaScriptEncoder` escapes `" & ' + < > \`` and all
  non-ASCII as `\uXXXX` with **upper-case** hex (Python's `json` uses lower-case and escapes
  less). Non-BMP characters become surrogate pairs. `DateTimeOffset` is written through a
  dedicated converter, so the `+` in its offset is *not* escaped.
- **Deterministic MCP request id** — SHA-256 read back as a .NET GUID, which is mixed-endian:
  `uuid.UUID(bytes_le=...)`, not `bytes=`.
- **Project snapshot hash** — length-prefixed fields joined by `\n`, upper-case hex.
- **Rendered SCL** — CRLF line endings, matching `AppendLine()` on Windows.
- **Worker wire format** — the exact JSON the existing C# worker parses.

## Running

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -e ".[dev]"

$env:McpTransport__AllowInsecureLocalhost = "true"
$env:TiaV19Worker__Enabled = "true"
$env:TiaV19Worker__EnableBlockCatalogRead = "true"
$env:TiaV19Worker__EnableBlockWrite = "false"
$env:TiaV19Worker__WorkerExecutablePath = "C:\git\tia-portal-mcp-server\src\EngineerPc.Tia.V19.Worker\bin\Debug\net48\EngineerPc.Tia.V19.Worker.exe"
$env:TiaV19Worker__ConfigurationPath = "C:\Engineering\tia-v19-worker.json"
.\.venv\Scripts\python.exe -m engineerpc.host
```

Configuration binding matches ASP.NET Core (`Section__Key`, arrays as `Section__Key__0`),
so existing deployment scripts and `appsettings.json` work unchanged. The endpoint,
`/health`, the dev console at `/` and every tool behave as documented in the root `README.md`.

```
.\.venv\Scripts\python.exe -m pytest
```

## Verified

- 168 unit/parity tests pass, including the full `plan_create_block` → `approve_create_block`
  → `execute_create_block` flow over real JSON-RPC.
- The Python host was run against a real TIA Portal V19 project through the C# worker and
  returned the same tool schemas, the same 1426-block catalog and the same pagination as the
  C# host.

## Not yet verified

The mTLS path (`AllowInsecureLocalhost=false`) is implemented — trusted-issuer and expiry
checks, thumbprint-to-principal allow-list, and a `TlsAwareH11Protocol` that injects the peer
certificate into the ASGI scope, because uvicorn does not expose it by default — but it has
**not** been exercised against real certificates. The pure logic (validation, mapping,
thumbprints) is unit-tested; the uvicorn wiring is not. Verify it before production use.

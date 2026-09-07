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

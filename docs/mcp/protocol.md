# MCP Boundary Protocol

The host implements the MCP `2025-06-18` Streamable HTTP transport at `/mcp`. Requests use UTF-8 JSON-RPC 2.0 over HTTP POST. GET returns `405 Method Not Allowed` because the server does not offer an SSE stream.

The first request is `initialize`; the server responds with an `Mcp-Session-Id` header. The client sends `notifications/initialized` with that header before normal operations. Every subsequent POST must supply both `Mcp-Session-Id` and `MCP-Protocol-Version: 2025-06-18` headers.

The server always advertises the approval-gated `plan_create_block` tool through `tools/list`:

```json
{
  "jsonrpc": "2.0",
  "id": "request-id",
  "method": "tools/call",
  "params": {
    "name": "plan_create_block",
    "arguments": {
      "operationId": "...",
      "projectContext": { "projectId": "...", "snapshotHash": "..." },
      "idempotencyKey": "...",
      "name": "...",
      "blockType": "FunctionBlock",
      "language": "Scl",
      "interface": { "inputs": [] }
    }
  }
}
```

The server also always advertises `preview_scl_block`. It accepts the same typed block intent as `plan_create_block`, requires the authenticated `Engineer` role and `engineering.plan` scope, and applies the same deterministic planning validation and create-block policy evaluation. It generates constrained source for `Function` and `FunctionBlock` intents with `language: "Scl"`; parameter data types must be single identifier tokens. The interface can declare inputs and outputs. Each optional structured statement is an assignment whose target must be a declared output and whose source must be a declared input or output identifier. The result includes the deterministic operation plan, policy decision, and SCL source text. It does not create a transaction, start the V19 worker, call TIA Openness, or write a block. The engine records a scrubbed preview outcome.

```json
{
  "jsonrpc": "2.0",
  "id": "request-id",
  "method": "tools/call",
  "params": {
    "name": "preview_scl_block",
    "arguments": {
      "operationId": "...",
      "projectContext": { "projectId": "...", "snapshotHash": "..." },
      "idempotencyKey": "...",
      "name": "FB_Motor",
      "blockType": "FunctionBlock",
      "language": "Scl",
      "interface": {
        "inputs": [{ "name": "Start", "dataType": "Bool" }],
        "outputs": [{ "name": "Running", "dataType": "Bool" }]
      },
      "statements": [{ "target": "Running", "source": "Start" }]
    }
  }
}
```

When the locally managed `TiaV19Worker:Enabled` setting is `true` and its worker executable and configuration paths validate, the server additionally advertises the read-only `get_project_context` tool. It requires the authenticated `Engineer` role and `engineering.read` scope. The request contains only the configured project ID, never a project file path:

```json
{
  "jsonrpc": "2.0",
  "id": "request-id",
  "method": "tools/call",
  "params": {
    "name": "get_project_context",
    "arguments": {
      "projectId": "test-project"
    }
  }
}
```

The engine routes this call to the V19 adapter abstraction, records a scrubbed project-context read outcome, and returns only the project ID and snapshot hash. The read tool is omitted when the worker is disabled, so remote input cannot activate process execution or select executable, configuration, or project paths.

When `TiaV19Worker:Enabled` and the separate default-off `TiaV19Worker:EnableBlockCatalogRead` settings are both `true`, the server additionally advertises the read-only `get_block_catalog` tool. It also requires the authenticated `Engineer` role and `engineering.read` scope. The request contains only the configured project ID:

```json
{
  "jsonrpc": "2.0",
  "id": "request-id",
  "method": "tools/call",
  "params": {
    "name": "get_block_catalog",
    "arguments": {
      "projectId": "test-project",
      "startIndex": 0,
      "maxBlocks": 100,
      "expectedSnapshotHash": "snapshot-from-previous-page"
    }
  }
}
```

`startIndex` is optional and defaults to zero; clients continue a truncated response using its `nextStartIndex` value. Every continuation (`startIndex` greater than zero) must include `expectedSnapshotHash` from the preceding successful response. `maxBlocks` is optional, must be positive, and is capped at 500 by the Engineering Engine. The response contains the project ID, snapshot hash, `totalBlockCount`, `isTruncated`, optional `nextStartIndex`, and read-only block metadata: controller name, block name, namespace, number, and programming language. If the project snapshot has changed, the call returns an error requiring the client to restart from `startIndex: 0`; it never mixes pages from different snapshots. The engine records a scrubbed catalog-read outcome. The tool is omitted unless both deployment-controlled settings are enabled; it cannot select project, worker executable, or worker configuration paths, and it exposes no raw Siemens objects or mutation operations.

The server derives internal request and correlation IDs from the authenticated session and JSON-RPC ID. It does not accept transport identity, roles, scopes, session state, or correlation IDs from tool arguments. MCP is not permitted to bypass Engineering Engine boundaries.

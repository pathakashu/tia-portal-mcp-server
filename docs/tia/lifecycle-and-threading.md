# TIA V18 Lifecycle and Execution Boundary

Transport callbacks must never directly manipulate Openness objects.

Use a dedicated TIA execution boundary or command queue.

```text
MCP request
  -> Engineering Engine
  -> transaction
  -> TIA command queue
  -> TiaV18Adapter
  -> Openness V18
```

Writes should be serialized per TIA project.

Project changes must invalidate or update the relevant snapshot/context state.

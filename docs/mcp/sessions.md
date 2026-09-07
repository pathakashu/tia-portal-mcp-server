# MCP Session Lifecycle

```text
CONNECT
  -> AUTHENTICATE
  -> INITIALIZE
  -> READY
  -> TOOL_CALL / PROGRESS / RESULT
  -> HEARTBEAT
  -> DISCONNECT
```

Sessions must support timeout and cancellation.

Identity is derived from validated credentials/certificates, not request payload claims.

# Data Flow

1. Remote client connects through HTTPS Streamable HTTP.
2. mTLS authenticates the client.
3. MCP server validates and routes a typed request.
4. Engineering Engine resolves project context.
5. Read operations execute directly when policy permits.
6. Write operations are normalized into Engineering IR.
7. Policy and risk are evaluated.
8. A deterministic preview/diff is created.
9. Human approval is requested where required.
10. Transaction manager executes through TiaV18Adapter.
11. Adapter invokes TIA Openness V18.
12. Post-execution validation runs.
13. Audit events are persisted.
14. Structured result/progress returns through MCP.

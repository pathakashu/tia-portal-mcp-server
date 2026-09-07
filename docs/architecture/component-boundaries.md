# Component Boundaries

| Component | Owns | Must not own |
|---|---|---|
| MCP Server | transport/session/tool routing | TIA logic |
| Engineering Engine | engineering workflow | transport details |
| Engineering IR | Siemens-independent operation model | Siemens types |
| Policy | authorization/risk/approval rules | TIA API calls |
| Validation | deterministic checks | transport |
| Transactions | write lifecycle/idempotency | MCP protocol |
| Project Intelligence | model/graph/search | Siemens objects |
| TIA V19 Adapter | Siemens integration | policy/approval |
| Audit | immutable event record | engineering decisions |

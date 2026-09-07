# Engineering Policy

Policy is deterministic.

AI cannot self-approve operations.

Example defaults:

| Operation | Risk | Approval |
|---|---|---|
| Read project | Low | No |
| Read block | Low | No |
| Preview constrained SCL block source | Low | No |
| Create block | Medium | Yes |
| Modify block | Medium | Yes |
| Delete block | High | Yes |
| Hardware configuration | High | Yes |
| PLC download | Critical | Not implemented |

The policy engine must remain configurable without changing MCP handlers.

# Microsoft Foundry Integration Boundary

Microsoft Foundry is an upstream consumer of this Engineer-PC runtime.

It is intentionally outside the core Engineer-PC implementation.

The future cloud agent should call the Engineer-PC MCP server over the secure WSS/mTLS boundary.

The cloud agent must not receive or manipulate Siemens.Engineering objects.

The Engineer-PC runtime remains responsible for:

- authentication
- authorization
- policy
- approval
- validation
- transaction execution
- TIA Portal V18 interaction
- audit

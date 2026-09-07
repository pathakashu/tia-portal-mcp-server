# Security Boundary

The Engineer-PC machine is the trust boundary between cloud reasoning and the OT engineering environment.

The remote agent is never trusted with direct TIA access.

## Required protections

- HTTPS Streamable HTTP
- mutual TLS
- authenticated sessions
- role/scope authorization
- operation allow-list
- policy checks
- approval for writes
- project snapshot/hash checks
- idempotency
- timeouts
- audit trail
- no arbitrary command execution

## Default posture

Reject unsafe requests rather than attempting to interpret them.

# Security Deployment

Production engineer-PC communication should use HTTPS Streamable HTTP with mutual TLS.

Minimum controls:

- server certificate
- client certificate
- trusted CA/issuer
- certificate expiry monitoring
- scope-based authorization
- firewall/egress controls
- local audit persistence
- least privilege
- secure secret storage

Development-only insecure localhost mode must be explicit and disabled by default.

## MCP host configuration

`EngineerPc.Mcp.Host` fails closed unless production certificate settings are supplied.

Configure these values through secure deployment configuration, not source-controlled files:

- `McpTransport__ServerCertificatePath`
- `McpTransport__ServerCertificatePassword`
- `McpTransport__TrustedClientIssuers__0` and subsequent trusted issuer entries
- `McpTransport__ClientPrincipalMappings__0__CertificateThumbprint`, plus the mapped `Roles` and `Scopes` entries
- `McpTransport__Port`
- `McpAudit__FilePath` and `McpAudit__EngineeringFilePath` to place append-only local audit files outside the deployment directory

The host accepts only TLS 1.2 and TLS 1.3, requires a client certificate, and rejects untrusted, expired, unmapped, or invalid client chains. Principal mappings are local deployment configuration: they bind an exact client certificate SHA-1 thumbprint to trusted roles and scopes. Do not take roles, scopes, subjects, or thumbprints from MCP request payloads.

With a valid transport configuration and an accepted client certificate mapping, the bounded planning endpoint is available at `/mcp`. It accepts only the current allow-listed planning message and cannot execute engineering operations; execution remains unavailable until a TIA Portal V18 adapter is configured.

Each authorization decision is appended synchronously as a JSON Lines `SecurityEvent`. Planning, approval, and execution lifecycle outcomes are written separately as JSON Lines `EngineeringAuditEvent` records. The default paths are under the current user local application-data directory; configure both paths to access-controlled operational locations in production. Audit retention, backup, and tamper-evidence controls are deployment responsibilities.

For an explicit development-only localhost listener, set `McpTransport__AllowInsecureLocalhost=true`. This bypasses TLS and must not be enabled in production.

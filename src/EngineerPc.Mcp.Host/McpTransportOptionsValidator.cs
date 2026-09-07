namespace EngineerPc.Mcp.Host;

public sealed record McpTransportOptionsValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class McpTransportOptionsValidator
{
    public static McpTransportOptionsValidationResult Validate(McpTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();

        if (options.Port is < 1 or > 65535)
        {
            errors.Add("MCP transport port must be between 1 and 65535.");
        }

        if (options.MaxRequestBodySizeBytes is < 1 or > 10_485_760)
        {
            errors.Add("MCP transport message size must be between 1 and 10485760 bytes.");
        }

        if (options.RequestTimeoutSeconds is < 1 or > 300)
        {
            errors.Add("MCP transport request timeout must be between 1 and 300 seconds.");
        }

        if (options.SessionDurationSeconds is < 1 or > 3_600)
        {
            errors.Add("MCP session duration must be between 1 and 3600 seconds.");
        }

        if (!options.AllowInsecureLocalhost)
        {
            if (string.IsNullOrWhiteSpace(options.ServerCertificatePath))
            {
                errors.Add("A server certificate path is required when insecure localhost mode is disabled.");
            }

            if (options.TrustedClientIssuers.Length == 0)
            {
                errors.Add("At least one trusted client issuer is required when insecure localhost mode is disabled.");
            }

            if (options.ClientPrincipalMappings.Length == 0)
            {
                errors.Add("At least one client certificate principal mapping is required when insecure localhost mode is disabled.");
            }

            var configuredThumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in options.ClientPrincipalMappings)
            {
                if (!ClientCertificatePrincipalMapper.IsValidThumbprint(mapping.CertificateThumbprint))
                {
                    errors.Add("Each client certificate principal mapping must contain a SHA-1 certificate thumbprint.");
                }
                else if (!configuredThumbprints.Add(mapping.CertificateThumbprint))
                {
                    errors.Add("Client certificate principal mappings must not contain duplicate thumbprints.");
                }

                if (mapping.Roles.Length == 0 || mapping.Roles.Any(string.IsNullOrWhiteSpace))
                {
                    errors.Add("Each client certificate principal mapping must contain at least one role.");
                }

                if (mapping.Scopes.Length == 0 || mapping.Scopes.Any(string.IsNullOrWhiteSpace))
                {
                    errors.Add("Each client certificate principal mapping must contain at least one scope.");
                }
            }
        }

        return new McpTransportOptionsValidationResult(errors);
    }
}
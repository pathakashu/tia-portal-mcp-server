namespace EngineerPc.Mcp.Host;

public sealed record McpTransportOptions
{
    public int Port { get; init; } = 7443;

    public bool AllowInsecureLocalhost { get; init; }

    public string? ServerCertificatePath { get; init; }

    public string? ServerCertificatePassword { get; init; }

    public string[] TrustedClientIssuers { get; init; } = [];

    public int MaxRequestBodySizeBytes { get; init; } = 1_048_576;

    public int RequestTimeoutSeconds { get; init; } = 30;

    public int SessionDurationSeconds { get; init; } = 300;

    public ClientCertificatePrincipalMapping[] ClientPrincipalMappings { get; init; } = [];
}
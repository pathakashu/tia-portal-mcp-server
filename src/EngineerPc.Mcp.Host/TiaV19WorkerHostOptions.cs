using EngineerPc.Tia.V19.Client;

namespace EngineerPc.Mcp.Host;

public sealed record TiaV19WorkerHostOptions
{
    public bool Enabled { get; init; }

    public bool EnableBlockCatalogRead { get; init; }

    public bool EnableBlockWrite { get; init; }

    public string? WorkerExecutablePath { get; init; }

    public string? ConfigurationPath { get; init; }

    public int RequestTimeoutSeconds { get; init; } = 30;

    public TiaV19WorkerClientOptions ToClientOptions()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("TIA Portal V19 worker is disabled.");
        }

        return new TiaV19WorkerClientOptions(
            WorkerExecutablePath ?? string.Empty,
            ConfigurationPath ?? string.Empty,
            TimeSpan.FromSeconds(RequestTimeoutSeconds));
    }
}
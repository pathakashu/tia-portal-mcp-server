using EngineerPc.Tia.V18.Client;

namespace EngineerPc.Mcp.Host;

public sealed record TiaV18WorkerHostOptions
{
    public bool Enabled { get; init; }

    public bool EnableBlockCatalogRead { get; init; }

    public string? WorkerExecutablePath { get; init; }

    public string? ConfigurationPath { get; init; }

    public int RequestTimeoutSeconds { get; init; } = 30;

    public TiaV18WorkerClientOptions ToClientOptions()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("TIA Portal V18 worker is disabled.");
        }

        return new TiaV18WorkerClientOptions(
            WorkerExecutablePath ?? string.Empty,
            ConfigurationPath ?? string.Empty,
            TimeSpan.FromSeconds(RequestTimeoutSeconds));
    }
}
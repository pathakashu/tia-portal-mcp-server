namespace EngineerPc.Mcp.Host;

public sealed record McpAuditOptions
{
    public string FilePath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EngineerPc",
        "audit",
        "security-events.jsonl");

    public string EngineeringFilePath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EngineerPc",
        "audit",
        "engineering-events.jsonl");
}
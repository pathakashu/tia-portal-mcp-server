namespace EngineerPc.Tia.V18.Protocol;

public enum TiaV18BlockCatalogErrorCode
{
    None,
    SnapshotChanged
}

public sealed record TiaV18BlockDefinition(
    string ControllerName,
    string Name,
    string Namespace,
    int Number,
    string ProgrammingLanguage);

public sealed record TiaV18BlockCatalogResponse(
    string RequestId,
    string? ProjectId,
    string? SnapshotHash,
    IReadOnlyList<TiaV18BlockDefinition> Blocks,
    int? TotalBlockCount,
    int? NextStartIndex,
    TiaV18BlockCatalogErrorCode ErrorCode,
    string? Error)
{
    public bool IsSuccess => ProjectId is not null && SnapshotHash is not null && TotalBlockCount is not null && Error is null;
}
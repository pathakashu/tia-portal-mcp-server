namespace EngineerPc.Tia.V19.Protocol;

public enum TiaV19BlockCatalogErrorCode
{
    None,
    SnapshotChanged
}

public sealed record TiaV19BlockDefinition(
    string ControllerName,
    string Name,
    string Namespace,
    int Number,
    string ProgrammingLanguage);

public sealed record TiaV19BlockCatalogResponse(
    string RequestId,
    string? ProjectId,
    string? SnapshotHash,
    IReadOnlyList<TiaV19BlockDefinition> Blocks,
    int? TotalBlockCount,
    int? NextStartIndex,
    TiaV19BlockCatalogErrorCode ErrorCode,
    string? Error)
{
    public bool IsSuccess => ProjectId is not null && SnapshotHash is not null && TotalBlockCount is not null && Error is null;
}
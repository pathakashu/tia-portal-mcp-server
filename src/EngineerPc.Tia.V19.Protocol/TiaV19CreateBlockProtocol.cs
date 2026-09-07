namespace EngineerPc.Tia.V19.Protocol;

public enum TiaV19CreateBlockErrorCode
{
    None,
    SnapshotChanged,
    ControllerNotFound,
    BlockAlreadyExists,
    GenerationFailed
}

public sealed record TiaV19CreateBlockResponse(
    string RequestId,
    string? ProjectId,
    string? SnapshotHash,
    TiaV19CreateBlockErrorCode ErrorCode,
    string? Error)
{
    public bool IsSuccess => ProjectId is not null && SnapshotHash is not null &&
        ErrorCode == TiaV19CreateBlockErrorCode.None && Error is null;
}

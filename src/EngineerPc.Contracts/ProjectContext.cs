namespace EngineerPc.Contracts;

public sealed record ProjectContext(
    string ProjectId,
    string SnapshotHash);
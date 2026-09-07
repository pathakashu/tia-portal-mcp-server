namespace EngineerPc.ProjectModel;

public sealed record ProjectSnapshot(
    string ProjectId,
    string TiaVersion,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<ProjectArtifact> Artifacts,
    string SnapshotHash);
namespace EngineerPc.ProjectGraph;

public sealed record ProjectGraphEdge(
    string SourceArtifactId,
    ProjectGraphRelationship Relationship,
    string TargetArtifactId);

public enum ProjectGraphRelationship
{
    Contains,
    Calls,
    Reads,
    Writes,
    Uses,
    References,
    DependsOn,
    ConnectedTo,
    BindsTo
}
using EngineerPc.ProjectModel;

namespace EngineerPc.ProjectGraph;

public sealed record ProjectGraph(
    ProjectSnapshot Snapshot,
    IReadOnlyList<ProjectGraphEdge> Edges)
{
    public IReadOnlyList<ProjectGraphEdge> GetOutgoingEdges(string sourceArtifactId) =>
        Edges
            .Where(edge => string.Equals(edge.SourceArtifactId, sourceArtifactId, StringComparison.Ordinal))
            .OrderBy(edge => edge.Relationship)
            .ThenBy(edge => edge.TargetArtifactId, StringComparer.Ordinal)
            .ToArray();
}
using EngineerPc.ProjectModel;

namespace EngineerPc.ProjectGraph;

public static class ProjectGraphFactory
{
    public static ProjectGraph Create(ProjectSnapshot snapshot, IEnumerable<ProjectGraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(edges);

        var artifactIds = snapshot.Artifacts
            .Select(artifact => artifact.ArtifactId)
            .ToHashSet(StringComparer.Ordinal);
        var edgeList = edges
            .OrderBy(edge => edge.SourceArtifactId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Relationship)
            .ThenBy(edge => edge.TargetArtifactId, StringComparer.Ordinal)
            .ToArray();
        var uniqueEdges = new HashSet<ProjectGraphEdge>();

        foreach (var edge in edgeList)
        {
            if (!artifactIds.Contains(edge.SourceArtifactId) || !artifactIds.Contains(edge.TargetArtifactId))
            {
                throw new ArgumentException(
                    $"Graph edge '{edge.SourceArtifactId}' -> '{edge.TargetArtifactId}' references an artifact outside the snapshot.",
                    nameof(edges));
            }

            if (!uniqueEdges.Add(edge))
            {
                throw new ArgumentException(
                    $"Graph edge '{edge.SourceArtifactId}' -> '{edge.TargetArtifactId}' is duplicated.",
                    nameof(edges));
            }
        }

        return new ProjectGraph(snapshot, edgeList);
    }
}
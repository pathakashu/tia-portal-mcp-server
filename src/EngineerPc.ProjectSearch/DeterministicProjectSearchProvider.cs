using EngineerPc.ProjectModel;

namespace EngineerPc.ProjectSearch;

public sealed class DeterministicProjectSearchProvider : IProjectSearchProvider
{
    public IReadOnlyList<ProjectArtifact> Search(ProjectSnapshot snapshot, ProjectSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);

        var term = query.Text.Trim();
        if (term.Length == 0)
        {
            return [];
        }

        return snapshot.Artifacts
            .Where(artifact => query.ArtifactType is null || artifact.Type == query.ArtifactType)
            .Where(artifact => Matches(artifact, term))
            .OrderBy(artifact => MatchRank(artifact.Name, term))
            .ThenBy(artifact => artifact.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(artifact => artifact.ArtifactId, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool Matches(ProjectArtifact artifact, string term) =>
        Contains(artifact.Name, term) ||
        Contains(artifact.Path, term) ||
        Contains(artifact.Type.ToString(), term) ||
        artifact.Metadata.Any(metadata => Contains(metadata.Key, term) || Contains(metadata.Value, term));

    private static int MatchRank(string name, string term)
    {
        if (string.Equals(name, term, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return name.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static bool Contains(string value, string term) =>
        value.Contains(term, StringComparison.OrdinalIgnoreCase);
}
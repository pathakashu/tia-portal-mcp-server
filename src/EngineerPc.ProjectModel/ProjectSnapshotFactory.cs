using System.Security.Cryptography;
using System.Text;

namespace EngineerPc.ProjectModel;

public static class ProjectSnapshotFactory
{
    public static ProjectSnapshot Create(
        string projectId,
        string tiaVersion,
        DateTimeOffset capturedAtUtc,
        IEnumerable<ProjectArtifact> artifacts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tiaVersion);
        ArgumentNullException.ThrowIfNull(artifacts);

        var artifactList = artifacts.OrderBy(artifact => artifact.ArtifactId, StringComparer.Ordinal).ToArray();
        EnsureUniqueArtifactIds(artifactList);

        return new ProjectSnapshot(
            projectId,
            tiaVersion,
            capturedAtUtc,
            artifactList,
            CalculateSnapshotHash(projectId, tiaVersion, artifactList));
    }

    private static void EnsureUniqueArtifactIds(IEnumerable<ProjectArtifact> artifacts)
    {
        var seenArtifactIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.ArtifactId))
            {
                throw new ArgumentException("Artifact ID is required.", nameof(artifacts));
            }

            if (!seenArtifactIds.Add(artifact.ArtifactId))
            {
                throw new ArgumentException($"Artifact ID '{artifact.ArtifactId}' is duplicated.", nameof(artifacts));
            }
        }
    }

    private static string CalculateSnapshotHash(
        string projectId,
        string tiaVersion,
        IEnumerable<ProjectArtifact> artifacts)
    {
        var canonical = new StringBuilder();
        AppendField(canonical, projectId);
        AppendField(canonical, tiaVersion);

        foreach (var artifact in artifacts)
        {
            AppendField(canonical, artifact.ArtifactId);
            AppendField(canonical, artifact.Name);
            AppendField(canonical, artifact.Path);
            AppendField(canonical, artifact.Type.ToString());
            AppendField(canonical, artifact.SourceHash);

            foreach (var metadata in artifact.Metadata.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                AppendField(canonical, metadata.Key);
                AppendField(canonical, metadata.Value);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendField(StringBuilder builder, string? value)
    {
        var normalizedValue = value ?? string.Empty;
        builder.Append(normalizedValue.Length);
        builder.Append(':');
        builder.Append(normalizedValue);
        builder.Append('|');
    }
}
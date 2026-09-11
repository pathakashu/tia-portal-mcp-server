using System.Security.Cryptography;
using System.Text;

namespace EngineerPc.Tia.V19.Protocol;

public sealed record TiaV19ProjectDefinition(string ProjectId, string ProjectFilePath);

public static class TiaV19WorkerProtocol
{
    public const string Version = "1.3";
    public const string GetProjectContextMethod = "get_project_context";
    public const string GetBlockCatalogMethod = "get_block_catalog";
    public const string CreateBlockMethod = "create_block";
    public const int MaximumBlockCatalogBlockCount = 500;
}

public sealed record TiaV19WorkerRequest(
    string ProtocolVersion,
    string RequestId,
    string Method,
    string? ProjectId,
    int? BlockCatalogStartIndex = null,
    int? BlockCatalogMaximumBlockCount = null,
    string? BlockCatalogExpectedSnapshotHash = null,
    string? CreateBlockControllerName = null,
    string? CreateBlockName = null,
    string? CreateBlockType = null,
    string? CreateBlockSourceText = null,
    string? CreateBlockExpectedSnapshotHash = null);

public sealed record TiaV19WorkerConfiguration(IReadOnlyList<TiaV19ProjectDefinition> Projects);

public sealed class TiaV19ProjectCatalog
{
    private readonly IReadOnlyDictionary<string, TiaV19ProjectDefinition> projects;

    public TiaV19ProjectCatalog(IEnumerable<TiaV19ProjectDefinition> projects)
    {
        if (projects is null)
        {
            throw new ArgumentNullException(nameof(projects));
        }

        var catalog = new Dictionary<string, TiaV19ProjectDefinition>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            if (string.IsNullOrWhiteSpace(project.ProjectId))
            {
                throw new ArgumentException("TIA V19 project ID is required.", nameof(projects));
            }

            if (string.IsNullOrWhiteSpace(project.ProjectFilePath) || !IsAbsolutePath(project.ProjectFilePath))
            {
                throw new ArgumentException("TIA V19 project file path must be absolute.", nameof(projects));
            }

            if (catalog.ContainsKey(project.ProjectId))
            {
                throw new ArgumentException("TIA V19 project IDs must be unique.", nameof(projects));
            }

            catalog.Add(project.ProjectId, project);
        }

        this.projects = catalog;
    }

    public bool TryGetProject(string projectId, out TiaV19ProjectDefinition? project)
    {
        ThrowIfNullOrWhiteSpace(projectId, nameof(projectId));
        return projects.TryGetValue(projectId, out project);
    }

    private static bool IsAbsolutePath(string path) =>
        Path.IsPathRooted(path) && string.Equals(
            Path.GetFullPath(path),
            path,
            StringComparison.OrdinalIgnoreCase);

    private static void ThrowIfNullOrWhiteSpace(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }
    }
}

public sealed record TiaV19ProjectContextResponse(
    string RequestId,
    string? ProjectId,
    string? SnapshotHash,
    string? Error)
{
    public bool IsSuccess => ProjectId is not null && SnapshotHash is not null && Error is null;
}

public static class TiaV19ProjectSnapshot
{
    /// <remarks>
    /// <c>Project.Size</c> is deliberately excluded. TIA Portal appends a log entry every time
    /// Openness opens a project, so the reported size grows on each open even when no
    /// engineering content changed. Including it made the snapshot hash differ on every read,
    /// which permanently broke catalog continuation and made every approved write fail its
    /// stale-context check. <c>LastModified</c> is the stable "has this project changed" signal.
    /// </remarks>
    public static string Calculate(
        string projectId,
        string projectName,
        string projectFilePath,
        DateTime lastModifiedUtc,
        string version)
    {
        ThrowIfNullOrWhiteSpace(projectId, nameof(projectId));
        ThrowIfNullOrWhiteSpace(projectName, nameof(projectName));
        ThrowIfNullOrWhiteSpace(projectFilePath, nameof(projectFilePath));
        if (version is null)
        {
            throw new ArgumentNullException(nameof(version));
        }

        var canonical = string.Join("\n", new[]
        {
            Encode(projectId),
            Encode(projectName),
            Encode(projectFilePath),
            Encode(lastModifiedUtc.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Encode(version)
        });
        using var algorithm = SHA256.Create();
        var hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return BitConverter.ToString(hash).Replace("-", string.Empty);
    }

    private static string Encode(string value) => $"{value.Length}:{value}";

    private static void ThrowIfNullOrWhiteSpace(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }
    }
}
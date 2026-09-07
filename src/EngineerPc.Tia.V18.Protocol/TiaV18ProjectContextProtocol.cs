using System.Security.Cryptography;
using System.Text;

namespace EngineerPc.Tia.V18.Protocol;

public sealed record TiaV18ProjectDefinition(string ProjectId, string ProjectFilePath);

public static class TiaV18WorkerProtocol
{
    public const string Version = "1.2";
    public const string GetProjectContextMethod = "get_project_context";
    public const string GetBlockCatalogMethod = "get_block_catalog";
    public const int MaximumBlockCatalogBlockCount = 500;
}

public sealed record TiaV18WorkerRequest(
    string ProtocolVersion,
    string RequestId,
    string Method,
    string? ProjectId,
    int? BlockCatalogStartIndex = null,
    int? BlockCatalogMaximumBlockCount = null,
    string? BlockCatalogExpectedSnapshotHash = null);

public sealed record TiaV18WorkerConfiguration(IReadOnlyList<TiaV18ProjectDefinition> Projects);

public sealed class TiaV18ProjectCatalog
{
    private readonly IReadOnlyDictionary<string, TiaV18ProjectDefinition> projects;

    public TiaV18ProjectCatalog(IEnumerable<TiaV18ProjectDefinition> projects)
    {
        if (projects is null)
        {
            throw new ArgumentNullException(nameof(projects));
        }

        var catalog = new Dictionary<string, TiaV18ProjectDefinition>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            if (string.IsNullOrWhiteSpace(project.ProjectId))
            {
                throw new ArgumentException("TIA V18 project ID is required.", nameof(projects));
            }

            if (string.IsNullOrWhiteSpace(project.ProjectFilePath) || !IsAbsolutePath(project.ProjectFilePath))
            {
                throw new ArgumentException("TIA V18 project file path must be absolute.", nameof(projects));
            }

            if (catalog.ContainsKey(project.ProjectId))
            {
                throw new ArgumentException("TIA V18 project IDs must be unique.", nameof(projects));
            }

            catalog.Add(project.ProjectId, project);
        }

        this.projects = catalog;
    }

    public bool TryGetProject(string projectId, out TiaV18ProjectDefinition? project)
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

public sealed record TiaV18ProjectContextResponse(
    string RequestId,
    string? ProjectId,
    string? SnapshotHash,
    string? Error)
{
    public bool IsSuccess => ProjectId is not null && SnapshotHash is not null && Error is null;
}

public static class TiaV18ProjectSnapshot
{
    public static string Calculate(
        string projectId,
        string projectName,
        string projectFilePath,
        DateTime lastModifiedUtc,
        long size,
        string version)
    {
        ThrowIfNullOrWhiteSpace(projectId, nameof(projectId));
        ThrowIfNullOrWhiteSpace(projectName, nameof(projectName));
        ThrowIfNullOrWhiteSpace(projectFilePath, nameof(projectFilePath));
        ThrowIfNullOrWhiteSpace(version, nameof(version));

        var canonical = string.Join("\n", new[]
        {
            Encode(projectId),
            Encode(projectName),
            Encode(projectFilePath),
            Encode(lastModifiedUtc.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Encode(size.ToString(System.Globalization.CultureInfo.InvariantCulture)),
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
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EngineerPc.Contracts;
using EngineerPc.Engineering.Ir;
using EngineerPc.Tia.Abstractions;

namespace EngineerPc.Tia.Mock;

public sealed class MockTiaAdapter : ITiaAdapter
{
    private readonly ConcurrentDictionary<string, MockProject> projects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> projectWriteLocks = new(StringComparer.Ordinal);

    public MockTiaAdapter(IEnumerable<ProjectContext>? initialProjects = null)
    {
        foreach (var projectContext in initialProjects ?? [])
        {
            projects.TryAdd(projectContext.ProjectId, new MockProject(projectContext.SnapshotHash));
        }
    }

    public Task<ProjectContext?> GetProjectContextAsync(string projectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(projects.TryGetValue(projectId, out var project)
            ? new ProjectContext(projectId, project.SnapshotHash)
            : null);
    }

    public async Task<TiaAdapterExecutionResult> CreateBlockAsync(
        CreateBlockOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var writeLock = projectWriteLocks.GetOrAdd(operation.ProjectContext.ProjectId, _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync(cancellationToken);

        try
        {
            var project = projects.GetOrAdd(
                operation.ProjectContext.ProjectId,
                _ => new MockProject(operation.ProjectContext.SnapshotHash));

            if (!string.Equals(project.SnapshotHash, operation.ProjectContext.SnapshotHash, StringComparison.Ordinal))
            {
                return new TiaAdapterExecutionResult(null, ["Project snapshot is stale."]);
            }

            if (!project.BlockNames.Add(operation.Name))
            {
                return new TiaAdapterExecutionResult(null, [$"Block '{operation.Name}' already exists."]);
            }

            project.SnapshotHash = CalculateSnapshotHash(operation.ProjectContext.ProjectId, project.BlockNames);
            return new TiaAdapterExecutionResult(
                new ProjectContext(operation.ProjectContext.ProjectId, project.SnapshotHash),
                []);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static string CalculateSnapshotHash(string projectId, IEnumerable<string> blockNames)
    {
        var canonicalProject = $"{projectId}\n{string.Join("\n", blockNames.OrderBy(name => name, StringComparer.Ordinal))}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalProject)));
    }

    private sealed class MockProject(string snapshotHash)
    {
        public string SnapshotHash { get; set; } = snapshotHash;

        public HashSet<string> BlockNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
using EngineerPc.Contracts;

namespace EngineerPc.Tia.Abstractions;

public interface IProjectBlockCatalogReader
{
    Task<ProjectBlockCatalogPage?> GetBlockCatalogPageAsync(
        string projectId,
        int startIndex,
        int maximumBlockCount,
        string? expectedSnapshotHash,
        CancellationToken cancellationToken);
}

public sealed class ProjectBlockCatalogSnapshotChangedException : Exception
{
    public ProjectBlockCatalogSnapshotChangedException()
        : base("PLC block catalog snapshot has changed.")
    {
    }
}

public sealed record ProjectBlockCatalogPage(
    ProjectContext ProjectContext,
    IReadOnlyList<ProjectBlock> Blocks,
    int TotalBlockCount,
    int? NextStartIndex);

public sealed record ProjectBlock(
    string ControllerName,
    string Name,
    string Namespace,
    int Number,
    string ProgrammingLanguage);
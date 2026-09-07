using EngineerPc.Contracts;
using EngineerPc.Tia.Abstractions;

namespace EngineerPc.Engineering.Engine;

public interface IProjectBlockCatalogReadService
{
    Task<ProjectBlockCatalogReadResult> GetBlockCatalogAsync(
        string projectId,
        int startIndex,
        int maximumBlockCount,
        string? expectedSnapshotHash,
        AuthenticatedIdentity identity,
        Guid operationId,
        CancellationToken cancellationToken);
}

public sealed record ProjectBlockCatalogReadResult(
    ProjectBlockCatalogPage? BlockCatalog,
    int TotalBlockCount,
    bool IsTruncated,
    IReadOnlyList<string> Errors,
    int? NextStartIndex = null)
{
    public ProjectBlockCatalogReadResult(
        ProjectBlockCatalogPage? blockCatalog,
        IReadOnlyList<string> errors)
        : this(
            blockCatalog,
            blockCatalog?.TotalBlockCount ?? 0,
            blockCatalog?.NextStartIndex is not null,
            errors,
            blockCatalog?.NextStartIndex)
    {
    }

    public bool IsSuccess => BlockCatalog is not null && Errors.Count == 0;
}
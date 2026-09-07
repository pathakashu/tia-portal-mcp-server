using EngineerPc.Audit;
using EngineerPc.Contracts;
using EngineerPc.Tia.Abstractions;

namespace EngineerPc.Engineering.Engine;

public sealed class ProjectBlockCatalogReadService : IProjectBlockCatalogReadService
{
    public const int MaximumBlockCount = 500;
    private const string UnavailableSnapshotHash = "unavailable";
    private readonly IProjectBlockCatalogReader? blockCatalogReader;
    private readonly IEngineeringAuditSink auditSink;
    private readonly TimeProvider timeProvider;

    public ProjectBlockCatalogReadService(
        IProjectBlockCatalogReader? blockCatalogReader,
        IEngineeringAuditSink? auditSink = null,
        TimeProvider? timeProvider = null)
    {
        this.blockCatalogReader = blockCatalogReader;
        this.auditSink = auditSink ?? NullEngineeringAuditSink.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProjectBlockCatalogReadResult> GetBlockCatalogAsync(
        string projectId,
        int startIndex,
        int maximumBlockCount,
        string? expectedSnapshotHash,
        AuthenticatedIdentity identity,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Operation ID is required.", nameof(operationId));
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            return Reject(operationId, "unknown", identity, "PLC block catalog read requires a project ID.", "Project ID is required.");
        }

        if (maximumBlockCount <= 0)
        {
            return Reject(operationId, projectId, identity, "PLC block catalog read requires a positive block limit.", "Block limit must be positive.");
        }

        if (startIndex < 0)
        {
            return Reject(operationId, projectId, identity, "PLC block catalog read requires a non-negative start index.", "Block catalog start index must be non-negative.");
        }

        if (startIndex > 0 && string.IsNullOrWhiteSpace(expectedSnapshotHash))
        {
            return Reject(operationId, projectId, identity, "PLC block catalog continuation requires an expected snapshot hash.", "Expected snapshot hash is required for catalog continuation.");
        }

        if (blockCatalogReader is null)
        {
            return Reject(operationId, projectId, identity, "PLC block catalog reader is not configured.", "PLC block catalog reader is not configured.");
        }

        try
        {
            var effectiveBlockCount = Math.Min(maximumBlockCount, MaximumBlockCount);
            var blockCatalog = await blockCatalogReader.GetBlockCatalogPageAsync(
                projectId,
                startIndex,
                effectiveBlockCount,
                expectedSnapshotHash,
                cancellationToken);
            if (blockCatalog is null)
            {
                return Reject(operationId, projectId, identity, "Configured PLC block catalog was not found.", "Configured PLC block catalog was not found.");
            }

            Record(
                EngineeringAuditEventType.BlockCatalogReadSucceeded,
                operationId,
                blockCatalog.ProjectContext.ProjectId,
                blockCatalog.ProjectContext.SnapshotHash,
                identity,
                $"PLC block catalog was read ({blockCatalog.Blocks.Count} of {blockCatalog.TotalBlockCount} blocks).");
            return new ProjectBlockCatalogReadResult(
                blockCatalog,
                blockCatalog.TotalBlockCount,
                blockCatalog.NextStartIndex is not null,
                [],
                blockCatalog.NextStartIndex);
        }
        catch (ProjectBlockCatalogSnapshotChangedException)
        {
            return Reject(
                operationId,
                projectId,
                identity,
                "PLC block catalog snapshot changed before continuation.",
                "PLC block catalog snapshot has changed. Restart the catalog read.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Record(
                EngineeringAuditEventType.BlockCatalogReadFailed,
                operationId,
                projectId,
                UnavailableSnapshotHash,
                identity,
                "PLC block catalog read was canceled.");
            throw;
        }
        catch (Exception exception)
        {
            Record(
                EngineeringAuditEventType.BlockCatalogReadFailed,
                operationId,
                projectId,
                UnavailableSnapshotHash,
                identity,
                $"PLC block catalog read failed: {exception.GetType().Name}.");
            return new ProjectBlockCatalogReadResult(null, ["PLC block catalog read failed."]);
        }
    }

    private ProjectBlockCatalogReadResult Reject(
        Guid operationId,
        string projectId,
        AuthenticatedIdentity identity,
        string detail,
        string error)
    {
        Record(
            EngineeringAuditEventType.BlockCatalogReadRejected,
            operationId,
            projectId,
            UnavailableSnapshotHash,
            identity,
            detail);
        return new ProjectBlockCatalogReadResult(null, [error]);
    }

    private void Record(
        EngineeringAuditEventType eventType,
        Guid operationId,
        string projectId,
        string snapshotHash,
        AuthenticatedIdentity identity,
        string detail) => auditSink.Record(new EngineeringAuditEvent(
            eventType,
            timeProvider.GetUtcNow(),
            operationId,
            null,
            projectId,
            snapshotHash,
            identity,
            detail));
}
using EngineerPc.Audit;
using EngineerPc.Contracts;
using EngineerPc.Engineering.Engine;
using EngineerPc.Tia.Abstractions;

namespace EngineerPc.Engineering.Engine.Tests;

public sealed class ProjectBlockCatalogReadServiceTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedIdentity Identity = new("engineer-1", "client-1");

    [Fact]
    public async Task GetBlockCatalogAsync_WithConfiguredCatalog_ReturnsCatalogAndAuditsSuccess()
    {
        var auditSink = new InMemoryEngineeringAuditSink();
        var catalog = new ProjectBlockCatalogPage(
            new ProjectContext("project-1", "snapshot-1"),
            [
                new ProjectBlock("PLC_1", "FB_Motor", string.Empty, 1, "Scl"),
                new ProjectBlock("PLC_1", "FC_Motor", string.Empty, 2, "Scl")
            ],
            2,
            null);
        var service = new ProjectBlockCatalogReadService(
            new StaticBlockCatalogReader(catalog),
            auditSink,
            new TestTimeProvider(NowUtc));

        var result = await service.GetBlockCatalogAsync("project-1", 0, 10, null, Identity, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(catalog, result.BlockCatalog);
        var auditEvent = Assert.Single(auditSink.Events);
        Assert.Equal(EngineeringAuditEventType.BlockCatalogReadSucceeded, auditEvent.EventType);
        Assert.Equal(Identity, auditEvent.Identity);
        Assert.Equal(NowUtc, auditEvent.OccurredAtUtc);
    }

    [Fact]
    public async Task GetBlockCatalogAsync_WithUnavailableCatalog_ReturnsErrorAndAuditsRejection()
    {
        var auditSink = new InMemoryEngineeringAuditSink();
        var service = new ProjectBlockCatalogReadService(new StaticBlockCatalogReader(null), auditSink);

        var result = await service.GetBlockCatalogAsync("unknown", 0, 10, null, Identity, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Configured PLC block catalog was not found.", result.Errors);
        Assert.Equal(EngineeringAuditEventType.BlockCatalogReadRejected, Assert.Single(auditSink.Events).EventType);
    }

    [Fact]
    public async Task GetBlockCatalogAsync_WhenReaderFails_ReturnsScrubbedErrorAndAuditsFailure()
    {
        var auditSink = new InMemoryEngineeringAuditSink();
        var service = new ProjectBlockCatalogReadService(new FailingBlockCatalogReader(), auditSink);

        var result = await service.GetBlockCatalogAsync("project-1", 0, 10, null, Identity, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("PLC block catalog read failed.", result.Errors);
        var auditEvent = Assert.Single(auditSink.Events);
        Assert.Equal(EngineeringAuditEventType.BlockCatalogReadFailed, auditEvent.EventType);
        Assert.Equal("PLC block catalog read failed: InvalidOperationException.", auditEvent.Detail);
    }

    [Fact]
    public async Task GetBlockCatalogAsync_WithContinuation_ReturnsPagedCatalog()
    {
        var catalog = new ProjectBlockCatalogPage(
            new ProjectContext("project-1", "snapshot-1"),
            [new ProjectBlock("PLC_1", "FB_Motor", string.Empty, 1, "Scl")],
            2,
            1);
        var service = new ProjectBlockCatalogReadService(new StaticBlockCatalogReader(catalog));

        var result = await service.GetBlockCatalogAsync("project-1", 0, 1, null, Identity, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.TotalBlockCount);
        Assert.True(result.IsTruncated);
        Assert.Equal(1, result.NextStartIndex);
        Assert.Equal(["FB_Motor"], result.BlockCatalog?.Blocks.Select(block => block.Name));
    }

    [Fact]
    public async Task GetBlockCatalogAsync_WithNonPositiveLimit_RejectsRequest()
    {
        var auditSink = new InMemoryEngineeringAuditSink();
        var service = new ProjectBlockCatalogReadService(new StaticBlockCatalogReader(null), auditSink);

        var result = await service.GetBlockCatalogAsync("project-1", 0, 0, null, Identity, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Block limit must be positive.", result.Errors);
        Assert.Equal(EngineeringAuditEventType.BlockCatalogReadRejected, Assert.Single(auditSink.Events).EventType);
    }

    [Fact]
    public async Task GetBlockCatalogAsync_WithContinuationWithoutSnapshotHash_RejectsRequest()
    {
        var auditSink = new InMemoryEngineeringAuditSink();
        var service = new ProjectBlockCatalogReadService(new StaticBlockCatalogReader(null), auditSink);

        var result = await service.GetBlockCatalogAsync("project-1", 1, 10, null, Identity, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Expected snapshot hash is required for catalog continuation.", result.Errors);
        Assert.Equal(EngineeringAuditEventType.BlockCatalogReadRejected, Assert.Single(auditSink.Events).EventType);
    }

    [Fact]
    public async Task GetBlockCatalogAsync_WhenSnapshotChanges_ReturnsRestartRequiredError()
    {
        var auditSink = new InMemoryEngineeringAuditSink();
        var service = new ProjectBlockCatalogReadService(new SnapshotChangedBlockCatalogReader(), auditSink);

        var result = await service.GetBlockCatalogAsync("project-1", 1, 10, "snapshot-1", Identity, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("PLC block catalog snapshot has changed. Restart the catalog read.", result.Errors);
        Assert.Equal(EngineeringAuditEventType.BlockCatalogReadRejected, Assert.Single(auditSink.Events).EventType);
    }

    private sealed class StaticBlockCatalogReader(ProjectBlockCatalogPage? catalog) : IProjectBlockCatalogReader
    {
        public Task<ProjectBlockCatalogPage?> GetBlockCatalogPageAsync(
            string projectId,
            int startIndex,
            int maximumBlockCount,
            string? expectedSnapshotHash,
            CancellationToken cancellationToken) =>
            Task.FromResult(catalog);
    }

    private sealed class FailingBlockCatalogReader : IProjectBlockCatalogReader
    {
        public Task<ProjectBlockCatalogPage?> GetBlockCatalogPageAsync(
            string projectId,
            int startIndex,
            int maximumBlockCount,
                string? expectedSnapshotHash,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Worker diagnostics must not escape the audit boundary.");
    }

            private sealed class SnapshotChangedBlockCatalogReader : IProjectBlockCatalogReader
            {
            public Task<ProjectBlockCatalogPage?> GetBlockCatalogPageAsync(
                string projectId,
                int startIndex,
                int maximumBlockCount,
                string? expectedSnapshotHash,
                CancellationToken cancellationToken) => throw new ProjectBlockCatalogSnapshotChangedException();
            }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
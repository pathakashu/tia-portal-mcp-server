using EngineerPc.Audit;
using EngineerPc.Contracts;
using EngineerPc.Engineering.Ir;
using EngineerPc.Tia.Abstractions;
using EngineerPc.Tia.Mock;

namespace EngineerPc.Engineering.Engine.Tests;

public sealed class ProjectContextReadServiceTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedIdentity Identity = new("engineer-1", "client-1");

    [Fact]
    public async Task GetProjectContextAsync_WithConfiguredProject_ReturnsContextAndAuditsSuccess()
    {
        var auditSink = new InMemoryEngineeringAuditSink();
        var service = new ProjectContextReadService(
            new MockTiaAdapter([new ProjectContext("project-1", "snapshot-1")]),
            auditSink,
            new TestTimeProvider(NowUtc));

        var result = await service.GetProjectContextAsync("project-1", Identity, Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new ProjectContext("project-1", "snapshot-1"), result.ProjectContext);
        var auditEvent = Assert.Single(auditSink.Events);
        Assert.Equal(EngineeringAuditEventType.ProjectContextReadSucceeded, auditEvent.EventType);
        Assert.Equal(Identity, auditEvent.Identity);
        Assert.Equal(NowUtc, auditEvent.OccurredAtUtc);
    }

    [Fact]
    public async Task GetProjectContextAsync_WithUnknownProject_ReturnsErrorAndAuditsRejection()
    {
        var auditSink = new InMemoryEngineeringAuditSink();
        var service = new ProjectContextReadService(new MockTiaAdapter(), auditSink);

        var result = await service.GetProjectContextAsync("unknown", Identity, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Configured project context was not found.", result.Errors);
        Assert.Equal(EngineeringAuditEventType.ProjectContextReadRejected, Assert.Single(auditSink.Events).EventType);
    }

    [Fact]
    public async Task GetProjectContextAsync_WhenAdapterFails_ReturnsScrubbedErrorAndAuditsFailure()
    {
        var auditSink = new InMemoryEngineeringAuditSink();
        var service = new ProjectContextReadService(new FailingTiaAdapter(), auditSink);

        var result = await service.GetProjectContextAsync("project-1", Identity, Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Project context read failed.", result.Errors);
        var auditEvent = Assert.Single(auditSink.Events);
        Assert.Equal(EngineeringAuditEventType.ProjectContextReadFailed, auditEvent.EventType);
        Assert.Equal("Project context read failed: InvalidOperationException.", auditEvent.Detail);
    }

    private sealed class FailingTiaAdapter : ITiaAdapter
    {
        public Task<ProjectContext?> GetProjectContextAsync(string projectId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Worker diagnostics must not escape the audit boundary.");

        public Task<TiaAdapterExecutionResult> CreateBlockAsync(
            CreateBlockOperation operation,
            CancellationToken cancellationToken,
            string? sclSourceText = null) => throw new NotSupportedException();
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
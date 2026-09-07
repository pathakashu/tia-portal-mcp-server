using EngineerPc.Audit;
using EngineerPc.Contracts;
using EngineerPc.Tia.Abstractions;

namespace EngineerPc.Engineering.Engine;

public sealed class ProjectContextReadService : IProjectContextReadService
{
    private const string UnavailableSnapshotHash = "unavailable";
    private readonly ITiaAdapter tiaAdapter;
    private readonly IEngineeringAuditSink auditSink;
    private readonly TimeProvider timeProvider;

    public ProjectContextReadService(
        ITiaAdapter tiaAdapter,
        IEngineeringAuditSink? auditSink = null,
        TimeProvider? timeProvider = null)
    {
        this.tiaAdapter = tiaAdapter ?? throw new ArgumentNullException(nameof(tiaAdapter));
        this.auditSink = auditSink ?? NullEngineeringAuditSink.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProjectContextReadResult> GetProjectContextAsync(
        string projectId,
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
            Record(
                EngineeringAuditEventType.ProjectContextReadRejected,
                operationId,
                "unknown",
                UnavailableSnapshotHash,
                identity,
                "Project context read requires a project ID.");
            return new ProjectContextReadResult(null, ["Project ID is required."]);
        }

        try
        {
            var projectContext = await tiaAdapter.GetProjectContextAsync(projectId, cancellationToken);
            if (projectContext is null)
            {
                Record(
                    EngineeringAuditEventType.ProjectContextReadRejected,
                    operationId,
                    projectId,
                    UnavailableSnapshotHash,
                    identity,
                    "Configured project context was not found.");
                return new ProjectContextReadResult(null, ["Configured project context was not found."]);
            }

            Record(
                EngineeringAuditEventType.ProjectContextReadSucceeded,
                operationId,
                projectContext.ProjectId,
                projectContext.SnapshotHash,
                identity,
                "Project context was read.");
            return new ProjectContextReadResult(projectContext, []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Record(
                EngineeringAuditEventType.ProjectContextReadFailed,
                operationId,
                projectId,
                UnavailableSnapshotHash,
                identity,
                "Project context read was canceled.");
            throw;
        }
        catch (Exception exception)
        {
            Record(
                EngineeringAuditEventType.ProjectContextReadFailed,
                operationId,
                projectId,
                UnavailableSnapshotHash,
                identity,
                $"Project context read failed: {exception.GetType().Name}.");
            return new ProjectContextReadResult(null, ["Project context read failed."]);
        }
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
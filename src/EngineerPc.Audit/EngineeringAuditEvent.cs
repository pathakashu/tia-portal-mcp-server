using EngineerPc.Contracts;

namespace EngineerPc.Audit;

public sealed record EngineeringAuditEvent(
    EngineeringAuditEventType EventType,
    DateTimeOffset OccurredAtUtc,
    Guid OperationId,
    Guid? TransactionId,
    string ProjectId,
    string ProjectSnapshotHash,
    AuthenticatedIdentity? Identity,
    string Detail);

public enum EngineeringAuditEventType
{
    BlockCatalogReadSucceeded,
    BlockCatalogReadRejected,
    BlockCatalogReadFailed,
    ProjectContextReadSucceeded,
    ProjectContextReadRejected,
    ProjectContextReadFailed,
    SclPreviewGenerated,
    SclPreviewRejected,
    PlanAwaitingApproval,
    PlanRejected,
    ApprovalGranted,
    ApprovalRejected,
    ExecutionStarted,
    ExecutionRejected,
    ExecutionFailed,
    ExecutionCommitted
}
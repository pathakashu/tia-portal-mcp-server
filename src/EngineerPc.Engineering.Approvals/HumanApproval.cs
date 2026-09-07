using EngineerPc.Contracts;

namespace EngineerPc.Engineering.Approvals;

public sealed record HumanApproval(
    Guid ApprovalId,
    Guid TransactionId,
    string OperationHash,
    string ProjectSnapshotHash,
    AuthenticatedIdentity Approver,
    DateTimeOffset ExpiresAtUtc);
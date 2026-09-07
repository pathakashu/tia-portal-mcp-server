using EngineerPc.Contracts;
using EngineerPc.Engineering.Approvals;
using EngineerPc.Engineering.Ir;
using EngineerPc.Engineering.Transactions;

namespace EngineerPc.Engineering.Engine;

public interface ICreateBlockWorkflow
{
    CreateBlockSubmissionResult Submit(CreateBlockOperation operation, DateTimeOffset nowUtc);

    ApprovalResult Approve(
        EngineeringTransaction transaction,
        HumanApproval approval,
        AuthenticatedIdentity authenticatedIdentity,
        DateTimeOffset nowUtc);

    Task<CreateBlockExecutionResult> ExecuteAsync(
        EngineeringTransaction transaction,
        CreateBlockOperation operation,
        CancellationToken cancellationToken);
}
using EngineerPc.Contracts;
using EngineerPc.Engineering.Policy;
using EngineerPc.Engineering.Transactions;

namespace EngineerPc.Engineering.Engine;

public sealed record CreateBlockSubmissionResult(
    EngineeringOperationPlan? Plan,
    PolicyDecision? PolicyDecision,
    EngineeringTransaction? Transaction,
    IReadOnlyList<string> Errors)
{
    public bool IsAwaitingApproval => Transaction?.State == TransactionState.AwaitingApproval && Errors.Count == 0;
}

public sealed record CreateBlockExecutionResult(
    EngineeringTransaction Transaction,
    ProjectContext? UpdatedProjectContext,
    IReadOnlyList<string> Errors)
{
    public bool IsCommitted => Transaction.State == TransactionState.Committed && Errors.Count == 0;
}
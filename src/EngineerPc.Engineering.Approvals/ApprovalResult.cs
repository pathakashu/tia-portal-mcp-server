using EngineerPc.Engineering.Transactions;

namespace EngineerPc.Engineering.Approvals;

public sealed record ApprovalResult(
    EngineeringTransaction? Transaction,
    IReadOnlyList<string> Errors)
{
    public bool IsApproved => Transaction?.State == TransactionState.Approved && Errors.Count == 0;
}
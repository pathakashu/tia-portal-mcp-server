using EngineerPc.Contracts;
using EngineerPc.Engineering.Transactions;

namespace EngineerPc.Engineering.Approvals;

public sealed class ApprovalService : IApprovalService
{
    public ApprovalResult Approve(
        EngineeringTransaction transaction,
        HumanApproval approval,
        AuthenticatedIdentity authenticatedIdentity,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(authenticatedIdentity);

        var errors = new List<string>();

        if (transaction.State != TransactionState.AwaitingApproval)
        {
            errors.Add("Transaction is not awaiting approval.");
        }

        if (approval.ApprovalId == Guid.Empty)
        {
            errors.Add("Approval ID is required.");
        }

        if (approval.TransactionId != transaction.TransactionId)
        {
            errors.Add("Approval transaction ID does not match the transaction.");
        }

        if (!string.Equals(approval.OperationHash, transaction.OperationHash, StringComparison.Ordinal))
        {
            errors.Add("Approval operation hash does not match the transaction.");
        }

        if (!string.Equals(approval.ProjectSnapshotHash, transaction.ProjectContext.SnapshotHash, StringComparison.Ordinal))
        {
            errors.Add("Approval project snapshot hash does not match the transaction.");
        }

        if (approval.Approver != authenticatedIdentity)
        {
            errors.Add("Approval identity does not match the authenticated identity.");
        }

        if (approval.ExpiresAtUtc <= nowUtc)
        {
            errors.Add("Approval has expired.");
        }

        if (errors.Count > 0)
        {
            return new ApprovalResult(null, errors);
        }

        return new ApprovalResult(transaction with { State = TransactionState.Approved }, []);
    }
}
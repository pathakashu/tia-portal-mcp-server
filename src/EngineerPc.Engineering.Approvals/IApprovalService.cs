using EngineerPc.Contracts;
using EngineerPc.Engineering.Transactions;

namespace EngineerPc.Engineering.Approvals;

public interface IApprovalService
{
    ApprovalResult Approve(
        EngineeringTransaction transaction,
        HumanApproval approval,
        AuthenticatedIdentity authenticatedIdentity,
        DateTimeOffset nowUtc);
}
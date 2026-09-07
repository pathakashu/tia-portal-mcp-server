using EngineerPc.Contracts;
using EngineerPc.Engineering.Transactions;

namespace EngineerPc.Engineering.Approvals.Tests;

public sealed class ApprovalServiceTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedIdentity Identity = new("engineer-1", "client-1");

    [Fact]
    public void Approve_WithMatchingValidHumanApproval_ApprovesTransaction()
    {
        var transaction = CreateAwaitingApprovalTransaction();
        var service = new ApprovalService();

        var result = service.Approve(transaction, CreateApproval(transaction), Identity, NowUtc);

        Assert.True(result.IsApproved);
        Assert.Equal(TransactionState.Approved, result.Transaction?.State);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Approve_WithMismatchedOperationHash_ReturnsErrorWithoutTransaction()
    {
        var transaction = CreateAwaitingApprovalTransaction();
        var service = new ApprovalService();
        var approval = CreateApproval(transaction) with { OperationHash = "other-operation-sha256" };

        var result = service.Approve(transaction, approval, Identity, NowUtc);

        Assert.False(result.IsApproved);
        Assert.Null(result.Transaction);
        Assert.Contains("Approval operation hash does not match the transaction.", result.Errors);
    }

    [Fact]
    public void Approve_WithExpiredApproval_ReturnsErrorWithoutTransaction()
    {
        var transaction = CreateAwaitingApprovalTransaction();
        var service = new ApprovalService();
        var approval = CreateApproval(transaction) with { ExpiresAtUtc = NowUtc };

        var result = service.Approve(transaction, approval, Identity, NowUtc);

        Assert.False(result.IsApproved);
        Assert.Null(result.Transaction);
        Assert.Contains("Approval has expired.", result.Errors);
    }

    private static EngineeringTransaction CreateAwaitingApprovalTransaction() => EngineeringTransaction.Create(
        Guid.Parse("a5b9a005-26bc-4151-a45c-902a9338dc5a"),
        "operation-sha256",
        new ProjectContext("project-1", "snapshot-sha256"),
        "request-1",
        NowUtc) with { State = TransactionState.AwaitingApproval };

    private static HumanApproval CreateApproval(EngineeringTransaction transaction) => new(
        Guid.NewGuid(),
        transaction.TransactionId,
        transaction.OperationHash,
        transaction.ProjectContext.SnapshotHash,
        Identity,
        NowUtc.AddMinutes(5));
}
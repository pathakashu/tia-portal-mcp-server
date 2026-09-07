using EngineerPc.Contracts;

namespace EngineerPc.Engineering.Transactions.Tests;

public sealed class TransactionStateMachineTests
{
    [Fact]
    public void Transition_ToAwaitingApproval_AllowsOnlyPreApprovalLifecycleStages()
    {
        var transaction = EngineeringTransaction.Create(
            Guid.NewGuid(),
            "operation-sha256",
            new ProjectContext("project-1", "snapshot-sha256"),
            "request-1",
            DateTimeOffset.UtcNow);
        var stateMachine = new TransactionStateMachine();

        transaction = stateMachine.Transition(transaction, TransactionState.Validating);
        transaction = stateMachine.Transition(transaction, TransactionState.AwaitingApproval);

        Assert.Equal(TransactionState.AwaitingApproval, transaction.State);
    }

    [Fact]
    public void Transition_FromAwaitingApprovalToApproved_Throws()
    {
        var transaction = EngineeringTransaction.Create(
            Guid.NewGuid(),
            "operation-sha256",
            new ProjectContext("project-1", "snapshot-sha256"),
            "request-1",
            DateTimeOffset.UtcNow) with { State = TransactionState.AwaitingApproval };
        var stateMachine = new TransactionStateMachine();

        var exception = Assert.Throws<InvalidOperationException>(
            () => stateMachine.Transition(transaction, TransactionState.Approved));

        Assert.Equal("Transaction cannot transition from 'AwaitingApproval' to 'Approved'.", exception.Message);
    }
}
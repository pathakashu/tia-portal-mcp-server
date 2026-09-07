namespace EngineerPc.Engineering.Transactions;

public sealed class TransactionStateMachine : ITransactionStateMachine
{
    public EngineeringTransaction Transition(EngineeringTransaction transaction, TransactionState nextState)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (!IsAllowed(transaction.State, nextState))
        {
            throw new InvalidOperationException(
                $"Transaction cannot transition from '{transaction.State}' to '{nextState}'.");
        }

        return transaction with { State = nextState };
    }

    private static bool IsAllowed(TransactionState currentState, TransactionState nextState) =>
        (currentState, nextState) switch
        {
            (TransactionState.Created, TransactionState.Validating) => true,
            (TransactionState.Validating, TransactionState.AwaitingApproval) => true,
            (TransactionState.Approved, TransactionState.Executing) => true,
            (TransactionState.Executing, TransactionState.ValidatingResult) => true,
            (TransactionState.ValidatingResult, TransactionState.Committed) => true,
            (TransactionState.AwaitingApproval, TransactionState.Rejected) => true,
            (TransactionState.Executing, TransactionState.Failed) => true,
            (TransactionState.ValidatingResult, TransactionState.Failed) => true,
            (TransactionState.Executing, TransactionState.RolledBack) => true,
            (TransactionState.ValidatingResult, TransactionState.RolledBack) => true,
            (TransactionState.Failed, TransactionState.RolledBack) => true,
            (TransactionState.Created, TransactionState.Expired) => true,
            (TransactionState.Validating, TransactionState.Expired) => true,
            (TransactionState.AwaitingApproval, TransactionState.Expired) => true,
            (TransactionState.Approved, TransactionState.Expired) => true,
            _ => false
        };
}
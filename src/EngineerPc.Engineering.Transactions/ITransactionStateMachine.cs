namespace EngineerPc.Engineering.Transactions;

public interface ITransactionStateMachine
{
    EngineeringTransaction Transition(EngineeringTransaction transaction, TransactionState nextState);
}
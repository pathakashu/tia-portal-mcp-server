using EngineerPc.Contracts;

namespace EngineerPc.Engineering.Transactions;

public sealed record EngineeringTransaction(
    Guid TransactionId,
    Guid OperationId,
    string OperationHash,
    ProjectContext ProjectContext,
    string IdempotencyKey,
    TransactionState State,
    DateTimeOffset CreatedAtUtc)
{
    public static EngineeringTransaction Create(
        Guid operationId,
        string operationHash,
        ProjectContext projectContext,
        string idempotencyKey,
        DateTimeOffset createdAtUtc) => new(
            Guid.NewGuid(),
            operationId,
            operationHash,
            projectContext,
            idempotencyKey,
            TransactionState.Created,
            createdAtUtc);
}

public enum TransactionState
{
    Created,
    Validating,
    AwaitingApproval,
    Approved,
    Executing,
    ValidatingResult,
    Committed,
    Rejected,
    Failed,
    RolledBack,
    Expired
}
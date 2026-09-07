using EngineerPc.Contracts;

namespace EngineerPc.Engineering.Ir;

public abstract record EngineeringOperation(
    string IrVersion,
    Guid OperationId,
    ProjectContext ProjectContext,
    string IdempotencyKey)
{
    public abstract EngineeringOperationType OperationType { get; }
}

public enum EngineeringOperationType
{
    CreateBlock
}
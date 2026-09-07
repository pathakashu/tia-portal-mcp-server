using EngineerPc.Contracts;

namespace EngineerPc.Engineering.Ir;

public sealed record CreateBlockOperation(
    Guid OperationId,
    ProjectContext ProjectContext,
    string IdempotencyKey,
    string Name,
    BlockType BlockType,
    ProgrammingLanguage Language,
    BlockInterface Interface,
    IReadOnlyList<SclAssignment>? Statements = null) : EngineeringOperation("1.0", OperationId, ProjectContext, IdempotencyKey)
{
    public override EngineeringOperationType OperationType => EngineeringOperationType.CreateBlock;
}

public sealed record BlockInterface(
    IReadOnlyList<BlockParameter> Inputs,
    IReadOnlyList<BlockParameter>? Outputs = null);

public sealed record BlockParameter(string Name, string DataType);

public sealed record SclAssignment(string Target, string Source);

public enum BlockType
{
    Function,
    FunctionBlock,
    OrganizationBlock
}

public enum ProgrammingLanguage
{
    Scl,
    Lad,
    Fbd
}
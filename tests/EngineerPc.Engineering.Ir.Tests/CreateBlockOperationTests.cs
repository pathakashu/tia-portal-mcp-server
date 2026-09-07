using EngineerPc.Contracts;

namespace EngineerPc.Engineering.Ir.Tests;

public sealed class CreateBlockOperationTests
{
    [Fact]
    public void Validate_WithCanonicalCreateBlockIntent_ReturnsNoErrors()
    {
        var operation = new CreateBlockOperation(
            Guid.NewGuid(),
            new ProjectContext("project-1", "snapshot-sha256"),
            "request-1",
            "FB_Motor",
            BlockType.FunctionBlock,
            ProgrammingLanguage.Scl,
            new BlockInterface([new BlockParameter("Start", "Bool")]));

        var errors = EngineeringIrValidator.Validate(operation);

        Assert.Empty(errors);
        Assert.Equal("1.0", operation.IrVersion);
        Assert.Equal(EngineeringOperationType.CreateBlock, operation.OperationType);
    }

    [Fact]
    public void Validate_WithMissingBlockName_ReturnsDeterministicError()
    {
        var operation = new CreateBlockOperation(
            Guid.NewGuid(),
            new ProjectContext("project-1", "snapshot-sha256"),
            "request-1",
            " ",
            BlockType.FunctionBlock,
            ProgrammingLanguage.Scl,
            new BlockInterface([]));

        var errors = EngineeringIrValidator.Validate(operation);

        Assert.Contains("Block name is required.", errors);
    }

    [Fact]
    public void ControllerName_IsOptionalAndDefaultsToNull()
    {
        var operation = new CreateBlockOperation(
            Guid.NewGuid(),
            new ProjectContext("project-1", "snapshot-sha256"),
            "request-1",
            "FB_Motor",
            BlockType.FunctionBlock,
            ProgrammingLanguage.Scl,
            new BlockInterface([new BlockParameter("Start", "Bool")]));

        Assert.Null(operation.ControllerName);
        Assert.Empty(EngineeringIrValidator.Validate(operation));

        var withController = operation with { ControllerName = "PLC_1" };

        Assert.Equal("PLC_1", withController.ControllerName);
    }
}
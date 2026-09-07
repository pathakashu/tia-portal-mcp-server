using EngineerPc.Contracts;
using EngineerPc.Engineering.Ir;

namespace EngineerPc.Engineering.Validation.Tests;

public sealed class CreateBlockOperationValidatorTests
{
    [Fact]
    public void Validate_WithCanonicalBlockIntent_ReturnsValid()
    {
        var validator = new CreateBlockOperationValidator();

        var result = validator.Validate(CreateOperation(
            "FB_Motor",
            [new BlockParameter("Start", "Bool")]));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_WithInvalidBlockAndDuplicateInputNames_ReturnsErrors()
    {
        var validator = new CreateBlockOperationValidator();

        var result = validator.Validate(CreateOperation(
            "1 Motor",
            [
                new BlockParameter("Start", "Bool"),
                new BlockParameter("start", "")
            ]));

        Assert.False(result.IsValid);
        Assert.Contains(
            "Block name must start with a letter or underscore and contain only letters, digits, or underscores.",
            result.Errors);
        Assert.Contains("Input parameter name 'start' is duplicated.", result.Errors);
        Assert.Contains("Input parameter 'start' must declare a data type.", result.Errors);
    }

    [Fact]
    public void Validate_WithOutputDuplicatingInputName_ReturnsError()
    {
        var validator = new CreateBlockOperationValidator();
        var operation = CreateOperation("FB_Motor", [new BlockParameter("Start", "Bool")]) with
        {
            Interface = new BlockInterface(
                [new BlockParameter("Start", "Bool")],
                [new BlockParameter("Start", "Bool")])
        };

        var result = validator.Validate(operation);

        Assert.False(result.IsValid);
        Assert.Contains("Block interface parameter name 'Start' is duplicated.", result.Errors);
    }

    private static CreateBlockOperation CreateOperation(
        string name,
        IReadOnlyList<BlockParameter> inputs) => new(
            Guid.NewGuid(),
            new ProjectContext("project-1", "snapshot-sha256"),
            "request-1",
            name,
            BlockType.FunctionBlock,
            ProgrammingLanguage.Scl,
            new BlockInterface(inputs));
}
using EngineerPc.Contracts;
using EngineerPc.Engineering.Ir;
using EngineerPc.Engineering.Validation;

namespace EngineerPc.Engineering.Engine.Tests;

public sealed class EngineeringOperationPlannerTests
{
    [Fact]
    public void Plan_WithValidCreateBlockOperation_ReturnsStablePreviewAndHash()
    {
        var operation = CreateOperation("FB_Motor");
        var planner = CreatePlanner();

        var firstResult = planner.Plan(operation);
        var secondResult = planner.Plan(operation);

        Assert.True(firstResult.IsSuccess);
        Assert.NotNull(firstResult.Plan);
        Assert.Equal("Create FunctionBlock 'FB_Motor' using Scl.", firstResult.Plan.Preview);
        Assert.Equal(firstResult.Plan.OperationHash, secondResult.Plan?.OperationHash);
    }

    [Fact]
    public void Plan_WithInvalidCreateBlockOperation_ReturnsErrorsWithoutPlan()
    {
        var planner = CreatePlanner();

        var result = planner.Plan(CreateOperation(" "));

        Assert.False(result.IsSuccess);
        Assert.Null(result.Plan);
        Assert.Contains("Block name is required.", result.Errors);
    }

    private static EngineeringOperationPlanner CreatePlanner() => new(new CreateBlockOperationValidator());

    private static CreateBlockOperation CreateOperation(string name) => new(
        Guid.Parse("a5b9a005-26bc-4151-a45c-902a9338dc5a"),
        new ProjectContext("project-1", "snapshot-sha256"),
        "request-1",
        name,
        BlockType.FunctionBlock,
        ProgrammingLanguage.Scl,
        new BlockInterface([new BlockParameter("Start", "Bool")]));
}
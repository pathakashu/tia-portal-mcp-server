using EngineerPc.Contracts;
using EngineerPc.Engineering.Ir;

namespace EngineerPc.Engineering.Policy.Tests;

public sealed class DefaultEngineeringPolicyTests
{
    [Fact]
    public void Evaluate_CreateBlock_RequiresHumanApproval()
    {
        var policy = new DefaultEngineeringPolicy();

        var decision = policy.Evaluate(CreateBlockOperation());

        Assert.True(decision.IsAllowed);
        Assert.True(decision.RequiresApproval);
        Assert.Equal(OperationRisk.Medium, decision.Risk);
        Assert.Null(decision.DenialReason);
    }

    [Fact]
    public void Evaluate_UnsupportedOperation_DeniesByDefault()
    {
        var policy = new DefaultEngineeringPolicy();

        var decision = policy.Evaluate(new UnsupportedOperation());

        Assert.False(decision.IsAllowed);
        Assert.False(decision.RequiresApproval);
        Assert.Equal(OperationRisk.Critical, decision.Risk);
        Assert.Equal("Operation type '999' is not permitted by the default policy.", decision.DenialReason);
    }

    private static CreateBlockOperation CreateBlockOperation() => new(
        Guid.NewGuid(),
        new ProjectContext("project-1", "snapshot-sha256"),
        "request-1",
        "FB_Motor",
        BlockType.FunctionBlock,
        ProgrammingLanguage.Scl,
        new BlockInterface([]));

    private sealed record UnsupportedOperation() : EngineeringOperation(
        "1.0",
        Guid.NewGuid(),
        new ProjectContext("project-1", "snapshot-sha256"),
        "request-1")
    {
        public override EngineeringOperationType OperationType => (EngineeringOperationType)999;
    }
}
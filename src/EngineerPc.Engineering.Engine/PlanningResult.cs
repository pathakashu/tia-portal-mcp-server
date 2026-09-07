namespace EngineerPc.Engineering.Engine;

public sealed record PlanningResult(
    EngineeringOperationPlan? Plan,
    IReadOnlyList<string> Errors)
{
    public bool IsSuccess => Plan is not null && Errors.Count == 0;
}

public sealed record EngineeringOperationPlan(
    Guid OperationId,
    string OperationHash,
    string Preview);
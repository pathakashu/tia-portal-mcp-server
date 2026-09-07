using EngineerPc.Contracts;
using EngineerPc.Engineering.Ir;
using EngineerPc.Engineering.Policy;

namespace EngineerPc.Engineering.Engine;

public interface ISclBlockPreviewService
{
    SclBlockPreviewResult Generate(CreateBlockOperation operation, AuthenticatedIdentity identity);
}

public sealed record SclBlockPreviewResult(
    EngineeringOperationPlan? Plan,
    PolicyDecision? PolicyDecision,
    string? SourceText,
    IReadOnlyList<string> Errors)
{
    public bool IsSuccess => Plan is not null && PolicyDecision?.IsAllowed == true && SourceText is not null && Errors.Count == 0;
}
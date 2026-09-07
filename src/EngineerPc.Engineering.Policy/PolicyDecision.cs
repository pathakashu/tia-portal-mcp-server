namespace EngineerPc.Engineering.Policy;

public sealed record PolicyDecision(
    bool IsAllowed,
    OperationRisk Risk,
    bool RequiresApproval,
    string? DenialReason);

public enum OperationRisk
{
    Low,
    Medium,
    High,
    Critical
}
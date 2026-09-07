using EngineerPc.Audit;
using EngineerPc.Contracts;
using EngineerPc.Engineering.Ir;
using EngineerPc.Engineering.Policy;

namespace EngineerPc.Engineering.Engine;

public sealed class SclBlockPreviewService : ISclBlockPreviewService
{
    private readonly IEngineeringOperationPlanner planner;
    private readonly IEngineeringPolicy policy;
    private readonly IEngineeringAuditSink auditSink;
    private readonly TimeProvider timeProvider;

    public SclBlockPreviewService(
        IEngineeringOperationPlanner planner,
        IEngineeringPolicy policy,
        IEngineeringAuditSink? auditSink = null,
        TimeProvider? timeProvider = null)
    {
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.auditSink = auditSink ?? NullEngineeringAuditSink.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SclBlockPreviewResult Generate(CreateBlockOperation operation, AuthenticatedIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(identity);

        var planning = planner.Plan(operation);
        if (!planning.IsSuccess)
        {
            return Reject(operation, identity, null, null, planning.Errors, "SCL preview validation failed.");
        }

        var policyDecision = policy.Evaluate(operation);
        if (!policyDecision.IsAllowed)
        {
            return Reject(
                operation,
                identity,
                planning.Plan,
                policyDecision,
                [policyDecision.DenialReason ?? "Operation is not permitted by policy."],
                "SCL preview was denied by policy.");
        }

        var errors = SclSourceRenderer.Validate(operation);
        if (errors.Count > 0)
        {
            return Reject(operation, identity, planning.Plan, policyDecision, errors, "SCL preview source constraints failed.");
        }

        var sourceText = SclSourceRenderer.Render(operation);
        Record(EngineeringAuditEventType.SclPreviewGenerated, operation, identity, "Constrained SCL source preview was generated.");
        return new SclBlockPreviewResult(planning.Plan, policyDecision, sourceText, []);
    }

    private SclBlockPreviewResult Reject(
        CreateBlockOperation operation,
        AuthenticatedIdentity identity,
        EngineeringOperationPlan? plan,
        PolicyDecision? policyDecision,
        IReadOnlyList<string> errors,
        string detail)
    {
        Record(EngineeringAuditEventType.SclPreviewRejected, operation, identity, detail);
        return new SclBlockPreviewResult(plan, policyDecision, null, errors);
    }

    private void Record(
        EngineeringAuditEventType eventType,
        CreateBlockOperation operation,
        AuthenticatedIdentity identity,
        string detail) => auditSink.Record(new EngineeringAuditEvent(
            eventType,
            timeProvider.GetUtcNow(),
            operation.OperationId,
            null,
            operation.ProjectContext.ProjectId,
            operation.ProjectContext.SnapshotHash,
            identity,
            detail));
}
using EngineerPc.Audit;
using EngineerPc.Contracts;
using EngineerPc.Engineering.Approvals;
using EngineerPc.Engineering.Ir;
using EngineerPc.Engineering.Policy;
using EngineerPc.Engineering.Transactions;
using EngineerPc.Tia.Abstractions;

namespace EngineerPc.Engineering.Engine;

public sealed class CreateBlockWorkflow : ICreateBlockWorkflow
{
    private readonly IEngineeringOperationPlanner planner;
    private readonly IEngineeringPolicy policy;
    private readonly IApprovalService approvalService;
    private readonly ITransactionStateMachine stateMachine;
    private readonly ITiaAdapter tiaAdapter;
    private readonly IEngineeringAuditSink auditSink;
    private readonly TimeProvider timeProvider;

    public CreateBlockWorkflow(
        IEngineeringOperationPlanner planner,
        IEngineeringPolicy policy,
        IApprovalService approvalService,
        ITransactionStateMachine stateMachine,
        ITiaAdapter tiaAdapter,
        IEngineeringAuditSink? auditSink = null,
        TimeProvider? timeProvider = null)
    {
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.approvalService = approvalService ?? throw new ArgumentNullException(nameof(approvalService));
        this.stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        this.tiaAdapter = tiaAdapter ?? throw new ArgumentNullException(nameof(tiaAdapter));
        this.auditSink = auditSink ?? NullEngineeringAuditSink.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CreateBlockSubmissionResult Submit(CreateBlockOperation operation, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var planning = planner.Plan(operation);
        if (!planning.IsSuccess)
        {
            Record(operation, null, EngineeringAuditEventType.PlanRejected, null, "Planning validation failed.", nowUtc);
            return new CreateBlockSubmissionResult(null, null, null, planning.Errors);
        }

        var policyDecision = policy.Evaluate(operation);
        if (!policyDecision.IsAllowed)
        {
            Record(operation, null, EngineeringAuditEventType.PlanRejected, null, "Planning was denied by policy.", nowUtc);
            return new CreateBlockSubmissionResult(
                planning.Plan,
                policyDecision,
                null,
                [policyDecision.DenialReason ?? "Operation is not permitted by policy."]);
        }

        var transaction = EngineeringTransaction.Create(
            operation.OperationId,
            planning.Plan!.OperationHash,
            operation.ProjectContext,
            operation.IdempotencyKey,
            nowUtc);
        transaction = stateMachine.Transition(transaction, TransactionState.Validating);

        if (policyDecision.RequiresApproval)
        {
            transaction = stateMachine.Transition(transaction, TransactionState.AwaitingApproval);
            Record(operation, transaction, EngineeringAuditEventType.PlanAwaitingApproval, null, "Planning requires human approval.", nowUtc);
            return new CreateBlockSubmissionResult(planning.Plan, policyDecision, transaction, []);
        }

        Record(operation, transaction, EngineeringAuditEventType.PlanRejected, null, "Automatic execution is unavailable.", nowUtc);
        return new CreateBlockSubmissionResult(
            planning.Plan,
            policyDecision,
            transaction,
            ["Automatic execution without approval is not implemented."]);
    }

    public ApprovalResult Approve(
        EngineeringTransaction transaction,
        HumanApproval approval,
        AuthenticatedIdentity authenticatedIdentity,
        DateTimeOffset nowUtc)
    {
        var result = approvalService.Approve(transaction, approval, authenticatedIdentity, nowUtc);
        var eventType = result.IsApproved
            ? EngineeringAuditEventType.ApprovalGranted
            : EngineeringAuditEventType.ApprovalRejected;
        var detail = result.IsApproved
            ? "Human approval was granted."
            : "Human approval was rejected.";
        Record(transaction, eventType, authenticatedIdentity, detail, nowUtc);
        return result;
    }

    public async Task<CreateBlockExecutionResult> ExecuteAsync(
        EngineeringTransaction transaction,
        CreateBlockOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(operation);

        if (transaction.State != TransactionState.Approved)
        {
            Record(operation, transaction, EngineeringAuditEventType.ExecutionRejected, null, "Execution requires an approved transaction.", timeProvider.GetUtcNow());
            return new CreateBlockExecutionResult(transaction, null, ["Transaction must be approved before execution."]);
        }

        var planning = planner.Plan(operation);
        if (!planning.IsSuccess || planning.Plan?.OperationHash != transaction.OperationHash ||
            transaction.OperationId != operation.OperationId || transaction.ProjectContext != operation.ProjectContext)
        {
            Record(operation, transaction, EngineeringAuditEventType.ExecutionRejected, null, "Execution operation does not match its approved transaction.", timeProvider.GetUtcNow());
            return new CreateBlockExecutionResult(transaction, null, ["Operation does not match the approved transaction."]);
        }

        var executingTransaction = stateMachine.Transition(transaction, TransactionState.Executing);
        Record(operation, executingTransaction, EngineeringAuditEventType.ExecutionStarted, null, "Execution started.", timeProvider.GetUtcNow());
        var execution = await tiaAdapter.CreateBlockAsync(operation, cancellationToken);

        if (!execution.IsSuccess)
        {
            var failedTransaction = stateMachine.Transition(executingTransaction, TransactionState.Failed);
            Record(operation, failedTransaction, EngineeringAuditEventType.ExecutionFailed, null, "Execution failed.", timeProvider.GetUtcNow());
            return new CreateBlockExecutionResult(
                failedTransaction,
                null,
                execution.Errors);
        }

        var validatingResultTransaction = stateMachine.Transition(executingTransaction, TransactionState.ValidatingResult);
        var committedTransaction = stateMachine.Transition(validatingResultTransaction, TransactionState.Committed);
        Record(operation, committedTransaction, EngineeringAuditEventType.ExecutionCommitted, null, "Execution committed after validation.", timeProvider.GetUtcNow());
        return new CreateBlockExecutionResult(
            committedTransaction,
            execution.UpdatedProjectContext,
            []);
    }

    private void Record(
        CreateBlockOperation operation,
        EngineeringTransaction? transaction,
        EngineeringAuditEventType eventType,
        AuthenticatedIdentity? identity,
        string detail,
        DateTimeOffset occurredAtUtc) => auditSink.Record(new EngineeringAuditEvent(
            eventType,
            occurredAtUtc,
            operation.OperationId,
            transaction?.TransactionId,
            operation.ProjectContext.ProjectId,
            operation.ProjectContext.SnapshotHash,
            identity,
            detail));

    private void Record(
        EngineeringTransaction transaction,
        EngineeringAuditEventType eventType,
        AuthenticatedIdentity identity,
        string detail,
        DateTimeOffset occurredAtUtc) => auditSink.Record(new EngineeringAuditEvent(
            eventType,
            occurredAtUtc,
            transaction.OperationId,
            transaction.TransactionId,
            transaction.ProjectContext.ProjectId,
            transaction.ProjectContext.SnapshotHash,
            identity,
            detail));
}
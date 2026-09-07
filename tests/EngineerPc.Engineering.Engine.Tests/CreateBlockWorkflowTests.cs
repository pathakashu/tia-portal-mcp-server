using EngineerPc.Audit;
using EngineerPc.Contracts;
using EngineerPc.Engineering.Approvals;
using EngineerPc.Engineering.Ir;
using EngineerPc.Engineering.Policy;
using EngineerPc.Engineering.Transactions;
using EngineerPc.Engineering.Validation;
using EngineerPc.Tia.Abstractions;
using EngineerPc.Tia.Mock;

namespace EngineerPc.Engineering.Engine.Tests;

public sealed class CreateBlockWorkflowTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedIdentity Identity = new("engineer-1", "client-1");

    [Fact]
    public async Task ExecuteAsync_WithApprovedMatchingOperation_CommitsAndUpdatesContext()
    {
        var operation = CreateOperation();
        var auditSink = new InMemoryEngineeringAuditSink();
        var workflow = CreateWorkflow(auditSink: auditSink);
        var submission = workflow.Submit(operation, NowUtc);
        var approval = CreateApproval(submission.Transaction!);

        var approved = workflow.Approve(submission.Transaction!, approval, Identity, NowUtc);
        var execution = await workflow.ExecuteAsync(approved.Transaction!, operation, CancellationToken.None);

        Assert.True(submission.IsAwaitingApproval);
        Assert.True(approved.IsApproved);
        Assert.True(execution.IsCommitted);
        Assert.NotNull(execution.UpdatedProjectContext);
        Assert.NotEqual(operation.ProjectContext.SnapshotHash, execution.UpdatedProjectContext.SnapshotHash);
        Assert.Equal(
            [
                EngineeringAuditEventType.PlanAwaitingApproval,
                EngineeringAuditEventType.ApprovalGranted,
                EngineeringAuditEventType.ExecutionStarted,
                EngineeringAuditEventType.ExecutionCommitted
            ],
            auditSink.Events.Select(auditEvent => auditEvent.EventType));
    }

    [Fact]
    public async Task ExecuteAsync_WithoutApproval_ReturnsErrorAndDoesNotWrite()
    {
        var operation = CreateOperation();
        var adapter = new MockTiaAdapter([operation.ProjectContext]);
        var workflow = CreateWorkflow(adapter);
        var submission = workflow.Submit(operation, NowUtc);

        var execution = await workflow.ExecuteAsync(submission.Transaction!, operation, CancellationToken.None);
        var context = await adapter.GetProjectContextAsync(operation.ProjectContext.ProjectId, CancellationToken.None);

        Assert.False(execution.IsCommitted);
        Assert.Contains("Transaction must be approved before execution.", execution.Errors);
        Assert.Equal(operation.ProjectContext, context);
    }

    [Fact]
    public async Task ExecuteAsync_WithSclFunctionBlockIntent_PassesRenderedSourceToAdapter()
    {
        var operation = CreateOperation();
        var adapter = new RecordingTiaAdapter();
        var workflow = CreateWorkflow(adapter);
        var submission = workflow.Submit(operation, NowUtc);
        var approval = CreateApproval(submission.Transaction!);
        var approved = workflow.Approve(submission.Transaction!, approval, Identity, NowUtc);

        await workflow.ExecuteAsync(approved.Transaction!, operation, CancellationToken.None);

        Assert.NotNull(adapter.ReceivedSclSourceText);
        Assert.Contains("FUNCTION_BLOCK \"FB_Motor\"", adapter.ReceivedSclSourceText);
    }

    private sealed class RecordingTiaAdapter : ITiaAdapter
    {
        public string? ReceivedSclSourceText { get; private set; }

        public Task<ProjectContext?> GetProjectContextAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectContext?>(new ProjectContext(projectId, "snapshot-2"));

        public Task<TiaAdapterExecutionResult> CreateBlockAsync(
            CreateBlockOperation operation,
            CancellationToken cancellationToken,
            string? sclSourceText = null)
        {
            ReceivedSclSourceText = sclSourceText;
            return Task.FromResult(new TiaAdapterExecutionResult(
                new ProjectContext(operation.ProjectContext.ProjectId, "snapshot-2"),
                []));
        }
    }

    private static CreateBlockWorkflow CreateWorkflow(
        ITiaAdapter? adapter = null,
        IEngineeringAuditSink? auditSink = null) => new(
        new EngineeringOperationPlanner(new CreateBlockOperationValidator()),
        new DefaultEngineeringPolicy(),
        new ApprovalService(),
        new TransactionStateMachine(),
        adapter ?? new MockTiaAdapter([new ProjectContext("project-1", "snapshot-1")]),
        auditSink);

    private static CreateBlockOperation CreateOperation() => new(
        Guid.Parse("a5b9a005-26bc-4151-a45c-902a9338dc5a"),
        new ProjectContext("project-1", "snapshot-1"),
        "request-1",
        "FB_Motor",
        BlockType.FunctionBlock,
        ProgrammingLanguage.Scl,
        new BlockInterface([new BlockParameter("Start", "Bool")]));

    private static HumanApproval CreateApproval(EngineeringTransaction transaction) => new(
        Guid.NewGuid(),
        transaction.TransactionId,
        transaction.OperationHash,
        transaction.ProjectContext.SnapshotHash,
        Identity,
        NowUtc.AddMinutes(5));
}
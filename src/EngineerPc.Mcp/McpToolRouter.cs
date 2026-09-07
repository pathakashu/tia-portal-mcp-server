using EngineerPc.Engineering.Approvals;
using EngineerPc.Engineering.Engine;
using EngineerPc.Engineering.Ir;
using EngineerPc.Security;

namespace EngineerPc.Mcp;

public sealed class McpToolRouter
{
    private readonly McpSessionManager sessionManager;
    private readonly ICreateBlockWorkflow createBlockWorkflow;
    private readonly ISclBlockPreviewService sclBlockPreviewService;
    private readonly IProjectContextReadService projectContextReadService;
    private readonly IProjectBlockCatalogReadService projectBlockCatalogReadService;
    private readonly IAuthorizationService authorizationService;
    private readonly TimeProvider timeProvider;

    public McpToolRouter(
        McpSessionManager sessionManager,
        ICreateBlockWorkflow createBlockWorkflow,
        ISclBlockPreviewService sclBlockPreviewService,
        IProjectContextReadService projectContextReadService,
        IProjectBlockCatalogReadService projectBlockCatalogReadService,
        IAuthorizationService authorizationService,
        TimeProvider timeProvider)
    {
        this.sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        this.createBlockWorkflow = createBlockWorkflow ?? throw new ArgumentNullException(nameof(createBlockWorkflow));
        this.sclBlockPreviewService = sclBlockPreviewService ?? throw new ArgumentNullException(nameof(sclBlockPreviewService));
        this.projectContextReadService = projectContextReadService ?? throw new ArgumentNullException(nameof(projectContextReadService));
        this.projectBlockCatalogReadService = projectBlockCatalogReadService ?? throw new ArgumentNullException(nameof(projectBlockCatalogReadService));
        this.authorizationService = authorizationService ?? throw new ArgumentNullException(nameof(authorizationService));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public McpToolResult<CreateBlockSubmissionResult> PlanCreateBlock(McpToolCall<CreateBlockOperation> toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (toolCall.CorrelationId == Guid.Empty)
        {
            return Failed(toolCall, "Correlation ID is required.");
        }

        if (toolCall.Tool != McpTool.PlanCreateBlock)
        {
            return Failed(toolCall, $"Tool '{toolCall.Tool}' is not handled by this route.");
        }

        if (!sessionManager.TryGetReadySession(toolCall.SessionId, out var session, out var sessionError))
        {
            return Failed(toolCall, sessionError!);
        }

        var authorization = authorizationService.Authorize(
            session!.Principal!,
            toolCall.Tool.ToString(),
            toolCall.RequestId,
            toolCall.CorrelationId);
        if (!authorization.IsAllowed)
        {
            return Failed(toolCall, authorization.DenialReason!);
        }

        if (!sessionManager.TryRecordRequest(toolCall.SessionId, toolCall.RequestId, out var requestError))
        {
            return Failed(toolCall, requestError!);
        }

        var submission = createBlockWorkflow.Submit(toolCall.Payload, timeProvider.GetUtcNow());
        return new McpToolResult<CreateBlockSubmissionResult>(
            toolCall.RequestId,
            toolCall.CorrelationId,
            submission,
            submission.Errors);
    }

    public McpToolResult<SclBlockPreviewResult> PreviewSclBlock(McpToolCall<CreateBlockOperation> toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (toolCall.CorrelationId == Guid.Empty)
        {
            return FailedSclPreview(toolCall, "Correlation ID is required.");
        }

        if (toolCall.Tool != McpTool.PreviewSclBlock)
        {
            return FailedSclPreview(toolCall, $"Tool '{toolCall.Tool}' is not handled by this route.");
        }

        if (!sessionManager.TryGetReadySession(toolCall.SessionId, out var session, out var sessionError))
        {
            return FailedSclPreview(toolCall, sessionError!);
        }

        var authorization = authorizationService.Authorize(
            session!.Principal!,
            toolCall.Tool.ToString(),
            toolCall.RequestId,
            toolCall.CorrelationId);
        if (!authorization.IsAllowed)
        {
            return FailedSclPreview(toolCall, authorization.DenialReason!);
        }

        if (!sessionManager.TryRecordRequest(toolCall.SessionId, toolCall.RequestId, out var requestError))
        {
            return FailedSclPreview(toolCall, requestError!);
        }

        var preview = sclBlockPreviewService.Generate(toolCall.Payload, session.Principal!.Identity);
        return new McpToolResult<SclBlockPreviewResult>(
            toolCall.RequestId,
            toolCall.CorrelationId,
            preview,
            preview.Errors);
    }

    public McpToolResult<ApprovalResult> ApproveCreateBlock(McpToolCall<ApproveCreateBlockRequest> toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (toolCall.CorrelationId == Guid.Empty)
        {
            return FailedApproval(toolCall, "Correlation ID is required.");
        }

        if (toolCall.Tool != McpTool.ApproveCreateBlock)
        {
            return FailedApproval(toolCall, $"Tool '{toolCall.Tool}' is not handled by this route.");
        }

        if (!sessionManager.TryGetReadySession(toolCall.SessionId, out var session, out var sessionError))
        {
            return FailedApproval(toolCall, sessionError!);
        }

        var authorization = authorizationService.Authorize(
            session!.Principal!,
            toolCall.Tool.ToString(),
            toolCall.RequestId,
            toolCall.CorrelationId);
        if (!authorization.IsAllowed)
        {
            return FailedApproval(toolCall, authorization.DenialReason!);
        }

        if (!sessionManager.TryRecordRequest(toolCall.SessionId, toolCall.RequestId, out var requestError))
        {
            return FailedApproval(toolCall, requestError!);
        }

        var now = timeProvider.GetUtcNow();
        var approval = new HumanApproval(
            Guid.NewGuid(),
            toolCall.Payload.Transaction.TransactionId,
            toolCall.Payload.Transaction.OperationHash,
            toolCall.Payload.Transaction.ProjectContext.SnapshotHash,
            session.Principal!.Identity,
            toolCall.Payload.ExpiresAtUtc);
        var result = createBlockWorkflow.Approve(toolCall.Payload.Transaction, approval, session.Principal!.Identity, now);
        return new McpToolResult<ApprovalResult>(toolCall.RequestId, toolCall.CorrelationId, result, result.Errors);
    }

    public async Task<McpToolResult<CreateBlockExecutionResult>> ExecuteCreateBlockAsync(
        McpToolCall<ExecuteCreateBlockRequest> toolCall,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (toolCall.CorrelationId == Guid.Empty)
        {
            return FailedExecution(toolCall, "Correlation ID is required.");
        }

        if (toolCall.Tool != McpTool.ExecuteCreateBlock)
        {
            return FailedExecution(toolCall, $"Tool '{toolCall.Tool}' is not handled by this route.");
        }

        if (!sessionManager.TryGetReadySession(toolCall.SessionId, out var session, out var sessionError))
        {
            return FailedExecution(toolCall, sessionError!);
        }

        var authorization = authorizationService.Authorize(
            session!.Principal!,
            toolCall.Tool.ToString(),
            toolCall.RequestId,
            toolCall.CorrelationId);
        if (!authorization.IsAllowed)
        {
            return FailedExecution(toolCall, authorization.DenialReason!);
        }

        if (!sessionManager.TryRecordRequest(toolCall.SessionId, toolCall.RequestId, out var requestError))
        {
            return FailedExecution(toolCall, requestError!);
        }

        var result = await createBlockWorkflow.ExecuteAsync(
            toolCall.Payload.Transaction,
            toolCall.Payload.Operation,
            cancellationToken);
        return new McpToolResult<CreateBlockExecutionResult>(toolCall.RequestId, toolCall.CorrelationId, result, result.Errors);
    }

    public async Task<McpToolResult<ProjectContextReadResult>> GetProjectContextAsync(
        McpToolCall<GetProjectContextRequest> toolCall,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (toolCall.CorrelationId == Guid.Empty)
        {
            return Failed(toolCall, "Correlation ID is required.");
        }

        if (toolCall.Tool != McpTool.GetProjectContext)
        {
            return Failed(toolCall, $"Tool '{toolCall.Tool}' is not handled by this route.");
        }

        if (!sessionManager.TryGetReadySession(toolCall.SessionId, out var session, out var sessionError))
        {
            return Failed(toolCall, sessionError!);
        }

        var authorization = authorizationService.Authorize(
            session!.Principal!,
            toolCall.Tool.ToString(),
            toolCall.RequestId,
            toolCall.CorrelationId);
        if (!authorization.IsAllowed)
        {
            return Failed(toolCall, authorization.DenialReason!);
        }

        if (!sessionManager.TryRecordRequest(toolCall.SessionId, toolCall.RequestId, out var requestError))
        {
            return Failed(toolCall, requestError!);
        }

        var result = await projectContextReadService.GetProjectContextAsync(
            toolCall.Payload.ProjectId,
            session.Principal!.Identity,
            toolCall.RequestId,
            cancellationToken);
        return new McpToolResult<ProjectContextReadResult>(
            toolCall.RequestId,
            toolCall.CorrelationId,
            result,
            result.Errors);
    }

    public async Task<McpToolResult<ProjectBlockCatalogReadResult>> GetBlockCatalogAsync(
        McpToolCall<GetBlockCatalogRequest> toolCall,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        if (toolCall.CorrelationId == Guid.Empty)
        {
            return Failed(toolCall, "Correlation ID is required.");
        }

        if (toolCall.Tool != McpTool.GetBlockCatalog)
        {
            return Failed(toolCall, $"Tool '{toolCall.Tool}' is not handled by this route.");
        }

        if (!sessionManager.TryGetReadySession(toolCall.SessionId, out var session, out var sessionError))
        {
            return Failed(toolCall, sessionError!);
        }

        var authorization = authorizationService.Authorize(
            session!.Principal!,
            toolCall.Tool.ToString(),
            toolCall.RequestId,
            toolCall.CorrelationId);
        if (!authorization.IsAllowed)
        {
            return Failed(toolCall, authorization.DenialReason!);
        }

        if (!sessionManager.TryRecordRequest(toolCall.SessionId, toolCall.RequestId, out var requestError))
        {
            return Failed(toolCall, requestError!);
        }

        var result = await projectBlockCatalogReadService.GetBlockCatalogAsync(
            toolCall.Payload.ProjectId,
            toolCall.Payload.StartIndex ?? 0,
            toolCall.Payload.MaxBlocks ?? ProjectBlockCatalogReadService.MaximumBlockCount,
            toolCall.Payload.ExpectedSnapshotHash,
            session.Principal!.Identity,
            toolCall.RequestId,
            cancellationToken);
        return new McpToolResult<ProjectBlockCatalogReadResult>(
            toolCall.RequestId,
            toolCall.CorrelationId,
            result,
            result.Errors);
    }

    private static McpToolResult<CreateBlockSubmissionResult> Failed(
        McpToolCall<CreateBlockOperation> toolCall,
        string error) => new(toolCall.RequestId, toolCall.CorrelationId, null, [error]);

    private static McpToolResult<SclBlockPreviewResult> FailedSclPreview(
        McpToolCall<CreateBlockOperation> toolCall,
        string error) => new(toolCall.RequestId, toolCall.CorrelationId, null, [error]);

    private static McpToolResult<ProjectContextReadResult> Failed(
        McpToolCall<GetProjectContextRequest> toolCall,
        string error) => new(toolCall.RequestId, toolCall.CorrelationId, null, [error]);

    private static McpToolResult<ProjectBlockCatalogReadResult> Failed(
        McpToolCall<GetBlockCatalogRequest> toolCall,
        string error) => new(toolCall.RequestId, toolCall.CorrelationId, null, [error]);

    private static McpToolResult<ApprovalResult> FailedApproval(
        McpToolCall<ApproveCreateBlockRequest> toolCall,
        string error) => new(toolCall.RequestId, toolCall.CorrelationId, null, [error]);

    private static McpToolResult<CreateBlockExecutionResult> FailedExecution(
        McpToolCall<ExecuteCreateBlockRequest> toolCall,
        string error) => new(toolCall.RequestId, toolCall.CorrelationId, null, [error]);
}
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
}
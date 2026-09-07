using EngineerPc.Contracts;
using EngineerPc.Engineering.Approvals;
using EngineerPc.Engineering.Engine;
using EngineerPc.Engineering.Ir;
using EngineerPc.Engineering.Policy;
using EngineerPc.Engineering.Transactions;
using EngineerPc.Engineering.Validation;
using EngineerPc.Security;
using EngineerPc.Tia.Abstractions;
using EngineerPc.Tia.Mock;

namespace EngineerPc.Mcp.Tests;

public sealed class McpToolRouterTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PlanCreateBlock_WithReadySession_RoutesToEngineeringWorkflow()
    {
        var timeProvider = new TestTimeProvider(NowUtc);
        var sessionManager = new McpSessionManager(timeProvider);
        var session = CreateReadySession(sessionManager);
        var router = CreateRouter(sessionManager, timeProvider);
        var toolCall = CreateToolCall(session.SessionId, Guid.NewGuid());

        var result = router.PlanCreateBlock(toolCall);

        Assert.True(result.IsSuccess);
        Assert.Equal(toolCall.RequestId, result.RequestId);
        Assert.Equal(toolCall.CorrelationId, result.CorrelationId);
        Assert.True(result.Payload?.IsAwaitingApproval);
    }

    [Fact]
    public void PlanCreateBlock_WithSessionNotReady_RejectsRequest()
    {
        var timeProvider = new TestTimeProvider(NowUtc);
        var sessionManager = new McpSessionManager(timeProvider);
        var connectedSession = sessionManager.Connect(TimeSpan.FromMinutes(5));
        var router = CreateRouter(sessionManager, timeProvider);

        var result = router.PlanCreateBlock(CreateToolCall(connectedSession.SessionId, Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Contains("MCP session is not ready for tool calls.", result.Errors);
    }

    [Fact]
    public void PlanCreateBlock_WithoutRequiredScope_RejectsRequest()
    {
        var timeProvider = new TestTimeProvider(NowUtc);
        var sessionManager = new McpSessionManager(timeProvider);
        var session = CreateReadySession(sessionManager, []);
        var router = CreateRouter(sessionManager, timeProvider);

        var result = router.PlanCreateBlock(CreateToolCall(session.SessionId, Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Contains("Authenticated principal does not satisfy the required role and scope.", result.Errors);
    }

    [Fact]
    public void PlanCreateBlock_WithDuplicateRequestId_RejectsReplay()
    {
        var timeProvider = new TestTimeProvider(NowUtc);
        var sessionManager = new McpSessionManager(timeProvider);
        var session = CreateReadySession(sessionManager);
        var router = CreateRouter(sessionManager, timeProvider);
        var toolCall = CreateToolCall(session.SessionId, Guid.NewGuid());

        var firstResult = router.PlanCreateBlock(toolCall);
        var replayResult = router.PlanCreateBlock(toolCall);

        Assert.True(firstResult.IsSuccess);
        Assert.False(replayResult.IsSuccess);
        Assert.Contains("Request ID has already been processed for this session.", replayResult.Errors);
    }

    [Fact]
    public void PlanCreateBlock_WithExpiredSession_RejectsRequest()
    {
        var timeProvider = new TestTimeProvider(NowUtc);
        var sessionManager = new McpSessionManager(timeProvider);
        var session = CreateReadySession(sessionManager);
        var router = CreateRouter(sessionManager, timeProvider);
        timeProvider.UtcNow = NowUtc.AddMinutes(6);

        var result = router.PlanCreateBlock(CreateToolCall(session.SessionId, Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Contains("MCP session has expired.", result.Errors);
    }

    [Fact]
    public void PreviewSclBlock_WithRequiredScope_ReturnsDeclarationPreview()
    {
        var timeProvider = new TestTimeProvider(NowUtc);
        var sessionManager = new McpSessionManager(timeProvider);
        var session = CreateReadySession(sessionManager, ["engineering.plan"]);
        var router = CreateRouter(sessionManager, timeProvider);

        var result = router.PreviewSclBlock(CreateSclPreviewToolCall(session.SessionId, Guid.NewGuid()));

        Assert.True(result.IsSuccess);
        Assert.Contains("FUNCTION_BLOCK \"FB_Motor\"", result.Payload?.SourceText);
        Assert.Contains("Running := Start;", result.Payload?.SourceText);
    }

    [Fact]
    public void PreviewSclBlock_WithoutRequiredScope_RejectsRequest()
    {
        var timeProvider = new TestTimeProvider(NowUtc);
        var sessionManager = new McpSessionManager(timeProvider);
        var session = CreateReadySession(sessionManager, ["engineering.read"]);
        var router = CreateRouter(sessionManager, timeProvider);

        var result = router.PreviewSclBlock(CreateSclPreviewToolCall(session.SessionId, Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Contains("Authenticated principal does not satisfy the required role and scope.", result.Errors);
    }

    [Fact]
    public void PreviewSclBlock_WithDuplicateRequestId_RejectsReplay()
    {
        var timeProvider = new TestTimeProvider(NowUtc);
        var sessionManager = new McpSessionManager(timeProvider);
        var session = CreateReadySession(sessionManager, ["engineering.plan"]);
        var router = CreateRouter(sessionManager, timeProvider);
        var toolCall = CreateSclPreviewToolCall(session.SessionId, Guid.NewGuid());

        var firstResult = router.PreviewSclBlock(toolCall);
        var replayResult = router.PreviewSclBlock(toolCall);

        Assert.True(firstResult.IsSuccess);
        Assert.False(replayResult.IsSuccess);
        Assert.Contains("Request ID has already been processed for this session.", replayResult.Errors);
    }

    [Fact]
    public async Task GetProjectContextAsync_WithRequiredScope_RoutesToReadService()
    {
        var timeProvider = new TestTimeProvider(NowUtc);
        var sessionManager = new McpSessionManager(timeProvider);
        var session = CreateReadySession(sessionManager, ["engineering.read"]);
        var router = CreateRouter(sessionManager, timeProvider);

        var result = await router.GetProjectContextAsync(
            CreateProjectContextToolCall(session.SessionId, Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new ProjectContext("project-1", "snapshot-1"), result.Payload?.ProjectContext);
    }

    [Fact]
    public async Task GetProjectContextAsync_WithoutRequiredScope_RejectsBeforeRead()
    {
        var timeProvider = new TestTimeProvider(NowUtc);
        var sessionManager = new McpSessionManager(timeProvider);
        var session = CreateReadySession(sessionManager, ["engineering.plan"]);
        var router = CreateRouter(sessionManager, timeProvider);

        var result = await router.GetProjectContextAsync(
            CreateProjectContextToolCall(session.SessionId, Guid.NewGuid()),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Authenticated principal does not satisfy the required role and scope.", result.Errors);
    }

    [Fact]
    public async Task GetProjectContextAsync_WithDuplicateRequestId_RejectsReplay()
    {
        var timeProvider = new TestTimeProvider(NowUtc);
        var sessionManager = new McpSessionManager(timeProvider);
        var session = CreateReadySession(sessionManager, ["engineering.read"]);
        var router = CreateRouter(sessionManager, timeProvider);
        var toolCall = CreateProjectContextToolCall(session.SessionId, Guid.NewGuid());

        var firstResult = await router.GetProjectContextAsync(toolCall, CancellationToken.None);
        var replayResult = await router.GetProjectContextAsync(toolCall, CancellationToken.None);

        Assert.True(firstResult.IsSuccess);
        Assert.False(replayResult.IsSuccess);
        Assert.Contains("Request ID has already been processed for this session.", replayResult.Errors);
    }

    [Fact]
    public async Task GetBlockCatalogAsync_WithRequiredScope_RoutesToReadService()
    {
        var timeProvider = new TestTimeProvider(NowUtc);
        var sessionManager = new McpSessionManager(timeProvider);
        var session = CreateReadySession(sessionManager, ["engineering.read"]);
        var router = CreateRouter(sessionManager, timeProvider);

        var result = await router.GetBlockCatalogAsync(
            CreateBlockCatalogToolCall(session.SessionId, Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("FB_Motor", result.Payload?.BlockCatalog?.Blocks.Single().Name);
    }

    [Fact]
    public async Task GetBlockCatalogAsync_WithoutRequiredScope_RejectsBeforeRead()
    {
        var timeProvider = new TestTimeProvider(NowUtc);
        var sessionManager = new McpSessionManager(timeProvider);
        var session = CreateReadySession(sessionManager, ["engineering.plan"]);
        var router = CreateRouter(sessionManager, timeProvider);

        var result = await router.GetBlockCatalogAsync(
            CreateBlockCatalogToolCall(session.SessionId, Guid.NewGuid()),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Authenticated principal does not satisfy the required role and scope.", result.Errors);
    }

    [Fact]
    public async Task GetBlockCatalogAsync_WithRequestedPage_PassesPageToReadService()
    {
        var timeProvider = new TestTimeProvider(NowUtc);
        var sessionManager = new McpSessionManager(timeProvider);
        var session = CreateReadySession(sessionManager, ["engineering.read"]);
        var catalogReadService = new StaticProjectBlockCatalogReadService();
        var router = CreateRouter(sessionManager, timeProvider, catalogReadService);

        var result = await router.GetBlockCatalogAsync(
            CreateBlockCatalogToolCall(session.SessionId, Guid.NewGuid(), 100, 25, "snapshot-1"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(100, catalogReadService.RequestedStartIndex);
        Assert.Equal(25, catalogReadService.RequestedMaximumBlockCount);
        Assert.Equal("snapshot-1", catalogReadService.ExpectedSnapshotHash);
    }

    private static McpSession CreateReadySession(
        McpSessionManager sessionManager,
        IEnumerable<string>? scopes = null)
    {
        var connectedSession = sessionManager.Connect(TimeSpan.FromMinutes(5));
        sessionManager.Authenticate(
            connectedSession.SessionId,
            new AuthenticatedPrincipal(
                new AuthenticatedIdentity("engineer-1", "client-1"),
                new HashSet<string>(["Engineer"], StringComparer.Ordinal),
                new HashSet<string>(scopes ?? ["engineering.plan"], StringComparer.Ordinal)));
        return sessionManager.Initialize(connectedSession.SessionId);
    }

    private static ScopeAuthorizationService CreateAuthorizationService(TimeProvider timeProvider) => new(
        [
            new AuthorizationRule("PlanCreateBlock", "Engineer", "engineering.plan"),
            new AuthorizationRule("PreviewSclBlock", "Engineer", "engineering.plan"),
            new AuthorizationRule("GetProjectContext", "Engineer", "engineering.read"),
            new AuthorizationRule("GetBlockCatalog", "Engineer", "engineering.read")
        ],
        new InMemorySecurityEventSink(),
        timeProvider);

    private static McpToolCall<CreateBlockOperation> CreateToolCall(Guid sessionId, Guid requestId) => new(
        requestId,
        Guid.NewGuid(),
        sessionId,
        McpTool.PlanCreateBlock,
        new CreateBlockOperation(
            Guid.NewGuid(),
            new ProjectContext("project-1", "snapshot-1"),
            requestId.ToString("N"),
            "FB_Motor",
            BlockType.FunctionBlock,
            ProgrammingLanguage.Scl,
            new BlockInterface([])));

    private static McpToolCall<CreateBlockOperation> CreateSclPreviewToolCall(Guid sessionId, Guid requestId) => new(
        requestId,
        Guid.NewGuid(),
        sessionId,
        McpTool.PreviewSclBlock,
        new CreateBlockOperation(
            Guid.NewGuid(),
            new ProjectContext("project-1", "snapshot-1"),
            requestId.ToString("N"),
            "FB_Motor",
            BlockType.FunctionBlock,
            ProgrammingLanguage.Scl,
            new BlockInterface(
                [new BlockParameter("Start", "Bool")],
                [new BlockParameter("Running", "Bool")]),
            [new SclAssignment("Running", "Start")]));

    private static McpToolCall<GetProjectContextRequest> CreateProjectContextToolCall(Guid sessionId, Guid requestId) => new(
        requestId,
        Guid.NewGuid(),
        sessionId,
        McpTool.GetProjectContext,
        new GetProjectContextRequest("project-1"));

    private static McpToolCall<GetBlockCatalogRequest> CreateBlockCatalogToolCall(
        Guid sessionId,
        Guid requestId,
        int? startIndex = null,
        int? maxBlocks = null,
        string? expectedSnapshotHash = null) => new(
        requestId,
        Guid.NewGuid(),
        sessionId,
        McpTool.GetBlockCatalog,
        new GetBlockCatalogRequest("project-1", startIndex, maxBlocks, expectedSnapshotHash));

    private static McpToolRouter CreateRouter(
        McpSessionManager sessionManager,
        TimeProvider timeProvider,
        IProjectBlockCatalogReadService? blockCatalogReadService = null) => new(
        sessionManager,
        CreateWorkflow(),
        CreateSclBlockPreviewService(),
        CreateProjectContextReadService(),
        blockCatalogReadService ?? CreateBlockCatalogReadService(),
        CreateAuthorizationService(timeProvider),
        timeProvider);

    private static ICreateBlockWorkflow CreateWorkflow() => new CreateBlockWorkflow(
        new EngineeringOperationPlanner(new CreateBlockOperationValidator()),
        new DefaultEngineeringPolicy(),
        new ApprovalService(),
        new TransactionStateMachine(),
        new MockTiaAdapter([new ProjectContext("project-1", "snapshot-1")]));

    private static ISclBlockPreviewService CreateSclBlockPreviewService() => new SclBlockPreviewService(
        new EngineeringOperationPlanner(new CreateBlockOperationValidator()),
        new DefaultEngineeringPolicy());

    private static IProjectContextReadService CreateProjectContextReadService() => new ProjectContextReadService(
        new MockTiaAdapter([new ProjectContext("project-1", "snapshot-1")]));

    private static IProjectBlockCatalogReadService CreateBlockCatalogReadService() =>
        new StaticProjectBlockCatalogReadService();

    private sealed class StaticProjectBlockCatalogReadService : IProjectBlockCatalogReadService
    {
        public int? RequestedStartIndex { get; private set; }
        public int? RequestedMaximumBlockCount { get; private set; }
        public string? ExpectedSnapshotHash { get; private set; }

        public Task<ProjectBlockCatalogReadResult> GetBlockCatalogAsync(
            string projectId,
            int startIndex,
            int maximumBlockCount,
            string? expectedSnapshotHash,
            AuthenticatedIdentity identity,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            RequestedStartIndex = startIndex;
            RequestedMaximumBlockCount = maximumBlockCount;
            ExpectedSnapshotHash = expectedSnapshotHash;
            return Task.FromResult(new ProjectBlockCatalogReadResult(
                new ProjectBlockCatalogPage(
                    new ProjectContext(projectId, "snapshot-1"),
                    [new ProjectBlock("PLC_1", "FB_Motor", string.Empty, 1, "Scl")],
                    1,
                    null),
                []));
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
using System.Text.Json;
using EngineerPc.Contracts;
using EngineerPc.Engineering.Approvals;
using EngineerPc.Engineering.Engine;
using EngineerPc.Engineering.Ir;
using EngineerPc.Engineering.Policy;
using EngineerPc.Engineering.Transactions;
using EngineerPc.Engineering.Validation;
using EngineerPc.Mcp;
using EngineerPc.Mcp.Host;
using EngineerPc.Security;
using EngineerPc.Tia.Abstractions;
using Microsoft.AspNetCore.Http;

namespace EngineerPc.Mcp.Host.Tests;

public sealed class McpJsonRpcRequestProcessorTests
{
    [Fact]
    public void Process_Initialize_ReturnsSessionAndToolCapability()
    {
        var processor = CreateProcessor(out _);
        var request = Request("initialize", new
        {
            protocolVersion = McpJsonRpcRequestProcessor.ProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "test-client", version = "1.0.0" }
        });

        var result = processor.Process(request, Principal(), null, null);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.NotNull(result.SessionId);
        Assert.Null(result.Response?.Error);
        Assert.Contains("tools", McpJsonRpcRequestProcessor.SerializeResponse(result.Response!));
    }

    [Fact]
    public void Process_ToolsListBeforeInitialized_RejectsRequest()
    {
        var processor = CreateProcessor(out _);

        var result = processor.Process(
            Request("tools/list", new { }),
            Principal(),
            Guid.NewGuid().ToString("N"),
            McpJsonRpcRequestProcessor.ProtocolVersion);

        Assert.Equal(-32602, result.Response?.Error?.Code);
        Assert.Equal("MCP session is not ready for tool calls.", result.Response?.Error?.Message);
    }

    [Fact]
    public void Process_InitializedThenToolsList_ExposesPlanningTools()
    {
        var processor = CreateProcessor(out var sessionManager);
        var initialized = processor.Process(
            InitializeRequest(),
            Principal(),
            null,
            null);

        var notification = processor.Process(
            Notification("notifications/initialized"),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);
        var listed = processor.Process(
            Request("tools/list", new { }),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);

        Assert.Equal(StatusCodes.Status202Accepted, notification.StatusCode);
        Assert.Null(notification.Response);
        Assert.Equal(StatusCodes.Status200OK, listed.StatusCode);
        var responseJson = McpJsonRpcRequestProcessor.SerializeResponse(listed.Response!);
        Assert.Contains("plan_create_block", responseJson);
        Assert.Contains("preview_scl_block", responseJson);
        Assert.DoesNotContain("execute_create_block", responseJson);
        Assert.DoesNotContain("get_project_context", responseJson);
        Assert.DoesNotContain("get_block_catalog", responseJson);
    }

    [Fact]
    public void Process_EnabledBlockWriteTools_ExposesApproveAndExecuteTools()
    {
        var processor = CreateProcessor(out _, blockWriteEnabled: true);
        var initialized = processor.Process(InitializeRequest(), Principal(), null, null);
        processor.Process(
            Notification("notifications/initialized"),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);

        var listed = processor.Process(
            Request("tools/list", new { }),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);

        var responseJson = McpJsonRpcRequestProcessor.SerializeResponse(listed.Response!);
        Assert.Contains("approve_create_block", responseJson);
        Assert.Contains("execute_create_block", responseJson);
    }

    [Fact]
    public void Process_EnabledBlockWriteTools_ApprovesThenExecutesCreateBlock()
    {
        var processor = CreateProcessor(out _, blockWriteEnabled: true);
        var initialized = processor.Process(InitializeRequest(), Principal(), null, null);
        processor.Process(
            Notification("notifications/initialized"),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);
        var operation = CreateOperation();

        var planResult = processor.Process(
            Request("tools/call", new { name = "plan_create_block", arguments = operation }),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);
        using var planDocument = JsonDocument.Parse(McpJsonRpcRequestProcessor.SerializeResponse(planResult.Response!));
        var transaction = planDocument.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("transaction").Clone();

        var approveResult = processor.Process(
            Request("tools/call", new
            {
                name = "approve_create_block",
                arguments = new { transaction, expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5) }
            }),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);
        var approveJson = McpJsonRpcRequestProcessor.SerializeResponse(approveResult.Response!);
        Assert.Contains("\"isError\":false", approveJson);
        using var approveDocument = JsonDocument.Parse(approveJson);
        var approvedTransaction = approveDocument.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("transaction").Clone();

        var executeResult = processor.Process(
            Request("tools/call", new
            {
                name = "execute_create_block",
                arguments = new { transaction = approvedTransaction, operation }
            }),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);

        var executeJson = McpJsonRpcRequestProcessor.SerializeResponse(executeResult.Response!);
        Assert.Contains("\"isError\":false", executeJson);
        Assert.Contains("\"isCommitted\":true", executeJson);
    }

    [Fact]
    public void Process_EnabledProjectContextTool_ReturnsConfiguredProjectContext()
    {
        var processor = CreateProcessor(out _, projectContextReadEnabled: true);
        var initialized = processor.Process(
            InitializeRequest(),
            Principal(),
            null,
            null);
        processor.Process(
            Notification("notifications/initialized"),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);

        var listed = processor.Process(
            Request("tools/list", new { }),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);
        var result = processor.Process(
            Request("tools/call", new
            {
                name = "get_project_context",
                arguments = new { projectId = "project-1" }
            }),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);

        Assert.Contains("get_project_context", McpJsonRpcRequestProcessor.SerializeResponse(listed.Response!));
        var responseJson = McpJsonRpcRequestProcessor.SerializeResponse(result.Response!);
        Assert.Contains("snapshot-1", responseJson);
        Assert.Contains("\"isError\":false", responseJson);
    }

    [Fact]
    public void Process_EnabledBlockCatalogTool_ReturnsConfiguredCatalog()
    {
        var processor = CreateProcessor(out _, blockCatalogReadEnabled: true);
        var initialized = processor.Process(InitializeRequest(), Principal(), null, null);
        processor.Process(
            Notification("notifications/initialized"),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);

        var listed = processor.Process(
            Request("tools/list", new { }),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);
        var result = processor.Process(
            Request("tools/call", new
            {
                name = "get_block_catalog",
                arguments = new { projectId = "project-1", startIndex = 5, maxBlocks = 1, expectedSnapshotHash = "snapshot-1" }
            }),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);

        Assert.Contains("get_block_catalog", McpJsonRpcRequestProcessor.SerializeResponse(listed.Response!));
        var responseJson = McpJsonRpcRequestProcessor.SerializeResponse(result.Response!);
        Assert.Contains("FB_Motor", responseJson);
        Assert.Contains("\"totalBlockCount\":6", responseJson);
        Assert.Contains("\"isTruncated\":true", responseJson);
        Assert.Contains("\"nextStartIndex\":6", responseJson);
        Assert.Contains("\"isError\":false", responseJson);
    }

    [Fact]
    public void Process_ToolsCallAfterInitialized_ReturnsApprovalGatedPlan()
    {
        var processor = CreateProcessor(out _);
        var initialized = processor.Process(
            InitializeRequest(),
            Principal(),
            null,
            null);
        processor.Process(
            Notification("notifications/initialized"),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);

        var result = processor.Process(
            Request("tools/call", new
            {
                name = "plan_create_block",
                arguments = CreateOperation()
            }),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Null(result.Response?.Error);
        var responseJson = McpJsonRpcRequestProcessor.SerializeResponse(result.Response!);
        Assert.Contains("isAwaitingApproval", responseJson);
        Assert.Contains("\"isError\":false", responseJson);
    }

    [Fact]
    public void Process_PreviewSclBlock_ReturnsDeclarationSource()
    {
        var processor = CreateProcessor(out _);
        var initialized = processor.Process(InitializeRequest(), Principal(), null, null);
        processor.Process(
            Notification("notifications/initialized"),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);

        var result = processor.Process(
            Request("tools/call", new
            {
                name = "preview_scl_block",
                arguments = CreateSclPreviewOperation()
            }),
            Principal(),
            initialized.SessionId,
            McpJsonRpcRequestProcessor.ProtocolVersion);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        var responseJson = McpJsonRpcRequestProcessor.SerializeResponse(result.Response!);
        Assert.Contains("FUNCTION_BLOCK", responseJson);
        Assert.Contains("Running := Start;", responseJson);
        Assert.Contains("END_FUNCTION_BLOCK", responseJson);
        Assert.Contains("\"isError\":false", responseJson);
    }

    [Fact]
    public void Process_InitializeWithoutClientInfo_RejectsRequest()
    {
        var processor = CreateProcessor(out _);

        var result = processor.Process(
            Request("initialize", new { protocolVersion = McpJsonRpcRequestProcessor.ProtocolVersion, capabilities = new { } }),
            Principal(),
            null,
            null);

        Assert.Equal(-32602, result.Response?.Error?.Code);
        Assert.Equal("Initialize requires protocolVersion, capabilities, and clientInfo parameters.", result.Response?.Error?.Message);
    }

    private static McpJsonRpcRequestProcessor CreateProcessor(
        out McpSessionManager sessionManager,
        bool projectContextReadEnabled = false,
        bool blockCatalogReadEnabled = false,
        bool blockWriteEnabled = false)
    {
        var timeProvider = TimeProvider.System;
        sessionManager = new McpSessionManager(timeProvider);
        var planner = new EngineeringOperationPlanner(new CreateBlockOperationValidator());
        var policy = new DefaultEngineeringPolicy();
        var workflow = new CreateBlockWorkflow(
            planner,
            policy,
            new ApprovalService(),
            new TransactionStateMachine(),
            new RecordingCreateBlockAdapter());
        return new McpJsonRpcRequestProcessor(
            sessionManager,
            new McpToolRouter(
                sessionManager,
                workflow,
                new SclBlockPreviewService(planner, policy),
                new StaticProjectContextReadService(),
                new StaticProjectBlockCatalogReadService(),
                new ScopeAuthorizationService(
                    [
                        new AuthorizationRule("PlanCreateBlock", "Engineer", "engineering.plan"),
                        new AuthorizationRule("PreviewSclBlock", "Engineer", "engineering.plan"),
                        new AuthorizationRule("GetProjectContext", "Engineer", "engineering.read"),
                        new AuthorizationRule("GetBlockCatalog", "Engineer", "engineering.read"),
                        new AuthorizationRule("ApproveCreateBlock", "Engineer", "engineering.execute"),
                        new AuthorizationRule("ExecuteCreateBlock", "Engineer", "engineering.execute")
                    ],
                    new InMemorySecurityEventSink(),
                    timeProvider),
                timeProvider),
            projectContextReadEnabled,
            blockCatalogReadEnabled,
            blockWriteEnabled);
    }

    private static AuthenticatedPrincipal Principal() => new(
        new AuthenticatedIdentity("developer", "localhost"),
        new HashSet<string>(["Engineer"], StringComparer.Ordinal),
        new HashSet<string>(["engineering.plan", "engineering.read", "engineering.execute"], StringComparer.Ordinal));

    private sealed class RecordingCreateBlockAdapter : ITiaAdapter
    {
        private int writeCount;

        public Task<ProjectContext?> GetProjectContextAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectContext?>(new ProjectContext(projectId, "snapshot-1"));

        public Task<TiaAdapterExecutionResult> CreateBlockAsync(
            CreateBlockOperation operation,
            CancellationToken cancellationToken,
            string? sclSourceText = null)
        {
            writeCount++;
            return Task.FromResult(new TiaAdapterExecutionResult(
                new ProjectContext(operation.ProjectContext.ProjectId, $"snapshot-{writeCount + 1}"),
                []));
        }
    }

    private sealed class StaticProjectContextReadService : IProjectContextReadService
    {
        public Task<ProjectContextReadResult> GetProjectContextAsync(
            string projectId,
            AuthenticatedIdentity identity,
            Guid operationId,
            CancellationToken cancellationToken) => Task.FromResult(
                new ProjectContextReadResult(new ProjectContext(projectId, "snapshot-1"), []));
    }

    private sealed class StaticProjectBlockCatalogReadService : IProjectBlockCatalogReadService
    {
        public Task<ProjectBlockCatalogReadResult> GetBlockCatalogAsync(
            string projectId,
            int startIndex,
            int maximumBlockCount,
            string? expectedSnapshotHash,
            AuthenticatedIdentity identity,
            Guid operationId,
            CancellationToken cancellationToken) => Task.FromResult(new ProjectBlockCatalogReadResult(
            new ProjectBlockCatalogPage(
                new ProjectContext(projectId, "snapshot-1"),
                [new ProjectBlock("PLC_1", "FB_Motor", string.Empty, 1, "Scl")],
                6,
                6),
            []));
    }

    private static string Request(string method, object parameters) => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id = Guid.NewGuid().ToString("N"),
        method,
        @params = parameters
    });

    private static string Notification(string method) => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        method
    });

    private static string InitializeRequest() => Request("initialize", new
    {
        protocolVersion = McpJsonRpcRequestProcessor.ProtocolVersion,
        capabilities = new { },
        clientInfo = new { name = "test-client", version = "1.0.0" }
    });

    private static CreateBlockOperation CreateOperation() => new(
        Guid.NewGuid(),
        new ProjectContext("project-1", "snapshot-1"),
        "create-fb-motor",
        "FB_Motor",
        BlockType.FunctionBlock,
        ProgrammingLanguage.Scl,
        new BlockInterface([]));

    private static CreateBlockOperation CreateSclPreviewOperation() => new(
        Guid.NewGuid(),
        new ProjectContext("project-1", "snapshot-1"),
        "preview-fb-motor",
        "FB_Motor",
        BlockType.FunctionBlock,
        ProgrammingLanguage.Scl,
        new BlockInterface(
            [new BlockParameter("Start", "Bool")],
            [new BlockParameter("Running", "Bool")]),
        [new SclAssignment("Running", "Start")]);
}
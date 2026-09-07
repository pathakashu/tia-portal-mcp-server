using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EngineerPc.Contracts;
using EngineerPc.Engineering.Engine;
using EngineerPc.Engineering.Ir;
using EngineerPc.Mcp;

namespace EngineerPc.Mcp.Host;

public sealed class McpJsonRpcRequestProcessor
{
    public const string ProtocolVersion = "2025-06-18";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly McpSessionManager sessionManager;
    private readonly McpToolRouter toolRouter;
    private readonly bool projectContextReadEnabled;
    private readonly bool blockCatalogReadEnabled;

    public McpJsonRpcRequestProcessor(
        McpSessionManager sessionManager,
        McpToolRouter toolRouter,
        bool projectContextReadEnabled = false,
        bool blockCatalogReadEnabled = false)
    {
        this.sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        this.toolRouter = toolRouter ?? throw new ArgumentNullException(nameof(toolRouter));
        this.projectContextReadEnabled = projectContextReadEnabled;
        this.blockCatalogReadEnabled = blockCatalogReadEnabled;
    }

    public McpJsonRpcProcessingResult Process(
        string message,
        AuthenticatedPrincipal principal,
        string? sessionHeader,
        string? protocolVersionHeader) => ProcessAsync(
            message,
            principal,
            sessionHeader,
            protocolVersionHeader,
            CancellationToken.None).GetAwaiter().GetResult();

    public Task<McpJsonRpcProcessingResult> ProcessAsync(
        string message,
        AuthenticatedPrincipal principal,
        string? sessionHeader,
        string? protocolVersionHeader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        McpJsonRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<McpJsonRpcRequest>(message, SerializerOptions);
        }
        catch (JsonException)
        {
            return Task.FromResult(Error(null, -32700, "Parse error."));
        }

        if (request is null || request.JsonRpc != "2.0" || string.IsNullOrWhiteSpace(request.Method))
        {
            return Task.FromResult(Error(request?.Id, -32600, "Invalid JSON-RPC request."));
        }

        if (!string.Equals(request.Method, "initialize", StringComparison.Ordinal) &&
            !string.Equals(protocolVersionHeader, ProtocolVersion, StringComparison.Ordinal))
        {
            return Task.FromResult(Error(request.Id, -32600, "Missing or unsupported MCP-Protocol-Version header.", statusCode: 400));
        }

        return request.Method switch
        {
            "initialize" => Task.FromResult(Initialize(request, principal, sessionHeader)),
            "notifications/initialized" => Task.FromResult(InitializeSession(request, sessionHeader)),
            "ping" => Task.FromResult(Ping(request)),
            "tools/list" => Task.FromResult(ListTools(request, sessionHeader)),
            "tools/call" => CallToolAsync(request, sessionHeader, cancellationToken),
            _ => Task.FromResult(Error(request.Id, -32601, "Method not found."))
        };
    }

    public static string SerializeResponse(McpJsonRpcResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var serializedResponse = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = response.Id
        };

        if (response.Error is not null)
        {
            serializedResponse["error"] = response.Error;
        }
        else
        {
            serializedResponse["result"] = response.Result;
        }

        return JsonSerializer.Serialize(serializedResponse, SerializerOptions);
    }

    private McpJsonRpcProcessingResult Initialize(
        McpJsonRpcRequest request,
        AuthenticatedPrincipal principal,
        string? sessionHeader)
    {
        if (!HasRequestId(request) || !string.IsNullOrWhiteSpace(sessionHeader))
        {
            return Error(request.Id, -32600, "Initialize requires a request ID and no MCP session header.");
        }

        if (request.Params.ValueKind != JsonValueKind.Object ||
            !request.Params.TryGetProperty("protocolVersion", out var requestedVersion) ||
            requestedVersion.ValueKind != JsonValueKind.String ||
            !request.Params.TryGetProperty("capabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Object ||
            !request.Params.TryGetProperty("clientInfo", out var clientInfo) ||
            clientInfo.ValueKind != JsonValueKind.Object ||
            !clientInfo.TryGetProperty("name", out var clientName) ||
            clientName.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(clientName.GetString()) ||
            !clientInfo.TryGetProperty("version", out var clientVersion) ||
            clientVersion.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(clientVersion.GetString()))
        {
            return Error(request.Id, -32602, "Initialize requires protocolVersion, capabilities, and clientInfo parameters.");
        }

        if (!string.Equals(requestedVersion.GetString(), ProtocolVersion, StringComparison.Ordinal))
        {
            return Error(request.Id, -32602, "Unsupported protocol version.");
        }

        var session = sessionManager.Connect(TimeSpan.FromMinutes(5));
        sessionManager.Authenticate(session.SessionId, principal);
        return new McpJsonRpcProcessingResult(
            new McpJsonRpcResponse(
                request.Id,
                new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "engineer-pc", version = "1.0.0" }
                },
                null),
            StatusCodes.Status200OK,
            session.SessionId.ToString("N"));
    }

    private McpJsonRpcProcessingResult InitializeSession(McpJsonRpcRequest request, string? sessionHeader)
    {
        if (HasRequestId(request) || !TryParseSessionId(sessionHeader, out var sessionId))
        {
            return Error(request.Id, -32600, "Initialized notification requires an MCP session header and no request ID.");
        }

        try
        {
            sessionManager.Initialize(sessionId);
            return new McpJsonRpcProcessingResult(null, StatusCodes.Status202Accepted, null);
        }
        catch (InvalidOperationException)
        {
            return Error(null, -32602, "MCP session cannot be initialized.");
        }
    }

    private McpJsonRpcProcessingResult Ping(McpJsonRpcRequest request)
    {
        if (!HasRequestId(request))
        {
            return Error(request.Id, -32600, "Ping requires a request ID.");
        }

        return Result(request.Id, new { });
    }

    private McpJsonRpcProcessingResult ListTools(McpJsonRpcRequest request, string? sessionHeader)
    {
        if (!HasRequestId(request))
        {
            return Error(request.Id, -32600, "Tools list requires a request ID.");
        }

        if (!TryGetReadySession(sessionHeader, out _))
        {
            return Error(request.Id, -32602, "MCP session is not ready for tool calls.");
        }

        var tools = new List<object>
        {
            new
            {
                name = "plan_create_block",
                title = "Plan Create Block",
                description = "Validates and creates an approval-gated plan to create a TIA block.",
                inputSchema = CreateBlockInputSchema()
            },
            new
            {
                name = "preview_scl_block",
                title = "Preview SCL Block",
                description = "Generates a deterministic constrained SCL source preview for a block intent.",
                inputSchema = CreateSclBlockPreviewInputSchema()
            }
        };
        if (projectContextReadEnabled)
        {
            tools.Add(new
            {
                name = "get_project_context",
                title = "Get Project Context",
                description = "Reads the snapshot context for a deployment-configured TIA V18 project.",
                inputSchema = GetProjectContextInputSchema()
            });
        }
        if (blockCatalogReadEnabled)
        {
            tools.Add(new
            {
                name = "get_block_catalog",
                title = "Get Block Catalog",
                description = "Reads the PLC block catalog for a deployment-configured TIA V18 project.",
                inputSchema = GetBlockCatalogInputSchema()
            });
        }

        return Result(request.Id, new { tools });
    }

    private async Task<McpJsonRpcProcessingResult> CallToolAsync(
        McpJsonRpcRequest request,
        string? sessionHeader,
        CancellationToken cancellationToken)
    {
        if (!HasRequestId(request))
        {
            return Error(request.Id, -32600, "Tools call requires a request ID.");
        }

        if (!TryGetReadySession(sessionHeader, out var sessionId))
        {
            return Error(request.Id, -32602, "MCP session is not ready for tool calls.");
        }

        if (request.Params.ValueKind != JsonValueKind.Object ||
            !request.Params.TryGetProperty("name", out var toolName) ||
            toolName.ValueKind != JsonValueKind.String)
        {
            return Error(request.Id, -32602, "Tool name is required.");
        }

        if (!request.Params.TryGetProperty("arguments", out var arguments))
        {
            return Error(request.Id, -32602, "Tool arguments are required.");
        }

        var requestId = CreateDeterministicRequestId(sessionId, request.Id!.Value);
        return toolName.GetString() switch
        {
            "plan_create_block" => CallPlanCreateBlock(request, sessionId, requestId, arguments),
            "preview_scl_block" => CallPreviewSclBlock(request, sessionId, requestId, arguments),
            "get_project_context" when projectContextReadEnabled => await CallGetProjectContextAsync(
                request,
                sessionId,
                requestId,
                arguments,
                cancellationToken),
            "get_block_catalog" when blockCatalogReadEnabled => await CallGetBlockCatalogAsync(
                request,
                sessionId,
                requestId,
                arguments,
                cancellationToken),
            _ => Error(request.Id, -32602, "Tool is not supported.")
        };
    }

    private McpJsonRpcProcessingResult CallPlanCreateBlock(
        McpJsonRpcRequest request,
        Guid sessionId,
        Guid requestId,
        JsonElement arguments)
    {
        CreateBlockOperation? operation;
        try
        {
            operation = arguments.Deserialize<CreateBlockOperation>(SerializerOptions);
        }
        catch (JsonException)
        {
            return Error(request.Id, -32602, "Tool arguments are invalid for plan_create_block.");
        }

        if (operation is null)
        {
            return Error(request.Id, -32602, "Tool arguments are invalid for plan_create_block.");
        }

        var result = toolRouter.PlanCreateBlock(new McpToolCall<CreateBlockOperation>(
            requestId,
            requestId,
            sessionId,
            McpTool.PlanCreateBlock,
            operation));
        object structuredResult = result.Payload is null
            ? new { errors = result.Errors }
            : result.Payload;
        return Result(request.Id, new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = JsonSerializer.Serialize(structuredResult, SerializerOptions)
                }
            },
            structuredContent = structuredResult,
            isError = !result.IsSuccess
        });
    }

    private McpJsonRpcProcessingResult CallPreviewSclBlock(
        McpJsonRpcRequest request,
        Guid sessionId,
        Guid requestId,
        JsonElement arguments)
    {
        CreateBlockOperation? operation;
        try
        {
            operation = arguments.Deserialize<CreateBlockOperation>(SerializerOptions);
        }
        catch (JsonException)
        {
            return Error(request.Id, -32602, "Tool arguments are invalid for preview_scl_block.");
        }

        if (operation is null)
        {
            return Error(request.Id, -32602, "Tool arguments are invalid for preview_scl_block.");
        }

        var result = toolRouter.PreviewSclBlock(new McpToolCall<CreateBlockOperation>(
            requestId,
            requestId,
            sessionId,
            McpTool.PreviewSclBlock,
            operation));
        object structuredResult = result.Payload is null
            ? new { errors = result.Errors }
            : result.Payload;
        return Result(request.Id, new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = JsonSerializer.Serialize(structuredResult, SerializerOptions)
                }
            },
            structuredContent = structuredResult,
            isError = !result.IsSuccess
        });
    }

    private async Task<McpJsonRpcProcessingResult> CallGetBlockCatalogAsync(
        McpJsonRpcRequest request,
        Guid sessionId,
        Guid requestId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        GetBlockCatalogRequest? blockCatalogRequest;
        try
        {
            blockCatalogRequest = arguments.Deserialize<GetBlockCatalogRequest>(SerializerOptions);
        }
        catch (JsonException)
        {
            return Error(request.Id, -32602, "Tool arguments are invalid for get_block_catalog.");
        }

        if (blockCatalogRequest is null || string.IsNullOrWhiteSpace(blockCatalogRequest.ProjectId))
        {
            return Error(request.Id, -32602, "Tool arguments are invalid for get_block_catalog.");
        }

        var result = await toolRouter.GetBlockCatalogAsync(
            new McpToolCall<GetBlockCatalogRequest>(
                requestId,
                requestId,
                sessionId,
                McpTool.GetBlockCatalog,
                blockCatalogRequest),
            cancellationToken);
        object structuredResult = result.Payload is null
            ? new { errors = result.Errors }
            : result.Payload;
        return Result(request.Id, new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = JsonSerializer.Serialize(structuredResult, SerializerOptions)
                }
            },
            structuredContent = structuredResult,
            isError = !result.IsSuccess
        });
    }

    private async Task<McpJsonRpcProcessingResult> CallGetProjectContextAsync(
        McpJsonRpcRequest request,
        Guid sessionId,
        Guid requestId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        GetProjectContextRequest? projectContextRequest;
        try
        {
            projectContextRequest = arguments.Deserialize<GetProjectContextRequest>(SerializerOptions);
        }
        catch (JsonException)
        {
            return Error(request.Id, -32602, "Tool arguments are invalid for get_project_context.");
        }

        if (projectContextRequest is null || string.IsNullOrWhiteSpace(projectContextRequest.ProjectId))
        {
            return Error(request.Id, -32602, "Tool arguments are invalid for get_project_context.");
        }

        var result = await toolRouter.GetProjectContextAsync(
            new McpToolCall<GetProjectContextRequest>(
                requestId,
                requestId,
                sessionId,
                McpTool.GetProjectContext,
                projectContextRequest),
            cancellationToken);
        object structuredResult = result.Payload is null
            ? new { errors = result.Errors }
            : result.Payload;
        return Result(request.Id, new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = JsonSerializer.Serialize(structuredResult, SerializerOptions)
                }
            },
            structuredContent = structuredResult,
            isError = !result.IsSuccess
        });
    }

    private bool TryGetReadySession(string? sessionHeader, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        return TryParseSessionId(sessionHeader, out sessionId) &&
            sessionManager.TryGetReadySession(sessionId, out _, out _);
    }

    private static bool TryParseSessionId(string? sessionHeader, out Guid sessionId) =>
        Guid.TryParseExact(sessionHeader, "N", out sessionId);

    private static bool HasRequestId(McpJsonRpcRequest request) =>
        request.Id is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined };

    private static Guid CreateDeterministicRequestId(Guid sessionId, JsonElement id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId:N}:{id.GetRawText()}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static object CreateBlockInputSchema() => new
    {
        type = "object",
        properties = new
        {
            operationId = new { type = "string", format = "uuid" },
            projectContext = new
            {
                type = "object",
                properties = new
                {
                    projectId = new { type = "string" },
                    snapshotHash = new { type = "string" }
                },
                required = new[] { "projectId", "snapshotHash" }
            },
            idempotencyKey = new { type = "string" },
            name = new { type = "string" },
            blockType = new { type = "string", @enum = new[] { "Function", "FunctionBlock", "OrganizationBlock" } },
            language = new { type = "string", @enum = new[] { "Scl", "Lad", "Fbd" } },
            @interface = new
            {
                type = "object",
                properties = new
                {
                    inputs = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new { name = new { type = "string" }, dataType = new { type = "string" } },
                            required = new[] { "name", "dataType" }
                        }
                    }
                },
                required = new[] { "inputs" }
            }
        },
        required = new[] { "operationId", "projectContext", "idempotencyKey", "name", "blockType", "language", "interface" }
    };

    private static object CreateSclBlockPreviewInputSchema() => new
    {
        type = "object",
        properties = new
        {
            operationId = new { type = "string", format = "uuid" },
            projectContext = new
            {
                type = "object",
                properties = new
                {
                    projectId = new { type = "string" },
                    snapshotHash = new { type = "string" }
                },
                required = new[] { "projectId", "snapshotHash" }
            },
            idempotencyKey = new { type = "string" },
            name = new { type = "string" },
            blockType = new { type = "string", @enum = new[] { "Function", "FunctionBlock" } },
            language = new { type = "string", @enum = new[] { "Scl" } },
            @interface = new
            {
                type = "object",
                properties = new
                {
                    inputs = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new { name = new { type = "string" }, dataType = new { type = "string" } },
                            required = new[] { "name", "dataType" }
                        }
                    },
                    outputs = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new { name = new { type = "string" }, dataType = new { type = "string" } },
                            required = new[] { "name", "dataType" }
                        }
                    }
                },
                required = new[] { "inputs" }
            },
            statements = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new { target = new { type = "string" }, source = new { type = "string" } },
                    required = new[] { "target", "source" }
                }
            }
        },
        required = new[] { "operationId", "projectContext", "idempotencyKey", "name", "blockType", "language", "interface" }
    };

    private static object GetProjectContextInputSchema() => new
    {
        type = "object",
        properties = new
        {
            projectId = new { type = "string" }
        },
        required = new[] { "projectId" }
    };

    private static object GetBlockCatalogInputSchema() => new
    {
        type = "object",
        properties = new
        {
            projectId = new { type = "string" },
            startIndex = new { type = "integer", minimum = 0 },
            maxBlocks = new { type = "integer", minimum = 1, maximum = ProjectBlockCatalogReadService.MaximumBlockCount },
            expectedSnapshotHash = new { type = "string" }
        },
        required = new[] { "projectId" }
    };

    private static McpJsonRpcProcessingResult Result(JsonElement? id, object result) =>
        new(new McpJsonRpcResponse(id, result, null), StatusCodes.Status200OK, null);

    private static McpJsonRpcProcessingResult Error(JsonElement? id, int code, string message, int statusCode = StatusCodes.Status200OK) =>
        new(new McpJsonRpcResponse(id, null, new McpJsonRpcError(code, message)), statusCode, null);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record McpJsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement Params);

public sealed record McpJsonRpcResponse(
    JsonElement? Id,
    object? Result,
    McpJsonRpcError? Error);

public sealed record McpJsonRpcError(int Code, string Message);

public sealed record McpJsonRpcProcessingResult(
    McpJsonRpcResponse? Response,
    int StatusCode,
    string? SessionId);
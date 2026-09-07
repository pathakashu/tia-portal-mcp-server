namespace EngineerPc.Mcp;

public sealed record McpToolCall<TPayload>(
    Guid RequestId,
    Guid CorrelationId,
    Guid SessionId,
    McpTool Tool,
    TPayload Payload);

public enum McpTool
{
    PlanCreateBlock,
    PreviewSclBlock,
    GetProjectContext,
    GetBlockCatalog
}
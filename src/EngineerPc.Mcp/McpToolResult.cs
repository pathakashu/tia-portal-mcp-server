namespace EngineerPc.Mcp;

public sealed record McpToolResult<TPayload>(
    Guid RequestId,
    Guid CorrelationId,
    TPayload? Payload,
    IReadOnlyList<string> Errors)
{
    public bool IsSuccess => Payload is not null && Errors.Count == 0;
}
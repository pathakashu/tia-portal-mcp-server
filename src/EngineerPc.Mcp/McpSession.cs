using EngineerPc.Contracts;

namespace EngineerPc.Mcp;

public sealed record McpSession(
    Guid SessionId,
    AuthenticatedPrincipal? Principal,
    McpSessionState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public enum McpSessionState
{
    Connected,
    Authenticated,
    Ready,
    Disconnected,
    Expired
}
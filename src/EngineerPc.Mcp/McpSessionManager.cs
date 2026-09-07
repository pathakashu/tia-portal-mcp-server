using System.Collections.Concurrent;
using EngineerPc.Contracts;

namespace EngineerPc.Mcp;

public sealed class McpSessionManager
{
    private readonly ConcurrentDictionary<Guid, McpSession> sessions = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, byte>> processedRequestIds = new();
    private readonly TimeProvider timeProvider;

    public McpSessionManager(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public McpSession Connect(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Session duration must be positive.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var session = new McpSession(
            Guid.NewGuid(),
            null,
            McpSessionState.Connected,
            nowUtc,
            nowUtc.Add(duration));
        sessions.TryAdd(session.SessionId, session);
        return session;
    }

    public McpSession Authenticate(Guid sessionId, AuthenticatedPrincipal validatedPrincipal)
    {
        ArgumentNullException.ThrowIfNull(validatedPrincipal);
        return Transition(sessionId, McpSessionState.Connected, McpSessionState.Authenticated, validatedPrincipal);
    }

    public McpSession Initialize(Guid sessionId) =>
        Transition(sessionId, McpSessionState.Authenticated, McpSessionState.Ready, null);

    public bool TryGetReadySession(Guid sessionId, out McpSession? session, out string? error)
    {
        session = null;
        error = null;

        if (!sessions.TryGetValue(sessionId, out var storedSession))
        {
            error = "MCP session was not found.";
            return false;
        }

        if (storedSession.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            var expiredSession = storedSession with { State = McpSessionState.Expired };
            sessions.TryUpdate(sessionId, expiredSession, storedSession);
            error = "MCP session has expired.";
            return false;
        }

        if (storedSession.State != McpSessionState.Ready)
        {
            error = "MCP session is not ready for tool calls.";
            return false;
        }

        session = storedSession;
        return true;
    }

    public bool TryRecordRequest(Guid sessionId, Guid requestId, out string? error)
    {
        error = null;

        if (requestId == Guid.Empty)
        {
            error = "Request ID is required.";
            return false;
        }

        var sessionRequests = processedRequestIds.GetOrAdd(sessionId, _ => new ConcurrentDictionary<Guid, byte>());
        if (!sessionRequests.TryAdd(requestId, 0))
        {
            error = "Request ID has already been processed for this session.";
            return false;
        }

        return true;
    }

    private McpSession Transition(
        Guid sessionId,
        McpSessionState expectedState,
        McpSessionState nextState,
        AuthenticatedPrincipal? principal)
    {
        if (!sessions.TryGetValue(sessionId, out var session))
        {
            throw new InvalidOperationException("MCP session was not found.");
        }

        if (session.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            var expiredSession = session with { State = McpSessionState.Expired };
            sessions.TryUpdate(sessionId, expiredSession, session);
            throw new InvalidOperationException("MCP session has expired.");
        }

        if (session.State != expectedState)
        {
            throw new InvalidOperationException(
                $"MCP session cannot transition from '{session.State}' to '{nextState}'.");
        }

        var nextSession = session with { State = nextState, Principal = principal ?? session.Principal };
        if (!sessions.TryUpdate(sessionId, nextSession, session))
        {
            throw new InvalidOperationException("MCP session changed during transition.");
        }

        return nextSession;
    }
}
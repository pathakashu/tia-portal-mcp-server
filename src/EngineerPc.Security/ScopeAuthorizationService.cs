using EngineerPc.Contracts;

namespace EngineerPc.Security;

public sealed class ScopeAuthorizationService : IAuthorizationService
{
    private readonly IReadOnlyDictionary<string, AuthorizationRule> rules;
    private readonly ISecurityEventSink eventSink;
    private readonly TimeProvider timeProvider;

    public ScopeAuthorizationService(
        IEnumerable<AuthorizationRule> rules,
        ISecurityEventSink eventSink,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(rules);

        this.rules = rules.ToDictionary(rule => rule.Operation, StringComparer.Ordinal);
        this.eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public AuthorizationDecision Authorize(
        AuthenticatedPrincipal principal,
        string operation,
        Guid requestId,
        Guid correlationId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (!rules.TryGetValue(operation, out var rule))
        {
            return Deny(principal, operation, requestId, correlationId, "Operation is not configured for authorization.");
        }

        var hasRole = principal.Roles.Any(role => string.Equals(role, rule.RequiredRole, StringComparison.Ordinal));
        var hasScope = principal.Scopes.Any(scope => string.Equals(scope, rule.RequiredScope, StringComparison.Ordinal));
        if (!hasRole || !hasScope)
        {
            return Deny(
                principal,
                operation,
                requestId,
                correlationId,
                "Authenticated principal does not satisfy the required role and scope.");
        }

        eventSink.Record(new SecurityEvent(
            SecurityEventType.AuthorizationAllowed,
            timeProvider.GetUtcNow(),
            requestId,
            correlationId,
            principal.Identity,
            operation,
            "Role and scope authorization succeeded."));
        return new AuthorizationDecision(true, null);
    }

    private AuthorizationDecision Deny(
        AuthenticatedPrincipal principal,
        string operation,
        Guid requestId,
        Guid correlationId,
        string reason)
    {
        eventSink.Record(new SecurityEvent(
            SecurityEventType.AuthorizationDenied,
            timeProvider.GetUtcNow(),
            requestId,
            correlationId,
            principal.Identity,
            operation,
            reason));
        return new AuthorizationDecision(false, reason);
    }
}
namespace EngineerPc.Security;

public sealed record AuthorizationRule(
    string Operation,
    string RequiredRole,
    string RequiredScope);

public sealed record AuthorizationDecision(
    bool IsAllowed,
    string? DenialReason);
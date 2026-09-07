namespace EngineerPc.Contracts;

public sealed record AuthenticatedPrincipal(
    AuthenticatedIdentity Identity,
    IReadOnlySet<string> Roles,
    IReadOnlySet<string> Scopes);
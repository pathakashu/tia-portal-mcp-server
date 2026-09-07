namespace EngineerPc.Contracts;

public sealed record AuthenticatedIdentity(
    string SubjectId,
    string ClientId);
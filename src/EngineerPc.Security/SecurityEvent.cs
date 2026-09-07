using EngineerPc.Contracts;

namespace EngineerPc.Security;

public sealed record SecurityEvent(
    SecurityEventType EventType,
    DateTimeOffset OccurredAtUtc,
    Guid RequestId,
    Guid CorrelationId,
    AuthenticatedIdentity Identity,
    string Operation,
    string Detail);

public enum SecurityEventType
{
    AuthorizationAllowed,
    AuthorizationDenied
}
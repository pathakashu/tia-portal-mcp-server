using System.Collections.Concurrent;

namespace EngineerPc.Security;

public sealed class InMemorySecurityEventSink : ISecurityEventSink
{
    private readonly ConcurrentQueue<SecurityEvent> events = new();

    public IReadOnlyList<SecurityEvent> Events => events.ToArray();

    public void Record(SecurityEvent securityEvent)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);
        events.Enqueue(securityEvent);
    }
}
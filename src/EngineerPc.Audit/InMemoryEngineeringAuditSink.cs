using System.Collections.Concurrent;

namespace EngineerPc.Audit;

public sealed class InMemoryEngineeringAuditSink : IEngineeringAuditSink
{
    private readonly ConcurrentQueue<EngineeringAuditEvent> events = new();

    public IReadOnlyList<EngineeringAuditEvent> Events => events.ToArray();

    public void Record(EngineeringAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        events.Enqueue(auditEvent);
    }
}

public sealed class NullEngineeringAuditSink : IEngineeringAuditSink
{
    public static NullEngineeringAuditSink Instance { get; } = new();

    private NullEngineeringAuditSink()
    {
    }

    public void Record(EngineeringAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
    }
}
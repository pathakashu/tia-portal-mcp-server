using System.Text.Json;
using EngineerPc.Audit;
using EngineerPc.Contracts;
using EngineerPc.Security;

namespace EngineerPc.Audit.Tests;

public sealed class JsonLinesSecurityEventSinkTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), "EngineerPc.Audit.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Record_AppendsDurableJsonLinesThatCanBeReadAfterReopeningSink()
    {
        var filePath = Path.Combine(temporaryDirectory, "security-events.jsonl");
        var firstEvent = CreateEvent(SecurityEventType.AuthorizationAllowed, "Planning permitted.");
        var secondEvent = CreateEvent(SecurityEventType.AuthorizationDenied, "Scope missing.");

        new JsonLinesSecurityEventSink(filePath).Record(firstEvent);
        new JsonLinesSecurityEventSink(filePath).Record(secondEvent);

        var persistedEvents = File.ReadAllLines(filePath)
            .Select(line => JsonSerializer.Deserialize<SecurityEvent>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .ToList();

        Assert.Equal(2, persistedEvents.Count);
        Assert.Equal(firstEvent, persistedEvents[0]);
        Assert.Equal(secondEvent, persistedEvents[1]);
    }

    [Fact]
    public void Record_EngineeringEventPersistsWithoutAnOperationPayload()
    {
        var filePath = Path.Combine(temporaryDirectory, "engineering-events.jsonl");
        var auditEvent = new EngineeringAuditEvent(
            EngineeringAuditEventType.PlanAwaitingApproval,
            new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "project-1",
            "snapshot-1",
            new AuthenticatedIdentity("engineer-1", "certificate-1"),
            "Planning requires human approval.");

        new JsonLinesEngineeringAuditSink(filePath).Record(auditEvent);

        var persistedEvent = JsonSerializer.Deserialize<EngineeringAuditEvent>(
            File.ReadAllText(filePath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(auditEvent, persistedEvent);
    }

    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static SecurityEvent CreateEvent(SecurityEventType eventType, string detail) => new(
        eventType,
        new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero),
        Guid.NewGuid(),
        Guid.NewGuid(),
        new AuthenticatedIdentity("engineer-1", "certificate-1"),
        "PlanCreateBlock",
        detail);
}
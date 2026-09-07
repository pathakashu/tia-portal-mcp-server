using EngineerPc.Contracts;

namespace EngineerPc.Security.Tests;

public sealed class ScopeAuthorizationServiceTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Authorize_WithRequiredRoleAndScope_AllowsAndAudits()
    {
        var eventSink = new InMemorySecurityEventSink();
        var authorization = CreateAuthorizationService(eventSink);
        var principal = CreatePrincipal(["Engineer"], ["engineering.plan"]);

        var decision = authorization.Authorize(principal, "PlanCreateBlock", Guid.NewGuid(), Guid.NewGuid());

        Assert.True(decision.IsAllowed);
        var securityEvent = Assert.Single(eventSink.Events);
        Assert.Equal(SecurityEventType.AuthorizationAllowed, securityEvent.EventType);
        Assert.Equal("PlanCreateBlock", securityEvent.Operation);
    }

    [Fact]
    public void Authorize_WithoutRequiredScope_DeniesAndAudits()
    {
        var eventSink = new InMemorySecurityEventSink();
        var authorization = CreateAuthorizationService(eventSink);
        var principal = CreatePrincipal(["Engineer"], []);

        var decision = authorization.Authorize(principal, "PlanCreateBlock", Guid.NewGuid(), Guid.NewGuid());

        Assert.False(decision.IsAllowed);
        Assert.Equal("Authenticated principal does not satisfy the required role and scope.", decision.DenialReason);
        Assert.Equal(SecurityEventType.AuthorizationDenied, Assert.Single(eventSink.Events).EventType);
    }

    [Fact]
    public void Authorize_WithUnconfiguredOperation_DeniesByDefault()
    {
        var eventSink = new InMemorySecurityEventSink();
        var authorization = CreateAuthorizationService(eventSink);

        var decision = authorization.Authorize(
            CreatePrincipal(["Engineer"], ["engineering.plan"]),
            "ExecuteShellCommand",
            Guid.NewGuid(),
            Guid.NewGuid());

        Assert.False(decision.IsAllowed);
        Assert.Equal("Operation is not configured for authorization.", decision.DenialReason);
    }

    private static ScopeAuthorizationService CreateAuthorizationService(ISecurityEventSink eventSink) => new(
        [new AuthorizationRule("PlanCreateBlock", "Engineer", "engineering.plan")],
        eventSink,
        new TestTimeProvider(NowUtc));

    private static AuthenticatedPrincipal CreatePrincipal(
        IEnumerable<string> roles,
        IEnumerable<string> scopes) => new(
            new AuthenticatedIdentity("engineer-1", "client-1"),
            roles.ToHashSet(StringComparer.Ordinal),
            scopes.ToHashSet(StringComparer.Ordinal));

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
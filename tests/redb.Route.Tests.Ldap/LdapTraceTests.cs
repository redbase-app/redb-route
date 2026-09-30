using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Ldap;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.Ldap;

/// <summary>
/// A directory entry carries no trace context: the WATCH consumer opens a root span per routed entry, never a child of
/// the activity the poll loop inherited from whoever started it, and none for a poll that finds nothing. A failed
/// route marks it red. The producer is a <c>Client</c> span, red when the operation fails. <c>EnableTelemetry=false</c>
/// opens none of these spans. The tests only read the seeded <c>ou=users</c> entries; a marker in the requested
/// attributes keeps each test's endpoint apart. Expects OpenLDAP at localhost:389 (cn=admin,dc=redb,dc=test / admin).
/// </summary>
[Trait("Category", "Integration")]
public sealed class LdapTraceTests
{
    private const string Connection =
        "server=localhost&port=389&bindDn=cn=admin,dc=redb,dc=test&bindPassword=admin";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _marker = $"trace{Guid.NewGuid():N}";

    /// <summary>A WATCH endpoint; <paramref name="filter"/> null takes every seeded user.</summary>
    private string WatchUri(string? filter = null) =>
        $"ldap:WATCH:ou=users,dc=redb,dc=test?{Connection}" +
        $"&filter={filter ?? "(objectClass=inetOrgPerson)"}&attributes=cn,mail,{_marker}&initialLoad=true&pollInterval=300";

    private RouteTelemetryProbe Spans(ActivityKind kind)
        => new(a => a.Kind == kind && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_marker, StringComparison.Ordinal) == true);

    private async Task<RouteContext> StartConsumer(Action<IExchange> step, string? filter = null, bool telemetry = true)
    {
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new LdapComponent());
        ctx.AddRoutes(r => r.From(WatchUri(filter)).Process(step));
        using (new Activity("host startup").SetIdFormat(ActivityIdFormat.W3C).Start())
            await ctx.Start();
        return ctx;
    }

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(100);
    }

    [Fact]
    public async Task Each_entry_opens_a_root_span_even_under_an_ambient_activity()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(_ => { }))
            await Until(() => probe.Activities.Count >= 5);

        probe.Activities.Should().HaveCountGreaterThanOrEqualTo(5, "one span per routed entry of the initial load");
        probe.Activities.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "an entry carries no context, so its span starts a trace rather than joining the host's");
    }

    [Fact]
    public async Task A_poll_that_finds_nothing_opens_no_span()
    {
        using var probe = Spans(ActivityKind.Consumer);

        // A filter nothing matches: every poll, the initial load included, comes back empty.
        await using (await StartConsumer(_ => { }, filter: $"(cn={_marker})"))
            await Task.Delay(1500);

        probe.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_route_marks_the_span_red()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(_ => throw new InvalidOperationException("route failed")))
            await Until(() => probe.Activities.Count >= 1);

        probe.Activities.Should().NotBeEmpty().And.OnlyContain(a => a.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Tracing_off_opens_no_span()
    {
        using var probe = new RouteTelemetryProbe(a =>
            RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_marker, StringComparison.Ordinal) == true);
        var routed = new TaskCompletionSource();

        await using (await StartConsumer(_ => routed.TrySetResult(), telemetry: false))
            (await Task.WhenAny(routed.Task, Task.Delay(Wait))).Should().Be(routed.Task);

        probe.Activities.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_operation_marks_the_client_span_red_and_tracing_off_opens_none(bool telemetry)
    {
        using var probe = Spans(ActivityKind.Client);
        await using var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new LdapComponent());
        await ctx.Start();
        using var template = new ProducerTemplate(ctx);
        template.Start();

        var act = () => template.SendAsync(
            $"ldap:DELETE:cn={_marker},ou=users,dc=redb,dc=test?{Connection}", new Exchange(new Message(null)));

        await act.Should().ThrowAsync<Exception>("the entry does not exist");
        if (telemetry)
            probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
        else
            probe.Activities.Should().BeEmpty();
    }
}

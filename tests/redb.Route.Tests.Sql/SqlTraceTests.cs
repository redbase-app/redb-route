using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Telemetry;
using redb.Route.Tests.Sql.E2E.Infrastructure;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.Sql;

/// <summary>
/// A polled row carries no trace context: the consumer opens a root span per routed exchange — a row, or the list of a
/// batch — never a child of the activity the poll runs under, and none for an empty poll. A failed route marks it red,
/// our own stop does not; the producers mark a failed statement red. <c>EnableTelemetry=false</c> opens none of these
/// spans. Each test tags its query with a marker and reads only the spans of that endpoint.
/// </summary>
public sealed class SqlTraceTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();
    private readonly string _marker = $"trace-{Guid.NewGuid():N}";

    public SqlTraceTests()
    {
        _db.Execute("CREATE TABLE outbox (id INTEGER NOT NULL, message TEXT NOT NULL)");
    }

    public void Dispose() => _db.Dispose();

    private string Query => $"SELECT id, message FROM outbox /* {_marker} */";

    private RouteTelemetryProbe Spans(ActivityKind kind)
        => new(a => a.Kind == kind && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_marker, StringComparison.Ordinal) == true);

    private void Rows(int count)
    {
        for (var i = 1; i <= count; i++)
            _db.Execute($"INSERT INTO outbox (id, message) VALUES ({i}, 'm{i}')");
    }

    private static IProcessor Route(Func<IExchange, CancellationToken, Task>? step = null)
    {
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => step?.Invoke(ci.Arg<IExchange>(), ci.Arg<CancellationToken>()) ?? Task.CompletedTask);
        return processor;
    }

    private async Task Poll(IProcessor processor, Dictionary<string, string>? extra = null, bool telemetry = true,
        CancellationToken ct = default)
    {
        await using var context = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        var parameters = new Dictionary<string, string> { ["mode"] = "Poll", ["delay"] = "100", ["repeatCount"] = "1" };
        foreach (var (key, value) in extra ?? [])
            parameters[key] = value;
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), Query, parameters);
        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(ct);
    }

    [Fact]
    public async Task Each_row_opens_a_root_span_even_under_an_ambient_activity()
    {
        Rows(2);
        using var probe = Spans(ActivityKind.Consumer);

        using (new Activity("poll loop").SetIdFormat(ActivityIdFormat.W3C).Start())
            await Poll(Route());

        probe.Activities.Should().HaveCount(2, "one span per routed row");
        probe.Activities.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "a row carries no context, so its span starts a trace rather than joining the poll loop's");
        probe.Activities[0].GetTagItem("db.system").Should().Be("sql");
    }

    [Fact]
    public async Task A_batch_delivered_as_a_list_opens_one_span()
    {
        Rows(3);
        using var probe = Spans(ActivityKind.Consumer);

        await Poll(Route(), new() { ["pollDelivery"] = "List" });

        probe.Activities.Should().ContainSingle();
    }

    [Fact]
    public async Task An_empty_poll_opens_no_span()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await Poll(Route());

        probe.Activities.Should().BeEmpty();
    }

    [Theory]
    [InlineData("routeEmptyResultSet")]
    [InlineData("sendEmptyMessageWhenIdle")]
    public async Task An_empty_poll_that_still_runs_the_route_opens_its_span(string option)
    {
        using var probe = Spans(ActivityKind.Consumer);
        var routed = 0;

        await Poll(Route((_, _) => { routed++; return Task.CompletedTask; }), new() { [option] = "true" });

        routed.Should().Be(1, "the option routes one empty exchange");
        probe.Activities.Should().ContainSingle("an exchange the route runs has its span")
            .Which.ParentSpanId.Should().Be(default(ActivitySpanId));
    }

    [Fact]
    public async Task A_failed_route_marks_the_span_red()
    {
        Rows(1);
        using var probe = Spans(ActivityKind.Consumer);

        await Poll(Route((_, _) => throw new InvalidOperationException("route failed")));

        probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Our_own_stop_does_not_mark_the_span_red()
    {
        Rows(1);
        using var probe = Spans(ActivityKind.Consumer);
        using var stop = new CancellationTokenSource();

        var act = () => Poll(Route((_, _) =>
        {
            stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        }), ct: stop.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        probe.Activities.Should().ContainSingle().Which.Status.Should().NotBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Tracing_off_opens_no_span()
    {
        Rows(2);
        using var probe = new RouteTelemetryProbe(a =>
            RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_marker, StringComparison.Ordinal) == true);

        await Poll(Route(), telemetry: false);

        probe.Activities.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_statement_marks_the_producer_span_red_and_tracing_off_opens_none(bool telemetry)
    {
        using var probe = Spans(ActivityKind.Client);
        await using var context = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(),
            $"SELECT * FROM no_such_table /* {_marker} */");
        var producer = endpoint.CreateProducer();

        var act = () => producer.Process(new Exchange(new Message(null)), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        if (telemetry)
            probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
        else
            probe.Activities.Should().BeEmpty();
    }
}

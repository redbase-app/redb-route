using System.Diagnostics;
using Elastic.Clients.Elasticsearch;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Elasticsearch;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.Elasticsearch;

/// <summary>
/// A polled document carries no trace context: the consumer opens a root span per routed hit, never a child of the
/// activity the poll loop inherited from whoever started it, and none for an empty poll. A failed route marks it red.
/// The producer marks a failed call red. <c>EnableTelemetry=false</c> opens none of these spans. Each test works on an
/// index of its own, reads only the spans of that endpoint and deletes the index by name.
/// Expects Elasticsearch at localhost:9200.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ElasticsearchTraceTests : IAsyncLifetime
{
    private const string Nodes = "http://localhost:9200";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _index = $"trace-{Guid.NewGuid():N}";
    private ElasticsearchClient _raw = null!;

    public async Task InitializeAsync()
    {
        _raw = new ElasticsearchClient(new ElasticsearchClientSettings(new Uri(Nodes)));
        var created = await _raw.Indices.CreateAsync(_index);
        if (!created.IsValidResponse)
            throw new InvalidOperationException($"The test index was not created: {created.DebugInformation}");
    }

    public async Task DisposeAsync()
    {
        var deleted = await _raw.Indices.DeleteAsync(_index, d => d.IgnoreUnavailable(true));
        if (!deleted.IsValidResponse)
            throw new InvalidOperationException($"The test index was not deleted: {deleted.DebugInformation}");
    }

    private string ConsumerUri =>
        $"elasticsearch://{_index}?nodes={Nodes}&deleteAfterRead=true&delay=200&initialDelay=50&size=10&refresh=wait_for";

    private RouteTelemetryProbe Spans(ActivityKind kind)
        => new(a => a.Kind == kind && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_index, StringComparison.Ordinal) == true);

    private async Task Seed(string id)
    {
        var response = await _raw.IndexAsync(new { v = id }, i => i.Index(_index).Id(id).Refresh(Refresh.WaitFor));
        if (!response.IsValidResponse)
            throw new InvalidOperationException($"The document was not indexed: {response.DebugInformation}");
    }

    private async Task<RouteContext> StartConsumer(Action<IExchange> step, bool telemetry = true)
    {
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new ElasticsearchComponent());
        ctx.AddRoutes(r => r.From(ConsumerUri).Process(step));
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
    public async Task Each_hit_opens_a_root_span_even_under_an_ambient_activity()
    {
        await Seed("a");
        await Seed("b");
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(_ => { }))
            await Until(() => probe.Activities.Count >= 2);

        probe.Activities.Should().HaveCount(2, "one span per routed hit");
        probe.Activities.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "a document carries no context, so its span starts a trace rather than joining the host's");
    }

    [Fact]
    public async Task An_empty_poll_opens_no_span()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(_ => { }))
            await Task.Delay(1200);   // several polls of an empty index

        probe.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_route_marks_the_span_red()
    {
        await Seed("fail");
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(_ => throw new InvalidOperationException("route failed")))
            await Until(() => probe.Activities.Count >= 1);

        probe.Activities.Should().NotBeEmpty().And.OnlyContain(a => a.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_failure_that_escapes_the_route_keeps_the_document_with_deleteAfterRead()
    {
        await Seed("kept");
        var attempted = new TaskCompletionSource();
        await using (var ctx = new RouteContext())
        {
            ctx.AddComponent(new ElasticsearchComponent());
            ctx.AddRoutes(r =>
            {
                // An error handler passes a cancellation on rather than swallow it, so a timeout inside the route (an
                // HttpClient timeout is a TaskCanceledException) escapes the pipeline instead of staying on the exchange.
                r.OnException<InvalidOperationException>().Handled();
                r.From(ConsumerUri).Process(_ =>
                {
                    attempted.TrySetResult();
                    throw new TaskCanceledException("a call inside the route timed out");
                });
            });
            await ctx.Start();
            (await Task.WhenAny(attempted.Task, Task.Delay(Wait))).Should().Be(attempted.Task);
            await Task.Delay(500);   // past the delete the poll would have issued
        }

        var got = await _raw.GetAsync<object>(_index, "kept");
        got.Found.Should().BeTrue("a document whose route failed is not deleted, however the failure left the route");
    }

    [Fact]
    public async Task Tracing_off_opens_no_span()
    {
        await Seed("off");
        using var probe = new RouteTelemetryProbe(a =>
            RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_index, StringComparison.Ordinal) == true);
        var routed = new TaskCompletionSource();

        await using (await StartConsumer(_ => routed.TrySetResult(), telemetry: false))
            (await Task.WhenAny(routed.Task, Task.Delay(Wait))).Should().Be(routed.Task);

        probe.Activities.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_call_marks_the_producer_span_red_and_tracing_off_opens_none(bool telemetry)
    {
        using var probe = Spans(ActivityKind.Client);
        await using var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new ElasticsearchComponent());
        await ctx.Start();
        using var template = new ProducerTemplate(ctx);
        template.Start();
        var exchange = new Exchange(new Message(null));
        exchange.In.Headers[ElasticsearchHeaders.DocumentId] = "no-such-document";

        var act = () => template.SendAsync($"elasticsearch://Delete:{_index}?nodes={Nodes}", exchange);

        await act.Should().ThrowAsync<Exception>();
        if (telemetry)
            probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
        else
            probe.Activities.Should().BeEmpty();
    }
}

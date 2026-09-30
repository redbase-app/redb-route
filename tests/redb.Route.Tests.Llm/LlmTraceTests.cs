using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Tests.Llm.TestHelpers;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.Llm;

/// <summary>
/// A scheduled agent opens a root span per tick, with the model call and the route under it, never a child of the
/// activity that started the routes; a failed tick marks it red, our own stop does not. The model call of the producer
/// is a transport span of its endpoint: it honours <c>EnableTelemetry</c> and a failed call marks it red. Stub providers
/// only: no model is called.
/// </summary>
public sealed class LlmTraceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _factory = $"trace{Guid.NewGuid():N}";

    private RouteTelemetryProbe Spans(ActivityKind kind)
        => new(a => a.Kind == kind && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_factory, StringComparison.Ordinal) == true);

    private async Task<LiveLlmHost> Start(ILlmProvider provider, Action<InlineRouteBuilder> routes, bool telemetry = true)
    {
        var host = LiveLlmHost.Build(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        host.AddFactory(_factory, new LlmConnectionFactory { Name = _factory, Provider = "fake", ModelId = "fake-model", PrebuiltProvider = provider });
        using (new Activity("host startup").SetIdFormat(ActivityIdFormat.W3C).Start())
            await host.StartAsync(routes);
        return host;
    }

    private string Scheduled(string schedule = "100ms") => $"llm://{_factory}?schedule={schedule}&initialBodyRef=go";

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    [Fact]
    public async Task Each_tick_opens_a_root_span_even_under_an_ambient_activity()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await Start(new FakeProvider(), r => r.From(Scheduled()).Process(_ => { })))
            await Until(() => probe.Activities.Count >= 2);

        probe.Activities.Should().HaveCountGreaterThanOrEqualTo(2, "one span per tick");
        probe.Activities.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "a tick starts a trace rather than joining the host's");
        probe.Activities.Select(a => a.TraceId).Should().OnlyHaveUniqueItems();
        probe.Activities.Should().OnlyContain(a => a.Status != ActivityStatusCode.Error);
    }

    [Fact]
    public async Task No_tick_opens_no_span()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await Start(new FakeProvider(), r => r.From(Scheduled("1h")).Process(_ => { })))
            await Task.Delay(800);

        probe.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_route_marks_the_tick_span_red()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await Start(new FakeProvider(), r => r.From(Scheduled()).Process(_ => throw new InvalidOperationException("route failed"))))
            await Until(() => probe.Activities.Count >= 1);

        probe.Activities.Should().NotBeEmpty();
        probe.Activities.Should().OnlyContain(a => a.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_failed_model_call_marks_the_tick_span_red()
    {
        using var probe = Spans(ActivityKind.Consumer);
        var provider = new FakeProvider { ThrowOnCall = new HttpRequestException("model unavailable") };

        await using (await Start(provider, r => r.From(Scheduled()).Process(_ => { })))
            await Until(() => probe.Activities.Count >= 1);

        probe.Activities.Should().NotBeEmpty();
        probe.Activities.Should().OnlyContain(a => a.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Our_stop_cancelling_the_tick_does_not_mark_the_span()
    {
        using var probe = Spans(ActivityKind.Consumer);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider
        {
            OnCall = async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            },
        };

        await using (await Start(provider, r => r.From(Scheduled()).Process(_ => { })))
        {
            (await Task.WhenAny(entered.Task, Task.Delay(Wait))).Should().Be(entered.Task);
        }

        probe.Activities.Should().ContainSingle().Which.Status.Should().NotBe(ActivityStatusCode.Error,
            "our own stop cancelling the tick is not a failure");
    }

    [Fact]
    public async Task The_model_call_is_a_span_of_its_endpoint()
    {
        using var probe = Spans(ActivityKind.Client);

        await using var host = await Start(new FakeProvider().EnqueueText("hi"), r => r.From("direct:agent").To($"llm://{_factory}"));
        await host.ProducerTemplate.SendAsync("direct:agent", "hello");

        var span = probe.Activities.Should().ContainSingle().Subject;
        span.Status.Should().NotBe(ActivityStatusCode.Error);
        RouteTelemetryProbe.Tag(span, "llm.provider").Should().Be("fake");
    }

    [Fact]
    public async Task A_failed_model_call_marks_the_producer_span_red()
    {
        using var probe = Spans(ActivityKind.Client);
        var provider = new FakeProvider { ThrowOnCall = new HttpRequestException("model unavailable") };

        await using var host = await Start(provider, r => r.From("direct:agent").To($"llm://{_factory}"));
        var send = () => host.ProducerTemplate.SendAsync("direct:agent", "hello");
        await send.Should().ThrowAsync<Exception>();

        probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_failed_streamed_run_marks_the_stream_span_red()
    {
        using var probe = Spans(ActivityKind.Client);
        var provider = new StreamingFakeProvider(new FakeProvider { ThrowOnCall = new HttpRequestException("model unavailable") });

        await using var host = await Start(provider, r => r.From("direct:agent").To($"llm://{_factory}?stream=body"));
        var exchange = await host.ProducerTemplate.RequestAsync("direct:agent", new Exchange(new Message("hello")));
        var body = (IAsyncEnumerable<string>)exchange.Out!.Body!;
        var read = async () => { await foreach (var _ in body) { } };
        await read.Should().ThrowAsync<Exception>();

        probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Tracing_off_opens_no_span_on_either_side()
    {
        using var probe = new RouteTelemetryProbe(a =>
            RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_factory, StringComparison.Ordinal) == true
            || RouteTelemetryProbe.Tag(a, "llm.provider") == "fake-off");
        var ticked = new TaskCompletionSource();
        var provider = new FakeProvider();

        var host = LiveLlmHost.Build(options: new RouteEngineOptions { EnableTelemetry = false });
        host.AddFactory(_factory, new LlmConnectionFactory { Name = _factory, Provider = "fake-off", ModelId = "fake-model", PrebuiltProvider = provider });
        await using (host)
        {
            await host.StartAsync(r =>
            {
                r.From(Scheduled()).Process(_ => ticked.TrySetResult());
                r.From("direct:agent").To($"llm://{_factory}");
            });
            (await Task.WhenAny(ticked.Task, Task.Delay(Wait))).Should().Be(ticked.Task);
            await host.ProducerTemplate.SendAsync("direct:agent", "hello");
        }

        probe.Activities.Should().BeEmpty();
    }
}

using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Processors;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.Core;

/// <summary>
/// <c>DrainableConsumer.ProcessWithTracking</c> is the one place a polling consumer hands an exchange to the route. A
/// failure that escapes the pipeline — a cancellation the consumer did not ask for, e.g. an HttpClient timeout through
/// an error handler that rethrows it — lands on the exchange, so a consumer deciding on the outcome (delete after read,
/// move on failure) sees a failed route instead of a clean one; the receive span, when given, is marked by the same
/// rule as every transport; and only the consumer's own stop is passed on.
/// </summary>
public class DrainableConsumerTrackingTests
{
    private sealed class Probe(IProcessor processor, IEndpoint endpoint) : global::redb.Route.Core.DrainableConsumer(processor)
    {
        protected override IEndpoint ConsumerEndpoint => endpoint;
        protected override string ConsumerName => "probe";
        protected override Task RunAsync(CancellationToken pollCt, CancellationToken processingCt) => Task.CompletedTask;

        public Task Track(IExchange exchange, CancellationToken ct) => ProcessWithTracking(exchange, ct);

        public Task Track(IExchange exchange, TransportSpan span, CancellationToken ct) => ProcessWithTracking(exchange, span, ct);
    }

    private sealed class StubEndpoint(string uri) : IEndpoint
    {
        public EndpointUri Uri { get; } = global::redb.Route.Core.EndpointUriParser.Parse(uri);
        public IComponent Component { get; } = new TimerComponent();
        public IProducer CreateProducer() => throw new NotSupportedException();
        public IConsumer CreateConsumer(IProcessor processor) => throw new NotSupportedException();
        public Task Start(CancellationToken ct = default) => Task.CompletedTask;
        public Task Stop(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (Probe Consumer, string Endpoint) Consumer(Func<IExchange, CancellationToken, Task> route)
    {
        var endpoint = $"probe://tracking-{Guid.NewGuid():N}";
        return (new Probe(new DelegateProcessor(route), new StubEndpoint(endpoint)), endpoint);
    }

    private static TransportSpan Span(string endpoint)
        => RouteTelemetryExtensions.StartConsumerSpan<object?>(null, "probe receive", ActivityKind.Consumer,
            "messaging.system", "probe", endpoint, null, static (_, _) => null);

    [Fact]
    public async Task A_cancellation_escaping_the_route_lands_on_the_exchange()
    {
        var (consumer, _) = Consumer((_, _) => throw new TaskCanceledException("a call inside the route timed out"));
        var exchange = new global::redb.Route.Core.Exchange(new global::redb.Route.Core.Message("x"));

        await consumer.Track(exchange, CancellationToken.None);

        exchange.Exception.Should().BeOfType<TaskCanceledException>("the consumer decides on the outcome after this");
    }

    [Fact]
    public async Task A_failure_escaping_the_route_marks_the_span()
    {
        var (consumer, endpoint) = Consumer((_, _) => throw new TaskCanceledException("timed out"));
        using var probe = RouteTelemetryProbe.ForEndpointContaining(endpoint);

        using (var span = Span(endpoint))
            await consumer.Track(new global::redb.Route.Core.Exchange(new global::redb.Route.Core.Message("x")), span, CancellationToken.None);

        probe.Activities.Single().Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_failure_the_route_left_on_the_exchange_marks_the_span()
    {
        var (consumer, endpoint) = Consumer((e, _) =>
        {
            e.Exception = new InvalidOperationException("route failed");
            return Task.CompletedTask;
        });
        using var probe = RouteTelemetryProbe.ForEndpointContaining(endpoint);

        using (var span = Span(endpoint))
            await consumer.Track(new global::redb.Route.Core.Exchange(new global::redb.Route.Core.Message("x")), span, CancellationToken.None);

        probe.Activities.Single().Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_handled_failure_and_a_clean_route_leave_the_span_unmarked()
    {
        var (consumer, endpoint) = Consumer((e, _) =>
        {
            e.Exception = new InvalidOperationException("handled");
            e.ExceptionHandled = true;
            return Task.CompletedTask;
        });
        using var probe = RouteTelemetryProbe.ForEndpointContaining(endpoint);

        using (var span = Span(endpoint))
            await consumer.Track(new global::redb.Route.Core.Exchange(new global::redb.Route.Core.Message("x")), span, CancellationToken.None);

        probe.Activities.Single().Status.Should().Be(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task The_consumers_own_stop_is_passed_on_and_is_not_a_failure()
    {
        using var stop = new CancellationTokenSource();
        var (consumer, endpoint) = Consumer(async (_, ct) =>
        {
            await stop.CancelAsync();
            ct.ThrowIfCancellationRequested();
        });
        using var probe = RouteTelemetryProbe.ForEndpointContaining(endpoint);

        using (var span = Span(endpoint))
        {
            var act = () => consumer.Track(new global::redb.Route.Core.Exchange(new global::redb.Route.Core.Message("x")), span, stop.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        probe.Activities.Single().Status.Should().Be(ActivityStatusCode.Unset);
    }
}

using System.Diagnostics.Metrics;
using FluentAssertions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Telemetry;

/// <summary>
/// The route metrics and the route span count exchanges, as <c>METRICS.md</c> says and the endpoint statistics do: one
/// exchange redelivered by an error handler is one measurement and one span, with its final outcome. They sat inside
/// the handlers declared on the route builder, so every redelivery attempt went through them again —
/// <c>redb.route.exchanges.failed</c> grew by the attempts, not by the exchanges.
/// </summary>
public class RouteMetricsCountExchangesTests
{
    private sealed class Counts : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly string _routeId;
        public int Processed;
        public int Failed;

        public Counts(string routeId)
        {
            _routeId = routeId;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RouteMetrics.MeterName
                    && instrument.Name is "redb.route.exchanges.processed" or "redb.route.exchanges.failed")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "redb.route.id" && Equals(tag.Value, _routeId))
                    {
                        if (instrument.Name == "redb.route.exchanges.processed") Interlocked.Add(ref Processed, (int)value);
                        else Interlocked.Add(ref Failed, (int)value);
                    }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    private static async Task<RouteContext> Route(string routeId, bool handled)
    {
        var context = new RouteContext($"metrics-{routeId}");
        context.AddComponent(new DirectComponent());
        context.AddRoutes(r =>
        {
            var onException = r.OnException<InvalidOperationException>().MaximumRedeliveries(2).RedeliveryDelay(TimeSpan.Zero);
            if (handled) onException.Handled();
            r.From($"direct://{routeId}").RouteId(routeId).Process(_ => throw new InvalidOperationException("down"));
        });
        await context.Start();
        return context;
    }

    private static async Task Send(RouteContext context, string routeId)
    {
        var template = new ProducerTemplate(context);
        template.Start();
        try { await template.SendAsync($"direct://{routeId}", "x"); }
        catch (InvalidOperationException) { }
        finally { await template.DisposeAsync(); }
    }

    [Fact]
    public async Task A_redelivered_exchange_that_fails_is_one_failed_exchange_and_one_span()
    {
        var routeId = $"metrics-failed-{Guid.NewGuid():N}";
        using var counts = new Counts(routeId);
        using var spans = RouteTelemetryProbe.ForRouteIdPrefix(routeId);
        await using var context = await Route(routeId, handled: false);

        await Send(context, routeId);

        counts.Failed.Should().Be(1, "three attempts are one exchange");
        counts.Processed.Should().Be(0);
        spans.Activities.Should().ContainSingle();
    }

    [Fact]
    public async Task A_redelivered_exchange_the_handler_handles_is_one_processed_exchange()
    {
        var routeId = $"metrics-handled-{Guid.NewGuid():N}";
        using var counts = new Counts(routeId);
        await using var context = await Route(routeId, handled: true);

        await Send(context, routeId);

        counts.Processed.Should().Be(1);
        counts.Failed.Should().Be(0);
    }
}

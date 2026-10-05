using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Telemetry;

/// <summary>
/// The route-level <c>.Tracing()</c> switch: a route may opt out of the global
/// <c>RouteEngineOptions.EnableTelemetry</c>, so a service route (a health check, a metric summary
/// on a timer) opens no span of its own and its single-span traces stop crowding the collector.
/// </summary>
[Collection("Telemetry")]
public class RouteTracingTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.Stop();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TracingFalse_OpensNoRouteSpan()
    {
        using var probe = RouteTelemetryProbe.ForRouteIdPrefix("direct-notrace-");

        _context.AddRoutes(r => r.From("direct://notrace-in")
            .Tracing(false)
            .Process(e => e.In.Body = "done"));

        await _context.Start();
        var producer = _context.GetEndpoint("direct://notrace-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("x")));

        probe.Activities.Should().BeEmpty("the route opted out of tracing");
    }

    [Fact]
    public async Task TracingTrue_OpensTheRouteSpan()
    {
        using var probe = RouteTelemetryProbe.ForRouteIdPrefix("direct-traceon-");

        _context.AddRoutes(r => r.From("direct://traceon-in")
            .Tracing(true)
            .Process(e => e.In.Body = "done"));

        await _context.Start();
        var producer = _context.GetEndpoint("direct://traceon-in").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("x")));

        probe.Activities.Should().Contain(a =>
            (RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.RouteIdTag) ?? "").StartsWith("direct-traceon-"),
            "the route span carries its route id");
    }
}

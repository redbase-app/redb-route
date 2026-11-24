using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Extensions;

namespace redb.Route.Tests.Extensions;

/// <summary>
/// Tests for <see cref="RouteHealthCheck"/> per-route status and endpoint statistics.
/// </summary>
public class RouteHealthCheckTests : IAsyncLifetime
{
    private readonly RouteContext _context = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task NoRoutes_ReturnsDegraded()
    {
        var healthCheck = new RouteHealthCheck(_context);

        var result = await healthCheck.CheckHealthAsync(CreateContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("No routes");
    }

    [Fact]
    public async Task AllRoutesStarted_ReturnsHealthy()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://health-source")
             .Process(e => { });
        });

        await _context.Start();

        var healthCheck = new RouteHealthCheck(_context);
        var result = await healthCheck.CheckHealthAsync(CreateContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("1 route(s) running");
    }

    [Fact]
    public async Task WithRoutes_IncludesRouteCount()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://a")
             .Process(e => { });
        });
        _context.AddRoutes(r =>
        {
            r.From("direct://b")
             .Process(e => { });
        });

        await _context.Start();

        var healthCheck = new RouteHealthCheck(_context);
        var result = await healthCheck.CheckHealthAsync(CreateContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("routeCount");
        result.Data["routeCount"].Should().Be(2);
    }

    [Fact]
    public async Task WithRoutes_IncludesRouteDetails()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://meta")
             .RouteId("test-route")
             .Process(e => { });
        });

        await _context.Start();

        var healthCheck = new RouteHealthCheck(_context);
        var result = await healthCheck.CheckHealthAsync(CreateContext());

        result.Data.Should().ContainKey("routes");
        var routes = result.Data["routes"] as List<object>;
        routes.Should().NotBeNull();
        routes!.Count.Should().Be(1);
        var routeDict = routes[0] as Dictionary<string, object?>;
        routeDict.Should().NotBeNull();
        routeDict!["routeId"].Should().Be("test-route");
        routeDict["status"].Should().Be("Started");
    }

    [Fact]
    public async Task ErroredRoute_ReturnsUnhealthy()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://err")
             .RouteId("errored-route")
             .Process(e => { });
        });

        await _context.Start();

        // Manually set route to Errored state
        var route = _context.Routes.First(r => r.RouteId == "errored-route");
        route.Status = RouteStatus.Errored;

        var healthCheck = new RouteHealthCheck(_context);
        var result = await healthCheck.CheckHealthAsync(CreateContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("errors detected");
    }

    [Fact]
    public async Task StoppedRoute_ReturnsDegraded()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://stopped")
             .RouteId("stopped-route")
             .Process(e => { });
        });

        await _context.Start();
        await _context.StopRoute("stopped-route");

        var healthCheck = new RouteHealthCheck(_context);
        var result = await healthCheck.CheckHealthAsync(CreateContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("degraded");
    }

    [Fact]
    public void Constructor_NullContext_Throws()
    {
        var act = () => new RouteHealthCheck((IRouteContext)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task AnErrorThatQuotesACredentialedUri_ReachesThePayloadMasked()
    {
        // A connector's exception may quote the URI it failed on, password and all; the payload is often read by
        // unauthenticated probes.
        _context.AddRoutes(r => r.From("direct://health-leak").Process(_ =>
            throw new InvalidOperationException(
                "None of the endpoints were reachable: amqp://svc:Sup3rS3cret@broker:5672 (password=Sup3rS3cret)")));
        await _context.Start();
        var producer = _context.GetEndpoint("direct://health-leak").CreateProducer();
        await producer.Start();
        try { await producer.Process(new Exchange(new Message("x"))); }
        catch (InvalidOperationException) { }

        var result = await new RouteHealthCheck(_context).CheckHealthAsync(CreateContext());

        var payload = System.Text.Json.JsonSerializer.Serialize(result.Data);
        payload.Should().Contain("svc:" + EndpointUri.Redacted + "@broker", "the error is still there, masked");
        payload.Should().NotContain("Sup3rS3cret");
    }

    private static HealthCheckContext CreateContext()
    {
        return new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "test",
                Substitute.For<IHealthCheck>(),
                HealthStatus.Unhealthy,
                Array.Empty<string>())
        };
    }
}

using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests;

/// <summary>
/// Tests for route lifecycle: start/stop individual routes by routeId,
/// route status tracking, and GetRoute API.
/// </summary>
public class RouteLifecycleTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Start_SetsAllRoutesToStarted()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://a").RouteId("route-a").Process(_ => { });
        });
        _context.AddRoutes(r =>
        {
            r.From("direct://b").RouteId("route-b").Process(_ => { });
        });

        await _context.Start();

        var routeA = _context.GetRoute("route-a");
        var routeB = _context.GetRoute("route-b");

        routeA.Should().NotBeNull();
        routeB.Should().NotBeNull();
        routeA!.Status.Should().Be(RouteStatus.Started);
        routeB!.Status.Should().Be(RouteStatus.Started);
    }

    [Fact]
    public async Task Stop_SetsAllRoutesToStopped()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://a").RouteId("route-a").Process(_ => { });
        });

        await _context.Start();

        var route = _context.GetRoute("route-a");
        route!.Status.Should().Be(RouteStatus.Started);

        await _context.Stop();

        route.Status.Should().Be(RouteStatus.Stopped);
    }

    [Fact]
    public async Task GetRoute_ReturnsNull_WhenNotFound()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://x").RouteId("exists").Process(_ => { });
        });

        await _context.Start();

        _context.GetRoute("nonexistent").Should().BeNull();
    }

    [Fact]
    public void GetRoute_ThrowsOnNull()
    {
        var act = () => _context.GetRoute(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task GetRoute_CaseInsensitive()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://ci").RouteId("MyRoute").Process(_ => { });
        });

        await _context.Start();

        _context.GetRoute("myroute").Should().NotBeNull();
        _context.GetRoute("MYROUTE").Should().NotBeNull();
        _context.GetRoute("MyRoute").Should().NotBeNull();
    }

    [Fact]
    public async Task StopRoute_StopsIndividualRoute()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://s1").RouteId("route-1").Process(_ => { });
        });
        _context.AddRoutes(r =>
        {
            r.From("direct://s2").RouteId("route-2").Process(_ => { });
        });

        await _context.Start();

        _context.GetRoute("route-1")!.Status.Should().Be(RouteStatus.Started);
        _context.GetRoute("route-2")!.Status.Should().Be(RouteStatus.Started);

        await _context.StopRoute("route-1");

        _context.GetRoute("route-1")!.Status.Should().Be(RouteStatus.Stopped);
        _context.GetRoute("route-2")!.Status.Should().Be(RouteStatus.Started);
    }

    [Fact]
    public async Task StartRoute_RestartsStoppedRoute()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://restart").RouteId("route-restart").Process(_ => { });
        });

        await _context.Start();
        await _context.StopRoute("route-restart");
        _context.GetRoute("route-restart")!.Status.Should().Be(RouteStatus.Stopped);

        await _context.StartRoute("route-restart");
        _context.GetRoute("route-restart")!.Status.Should().Be(RouteStatus.Started);
    }

    [Fact]
    public async Task StartRoute_ThrowsIfAlreadyStarted()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://already").RouteId("route-dup-start").Process(_ => { });
        });

        await _context.Start();

        var act = async () => await _context.StartRoute("route-dup-start");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already started*");
    }

    [Fact]
    public async Task StopRoute_ThrowsIfAlreadyStopped()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://stopped").RouteId("route-dup-stop").Process(_ => { });
        });

        await _context.Start();
        await _context.StopRoute("route-dup-stop");

        var act = async () => await _context.StopRoute("route-dup-stop");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already stopped*");
    }

    [Fact]
    public async Task StartRoute_ThrowsIfNotFound()
    {
        await _context.Start();

        var act = async () => await _context.StartRoute("ghost-route");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task StopRoute_ThrowsIfNotFound()
    {
        await _context.Start();

        var act = async () => await _context.StopRoute("ghost-route");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task CompiledRoute_ExposesRouteProperties()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://props").RouteId("props-route").Process(_ => { });
        });

        await _context.Start();

        var route = _context.GetRoute("props-route");
        route.Should().NotBeNull();
        route!.RouteId.Should().Be("props-route");
        route.FromUri.Should().Be("direct://props");
        route.Definition.Should().NotBeNull();
        route.Pipeline.Should().NotBeNull();
        route.Consumer.Should().NotBeNull();
        route.Endpoint.Should().NotBeNull();
    }

    [Fact]
    public async Task UnnamedRoute_WithSecretInUri_DoesNotLeakSecretInRouteIdOrFromUri()
    {
        // No explicit RouteId(): the id falls back to the endpoint key, which must be
        // sanitized so a URI secret never surfaces in {RouteId}/{FromUri} logs or dashboards.
        _context.AddRoutes(r =>
        {
            r.From("direct://svc?password=topsecret").Process(_ => { });
        });

        await _context.Start();

        var route = _context.Routes.Single();
        route.RouteId.Should().NotContain("topsecret");
        route.RouteId.Should().Contain("****");
        route.FromUri.Should().NotContain("topsecret");
        route.FromUri.Should().Contain("****");
    }

    [Fact]
    public void RouteStatus_HasCorrectValues()
    {
        RouteStatus.Stopped.Should().Be((RouteStatus)0);
        RouteStatus.Started.Should().Be((RouteStatus)1);
        RouteStatus.Starting.Should().Be((RouteStatus)2);
        RouteStatus.Suspending.Should().Be((RouteStatus)3);
        RouteStatus.Suspended.Should().Be((RouteStatus)4);
        RouteStatus.Stopping.Should().Be((RouteStatus)5);
        RouteStatus.Errored.Should().Be((RouteStatus)6);
    }

    [Fact]
    public async Task StopAndRestart_RouteProcessesExchanges()
    {
        var received = new List<object?>();

        _context.AddRoutes(r =>
        {
            r.From("direct://cycle")
                .RouteId("cycle-route")
                .Process(e => received.Add(e.In.Body));
        });

        await _context.Start();

        // Send first message (route is started)
        var producer = _context.GetEndpoint("direct://cycle").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message { Body = "before-stop" }));

        received.Should().HaveCount(1);
        received[0].Should().Be("before-stop");

        // Stop the route
        await _context.StopRoute("cycle-route");

        // Start it again
        await _context.StartRoute("cycle-route");

        // Send another message
        await producer.Process(new Exchange(new Message { Body = "after-restart" }));

        received.Should().HaveCount(2);
        received[1].Should().Be("after-restart");
    }
}

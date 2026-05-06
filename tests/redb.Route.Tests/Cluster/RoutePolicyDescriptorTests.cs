using FluentAssertions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.Tests.Cluster;

/// <summary>
/// Verifies <see cref="IRouteContext.GetRoutePolicy"/> reports the effective cluster
/// policy: <c>AllNodes</c> for standalone routes, <c>ClusterLeader</c> when a
/// <see cref="IRoutePolicyFactory"/> is registered, <c>Custom</c> for explicit policies.
/// </summary>
public class RoutePolicyDescriptorTests : IAsyncDisposable
{
    private readonly RouteContext _context;
    private readonly CapturingLoggerProvider _capture = new();

    public RoutePolicyDescriptorTests()
    {
        var lf = LoggerFactory.Create(b => b.AddProvider(_capture).SetMinimumLevel(LogLevel.Debug));
        _context = new RouteContext(loggerFactory: lf, options: new RouteEngineOptions { StartupChecks = true });
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Standalone_route_reports_AllNodes()
    {
        _context.AddRoutes(r => r.From("direct://a").RouteId("a").Process(_ => { }));
        await _context.Start();

        var d = _context.GetRoutePolicy("a");
        d.RequestedCluster.Should().BeFalse();
        d.EffectivePolicy.Should().Be("AllNodes");
        d.PolicyFactoryType.Should().BeNull();
    }

    [Fact]
    public async Task Cluster_route_without_factory_reports_AllNodes_with_warning_reason()
    {
        _context.AddRoutes(r => r.From("direct://b").RouteId("b").Cluster(true).Process(_ => { }));
        await _context.Start();

        var d = _context.GetRoutePolicy("b");
        d.RequestedCluster.Should().BeTrue();
        d.EffectivePolicy.Should().Be("AllNodes");
        d.PolicyFactoryType.Should().BeNull();
        d.Reason.Should().Contain("no IRoutePolicyFactory");
    }

    [Fact]
    public async Task Cluster_route_with_factory_reports_ClusterLeader()
    {
        _context.AddRoutePolicyFactory(new StubFactory());
        _context.AddRoutes(r => r.From("direct://c").RouteId("c").Cluster(true).Process(_ => { }));
        await _context.Start();

        var d = _context.GetRoutePolicy("c");
        d.RequestedCluster.Should().BeTrue();
        d.EffectivePolicy.Should().Be("ClusterLeader");
        d.PolicyFactoryType.Should().Contain(nameof(StubFactory));
    }

    [Fact]
    public void Unknown_route_returns_AllNodes_NotFound_reason()
    {
        var d = _context.GetRoutePolicy("ghost");
        d.EffectivePolicy.Should().Be("AllNodes");
        d.Reason.Should().Be("Route not found");
    }

    private sealed class StubFactory : IRoutePolicyFactory
    {
        public IRoutePolicy? CreateRoutePolicy(IRouteContext context, string routeId, RouteDefinition definition)
            => new StubPolicy();
    }

    private sealed class StubPolicy : IRoutePolicy { }
}

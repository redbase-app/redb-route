using FluentAssertions;
using Microsoft.Extensions.Logging;
using redb.Route.Configuration;
using redb.Route.Core;

namespace redb.Route.Tests.Cluster;

/// <summary>
/// Verifies the startup-check warning emitted when a route declares <c>.Cluster(true)</c>
/// but no <see cref="redb.Route.Abstractions.IRoutePolicyFactory"/> is registered.
/// </summary>
public class StartupWarningTests : IAsyncDisposable
{
    private readonly CapturingLoggerProvider _capture = new();
    private readonly RouteContext _context;

    public StartupWarningTests()
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
    public async Task Cluster_route_without_factory_emits_warning_once()
    {
        _context.AddRoutes(r => r.From("direct://x").RouteId("x").Cluster(true).Process(_ => { }));
        await _context.Start();

        _capture.Entries
            .Count(e => e.Level == LogLevel.Warning && e.Message.Contains("no IRoutePolicyFactory"))
            .Should().Be(1);
    }

    [Fact]
    public async Task Standalone_route_emits_no_warning()
    {
        _context.AddRoutes(r => r.From("direct://y").RouteId("y").Process(_ => { }));
        await _context.Start();

        _capture.Entries.Should().NotContain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("IRoutePolicyFactory"));
    }

    [Fact]
    public async Task StartupChecks_disabled_silences_the_warning()
    {
        await _context.DisposeAsync();
        var lf = LoggerFactory.Create(b => b.AddProvider(_capture).SetMinimumLevel(LogLevel.Debug));
        await using var ctx = new RouteContext(
            loggerFactory: lf,
            options: new RouteEngineOptions { StartupChecks = false });

        ctx.AddRoutes(r => r.From("direct://z").RouteId("z").Cluster(true).Process(_ => { }));
        await ctx.Start();

        _capture.Entries.Should().NotContain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("IRoutePolicyFactory"));
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using redb.Route.Core;
using redb.Route.Extensions;

namespace redb.Route.Tests.Extensions;

/// <summary>
/// Tests for <see cref="RouteHostedService"/>.
/// </summary>
public class RouteHostedServiceTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();
    private readonly ILogger<RouteHostedService> _logger = NullLogger<RouteHostedService>.Instance;

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Constructor_NullEngine_Throws()
    {
        var act = () => new RouteHostedService(
            null!,
            Array.Empty<IRouteContextConfigurator>(),
            _logger);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullConfigurators_Throws()
    {
        var act = () => new RouteHostedService(
            _context,
            null!,
            _logger);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new RouteHostedService(
            _context,
            Array.Empty<IRouteContextConfigurator>(),
            null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task StartAsync_CallsConfiguratorsAndStartsEngine()
    {
        var configured = false;
        var configurator = Substitute.For<IRouteContextConfigurator>();
        configurator.When(c => c.Configure(Arg.Any<RouteContext>()))
            .Do(_ =>
            {
                configured = true;
                _context.AddRoutes(r =>
                {
                    r.From("direct://hosted")
                     .Process(e => { });
                });
            });

        var service = new RouteHostedService(_context, new[] { configurator }, _logger);

        await service.StartAsync(CancellationToken.None);

        configured.Should().BeTrue();
        _context.Routes.Should().HaveCount(1);
        configurator.Received(1).Configure(_context);
    }

    [Fact]
    public async Task StopAsync_StopsEngine()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://stop-test")
             .Process(e => { });
        });

        var service = new RouteHostedService(
            _context,
            Array.Empty<IRouteContextConfigurator>(),
            _logger);

        await service.StartAsync(CancellationToken.None);
        _context.Routes.Should().HaveCount(1);

        await service.StopAsync(CancellationToken.None);

        // Routes are cleared on Stop — recompiled from builders on next Start
        _context.Routes.Should().BeEmpty();
    }

    [Fact]
    public async Task DisposeAsync_DisposesEngine()
    {
        var service = new RouteHostedService(
            _context,
            Array.Empty<IRouteContextConfigurator>(),
            _logger);

        // Should not throw
        await service.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_NoConfigurators_StartsEmpty()
    {
        var service = new RouteHostedService(
            _context,
            Array.Empty<IRouteContextConfigurator>(),
            _logger);

        await service.StartAsync(CancellationToken.None);

        _context.Routes.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_MultipleConfigurators_AllApplied()
    {
        var c1 = Substitute.For<IRouteContextConfigurator>();
        var c2 = Substitute.For<IRouteContextConfigurator>();

        var service = new RouteHostedService(_context, new[] { c1, c2 }, _logger);
        await service.StartAsync(CancellationToken.None);

        c1.Received(1).Configure(_context);
        c2.Received(1).Configure(_context);
    }
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using redb.Route.Core;

namespace redb.Route.Extensions;

/// <summary>
/// <see cref="IHostedService"/> integration for the route context.
/// Starts and stops routes with the application lifecycle.
/// Register via <see cref="ServiceCollectionExtensions.AddRedbRoute"/>.
/// </summary>
public sealed class RouteHostedService : IHostedService, IAsyncDisposable
{
    private readonly RouteContext _context;
    private readonly IEnumerable<IRouteContextConfigurator> _configurators;
    private readonly ILogger<RouteHostedService> _logger;

    /// <summary>Creates a new hosted service wrapping the route context.</summary>
    /// <param name="context">Route context instance (resolved from DI).</param>
    /// <param name="configurators">Context configurators (route builders, components, inline routes).</param>
    /// <param name="logger">Logger.</param>
    public RouteHostedService(
        RouteContext context,
        IEnumerable<IRouteContextConfigurator> configurators,
        ILogger<RouteHostedService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _configurators = configurators ?? throw new ArgumentNullException(nameof(configurators));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting redb.Route hosted service...");

        // Apply all DI-registered configurators (route builders, components, inline routes)
        foreach (var configurator in _configurators)
        {
            configurator.Configure(_context);
        }

        await _context.Start(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("redb.Route hosted service started with {Count} route(s).", _context.Routes.Count);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping redb.Route hosted service...");
        await _context.Stop(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("redb.Route hosted service stopped.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync().ConfigureAwait(false);
    }
}

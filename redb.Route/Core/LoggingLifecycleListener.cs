using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// Built-in lifecycle listener that logs all route state transitions via <see cref="ILogger"/>.
/// </summary>
public sealed class LoggingLifecycleListener : IRouteLifecycleListener
{
    private readonly ILogger _logger;

    /// <summary>Creates a logging lifecycle listener.</summary>
    public LoggingLifecycleListener(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task OnRouteStarted(string routeId, CancellationToken ct)
    {
        _logger.LogInformation("[Lifecycle] Route '{RouteId}' started.", routeId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnRouteStopped(string routeId, CancellationToken ct)
    {
        _logger.LogInformation("[Lifecycle] Route '{RouteId}' stopped.", routeId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnRouteSuspending(string routeId, CancellationToken ct)
    {
        _logger.LogInformation("[Lifecycle] Route '{RouteId}' suspending (drain starting).", routeId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnRouteErrored(string routeId, Exception ex, CancellationToken ct)
    {
        _logger.LogError(ex, "[Lifecycle] Route '{RouteId}' errored.", routeId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnExchangeTimedOut(string routeId, string exchangeId, TimeSpan elapsed, CancellationToken ct)
    {
        _logger.LogWarning(
            "[Lifecycle] Exchange '{ExchangeId}' timed out on route '{RouteId}' after {Elapsed}s.",
            exchangeId, routeId, elapsed.TotalSeconds);
        return Task.CompletedTask;
    }
}

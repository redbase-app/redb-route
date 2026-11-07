using System.Diagnostics;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Wraps an inner processor with a per-exchange processing timeout.
/// If the timeout fires, throws <see cref="ExchangeTimedOutException"/>
/// which can be caught by route-level exception handlers.
/// </summary>
internal sealed class TimeoutProcessor : IProcessor
{
    private readonly IProcessor _inner;
    private readonly TimeSpan _timeout;
    private readonly string _routeId;
    private readonly RouteContext? _context;
    private readonly ILogger? _logger;

    public TimeoutProcessor(IProcessor inner, TimeSpan timeout, string routeId, RouteContext? context = null, ILogger? logger = null)
    {
        _inner = inner;
        _timeout = timeout;
        _routeId = routeId;
        _context = context;
        _logger = logger;
    }

    public async Task Process(IExchange exchange, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        var sw = Stopwatch.StartNew();
        try
        {
            await _inner.Process(exchange, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            sw.Stop();
            var elapsed = sw.Elapsed;
            var ex = new ExchangeTimedOutException(_routeId, _timeout, elapsed);
            exchange.Exception = ex;
            exchange.Properties["ExchangeTimedOut"] = true;
            ProcessorMetrics.TimeoutExpired.Add(1);
            _logger?.LogWarning("Exchange {ExchangeId} timed out after {Elapsed}ms on route '{RouteId}'.",
                exchange.ExchangeId, elapsed.TotalMilliseconds, _routeId);

            if (_context != null)
            {
                await _context.NotifyExchangeTimedOut(
                    _routeId, exchange.ExchangeId, elapsed, ct).ConfigureAwait(false);
            }

            throw ex;
        }
    }
}

using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Sends a fire-and-forget clone of the exchange to a tap processor.
/// The main exchange continues through the pipeline unchanged.
/// Supports an optional onPrepare callback to modify the clone before tapping
/// and an optional body replacement for the tapped exchange.
/// Errors in the tap are logged (if logger available) but never propagated
/// to the main pipeline — this is by-design for fire-and-forget semantics.
/// </summary>
public class WireTapProcessor : IProcessor
{
    private readonly IProcessor _tap;
    private readonly Action<IExchange>? _onPrepare;
    private readonly Func<IExchange, object?>? _newBodyFactory;
    private readonly ILogger? _logger;

    /// <summary>Creates a wire-tap that sends clones to the specified processor.</summary>
    /// <param name="tap">The processor to receive exchange clones.</param>
    /// <param name="onPrepare">Optional callback to modify the cloned exchange before tapping.</param>
    /// <param name="newBodyFactory">Optional factory to replace the body on the tapped clone.</param>
    /// <param name="logger">Optional logger for tap errors.</param>
    public WireTapProcessor(
        IProcessor tap,
        Action<IExchange>? onPrepare = null,
        Func<IExchange, object?>? newBodyFactory = null,
        ILogger? logger = null)
    {
        _tap = tap ?? throw new ArgumentNullException(nameof(tap));
        _onPrepare = onPrepare;
        _newBodyFactory = newBodyFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var clone = exchange.Clone();

        if (_newBodyFactory != null)
            clone.In.Body = _newBodyFactory(exchange);

        _onPrepare?.Invoke(clone);

        // Fire-and-forget: do not await, but catch and log errors.
        // The tap branch must NOT inherit the caller's CancellationToken — otherwise
        // cancelling the main pipeline (e.g. an HTTP request being aborted) would
        // kill an in-flight audit/notification mid-write. WireTap semantics per EIP
        // are "InOnly, detached": the side branch lives on its own lifecycle.
        _ = Task.Run(async () =>
        {
            try
            {
                await _tap.Process(clone, CancellationToken.None).ConfigureAwait(false);
                ProcessorMetrics.WireTapDispatched.Add(1);
            }
            catch (OperationCanceledException) { /* expected on host shutdown */ }
            catch (Exception ex)
            {
                ProcessorMetrics.WireTapFailed.Add(1);
                _logger?.LogWarning(ex, "WireTap processor failed for route '{RouteId}'. Error is non-fatal and will not affect the main pipeline.",
                    exchange.RouteId ?? "(no-route)");
            }
            finally
            {
                if (clone is IAsyncDisposable d) await d.DisposeAsync().ConfigureAwait(false);
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }
}

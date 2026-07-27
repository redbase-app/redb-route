using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Compiled body of a <c>.Replayable("name")</c> marker (a save-point). On each pass it captures a
/// deep, isolated <see cref="IExchange.Snapshot"/> into
/// <see cref="IExchange.Properties"/>[<see cref="RouteCheckpoint.PropertyKey"/>] (last marker wins),
/// then runs the marker's body. The same compiled <see cref="Body"/> is what
/// <see cref="IRouteContext.ReplayAsync(string, string, IExchange, System.Threading.CancellationToken)"/>
/// re-runs from a stored snapshot — no synthetic endpoint.
/// <para>
/// SECURITY: the snapshot is a verbatim, UNREDACTED copy of the mid-flight exchange — body, headers
/// and Properties may hold decrypted tokens, PII or secrets. Nothing is masked here (URI redaction
/// covers endpoint URIs, NOT payloads). Enabling <c>.Replayable()</c> accepts that; storing or
/// exposing snapshots is the platform's responsibility, not the route's.
/// </para>
/// </summary>
public sealed class CheckpointProcessor : IProcessor
{
    private readonly string _markerName;
    private readonly IProcessor _body;
    private readonly ILogger? _logger;

    /// <summary>The marker's compiled body — the tail re-run on replay.</summary>
    public IProcessor Body => _body;

    /// <summary>The save-point name (unique within the route).</summary>
    public string MarkerName => _markerName;

    /// <summary>Creates a checkpoint processor wrapping the marker body.</summary>
    public CheckpointProcessor(string markerName, IProcessor body, ILogger? logger = null)
    {
        _markerName = markerName ?? throw new ArgumentNullException(nameof(markerName));
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _logger = logger;
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        try
        {
            // Freeze the state at this marker — deep, isolated (NOT Clone). Last marker wins.
            exchange.Properties[RouteCheckpoint.PropertyKey] =
                new RouteCheckpoint(exchange.RouteId ?? string.Empty, _markerName, exchange.Snapshot());
        }
        catch (NotSupportedException ex)
        {
            // A non-snapshot-able body must NEVER break the happy path: degrade gracefully — this
            // pass captures no checkpoint (replay simply won't be available for it) and processing
            // continues. Make the body snapshot-able (immutable / byte[] / ICloneable) to enable it.
            _logger?.LogWarning(ex,
                "Checkpoint '{Marker}' not captured: exchange body is not snapshot-able; replay unavailable for this pass.",
                _markerName);
        }

        return _body.Process(exchange, ct);
    }
}

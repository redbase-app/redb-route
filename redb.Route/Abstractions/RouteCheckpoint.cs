namespace redb.Route.Abstractions;

/// <summary>
/// The last <c>.Replayable("name")</c> save-point an exchange passed. Captured on each pass into
/// <see cref="IExchange.Properties"/>[<see cref="PropertyKey"/>] (the last marker wins) and read by
/// an error handler / the platform (Tsak) to replay the tail of the route from the frozen snapshot
/// via <see cref="IRouteContext.ReplayAsync(string, string, IExchange, System.Threading.CancellationToken)"/>.
/// <para>
/// SECURITY: <see cref="Snapshot"/> is a verbatim, UNREDACTED copy of the mid-flight exchange —
/// body, headers and Properties may hold decrypted tokens, PII or secrets. Nothing is masked (URI
/// redaction covers endpoint URIs, NOT payloads). Storing or exposing snapshots (e.g. via Tsak) is
/// the platform's responsibility, not the route's.
/// </para>
/// </summary>
/// <param name="RouteId">Route the checkpoint belongs to.</param>
/// <param name="MarkerName">Name of the <c>.Replayable</c> marker.</param>
/// <param name="Snapshot">Deep, isolated snapshot of the exchange at the marker (see <see cref="IExchange.Snapshot"/>).</param>
public sealed record RouteCheckpoint(string RouteId, string MarkerName, IExchange Snapshot)
{
    /// <summary>Well-known <see cref="IExchange.Properties"/> key holding the last passed <see cref="RouteCheckpoint"/>.</summary>
    public const string PropertyKey = "route.checkpoint";

    /// <summary>
    /// The <c>direct:</c> endpoint URI an <c>exposed</c> marker is addressable at
    /// (<c>.To("direct:__replay:{routeId}:{markerName}")</c> from another route). Single source of
    /// the format; internal (non-exposed) markers register no such endpoint.
    /// </summary>
    public static string EndpointUri(string routeId, string markerName)
        => $"direct:__replay:{routeId}:{markerName}";
}

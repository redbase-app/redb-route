namespace redb.Route.Abstractions;

/// <summary>
/// Snapshot of an exchange currently being processed (in-flight).
/// </summary>
public record InflightExchange(
    string ExchangeId,
    string RouteId,
    DateTime StartedAt,
    int ManagedThreadId,
    string? FromEndpoint);

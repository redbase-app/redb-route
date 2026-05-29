using System.Diagnostics;

namespace redb.Route.Telemetry;

/// <summary>
/// Helpers for opening uniform transport spans on the shared
/// <see cref="RouteActivitySource.Source"/>. All transports (Kafka, RabbitMQ, Http,
/// Sql, gRPC, file, etc.) should use these helpers so spans share a consistent
/// shape and OpenTelemetry semantic-convention tags.
/// </summary>
public static class RouteTelemetryExtensions
{
    /// <summary>
    /// Starts a messaging / RPC / I/O span for a transport operation.
    /// Tags are emitted following the OpenTelemetry semantic conventions for the
    /// supplied <paramref name="system"/> namespace.
    /// </summary>
    /// <param name="name">Span name, e.g. <c>"queue.name publish"</c> or <c>"GET /users/{id}"</c>.</param>
    /// <param name="kind">Activity kind (Producer / Consumer / Client / Server).</param>
    /// <param name="systemAttribute">
    /// Semantic-convention attribute name. Use <c>"messaging.system"</c> for brokers,
    /// <c>"db.system"</c> for databases, <c>"rpc.system"</c> for gRPC,
    /// <c>"http.method"</c> for plain HTTP or <c>"file.system"</c> for file transports.
    /// </param>
    /// <param name="systemValue">Value of the <paramref name="systemAttribute"/> (e.g. <c>"rabbitmq"</c>).</param>
    /// <param name="endpointUri">The full redb.route endpoint URI; always emitted as <c>redb.route.endpoint</c>.</param>
    /// <param name="destination">Optional destination (queue/topic/path) emitted as <c>messaging.destination.name</c>.</param>
    /// <param name="operation">Optional verb emitted as <c>messaging.operation</c> when relevant.</param>
    /// <returns>The opened activity, or <c>null</c> when no listener is active.</returns>
    public static Activity? StartTransportSpan(
        string name,
        ActivityKind kind,
        string systemAttribute,
        string systemValue,
        string endpointUri,
        string? destination = null,
        string? operation = null)
    {
        var activity = RouteActivitySource.Source.StartActivity(name, kind);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(systemAttribute, systemValue);
            activity.SetTag("redb.route.endpoint", endpointUri);
            if (!string.IsNullOrEmpty(destination))
                activity.SetTag("messaging.destination.name", destination);
            if (!string.IsNullOrEmpty(operation))
                activity.SetTag("messaging.operation", operation);
        }
        return activity;
    }
}

namespace redb.Route.Telemetry;

/// <summary>Which span a <c>.SpanTag(...)</c> step puts its attribute on.</summary>
public enum SpanTarget
{
    /// <summary>
    /// The nearest span of redb.Route around the step: the enclosing <c>Traced(...)</c> scope, else the route span.
    /// With route tracing off (<c>.Tracing(false)</c>) there is none and no attribute is set — never on a transport
    /// span or on a span of the host.
    /// </summary>
    Enclosing,

    /// <summary>
    /// The inbound span of the transport that received the exchange — the server span of an HTTP, SOAP or gRPC request,
    /// the consumer span of a broker message or a scheduler fire. A route called through <c>direct:</c> keeps the
    /// caller's. Not the host's own span (ASP.NET Core): that one belongs to the host.
    /// </summary>
    Inbound,
}

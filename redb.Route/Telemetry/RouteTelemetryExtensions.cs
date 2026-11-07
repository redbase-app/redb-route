using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Telemetry;

/// <summary>Reads one trace field (<c>traceparent</c>, <c>tracestate</c>, <c>baggage</c>) from a transport carrier;
/// <c>null</c> when the carrier does not hold it. Header names are compared the way the transport compares them.</summary>
/// <typeparam name="TCarrier">Headers, message properties, attributes — whatever the transport carries.</typeparam>
public delegate string? TraceHeaderReader<in TCarrier>(TCarrier carrier, string name);

/// <summary>Writes one trace field to a transport carrier, <b>replacing</b> a value already there: a header copied
/// from the incoming message names the previous hop, not this one.</summary>
/// <typeparam name="TCarrier">Headers, message properties, attributes — whatever the transport carries.</typeparam>
public delegate void TraceHeaderWriter<in TCarrier>(TCarrier carrier, string name, string value);

/// <summary>Where an inbound span takes its parent from.</summary>
public enum InboundParent
{
    /// <summary>
    /// From the trace fields of the message itself. Without them the span is a root: the thread's ambient activity
    /// (a poll loop, a host span of some other work) is never the parent of a message it did not send.
    /// </summary>
    Carrier,

    /// <summary>
    /// The host's span for this same request, when there is one — an ASP.NET Core server span has already taken the
    /// caller's context from the request headers. Without one the carrier is read, as with <see cref="Carrier"/>.
    /// </summary>
    HostRequest,
}

/// <summary>
/// An inbound transport span opened by <see cref="RouteTelemetryExtensions.StartConsumerSpan{TCarrier}"/>. Disposing
/// it ends the span and gives the thread back the ambient activity it had before.
/// </summary>
public readonly struct TransportSpan : IDisposable
{
    private readonly Activity? _ambient;

    internal TransportSpan(Activity? activity, Activity? ambient)
    {
        Activity = activity;
        _ambient = ambient;
    }

    /// <summary>The span; <c>null</c> when nobody listens or tracing is off for the context.</summary>
    public Activity? Activity { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Activity is null)
            return;
        Activity.Dispose();
        // A span with a remote parent or none stops into a null current; the thread had its own before.
        System.Diagnostics.Activity.Current = _ambient;
    }
}

/// <summary>
/// The tracing contract of a transport, on the shared <see cref="RouteActivitySource.Source"/>: open the span
/// (<see cref="StartTransportSpan(IRouteContext?, string, ActivityKind, string, string, string, string?, string?)"/>
/// outbound, <see cref="StartConsumerSpan{TCarrier}"/> inbound), carry the context to the next hop
/// (<see cref="InjectTraceContext{TCarrier}"/>) and mark a failed call (<see cref="RecordFailure"/>). Every span
/// carries <c>redb.route.endpoint</c>, sanitized, and honours <c>RouteEngineOptions.EnableTelemetry</c> of the context.
/// A connector supplies only the carrier and its names; the W3C and baggage handling lives here once.
/// </summary>
public static class RouteTelemetryExtensions
{
    /// <summary>
    /// Whether spans are opened for <paramref name="context"/>: its <c>RouteEngineOptions.EnableTelemetry</c>.
    /// <c>true</c> without a context — a component used on its own traces as before.
    /// </summary>
    public static bool IsTracingEnabled(this IRouteContext? context)
        => (context as RouteContext)?.TelemetryEnabled ?? true;

    /// <summary>
    /// Starts a messaging / RPC / I/O span for a transport operation.
    /// Tags are emitted following the OpenTelemetry semantic conventions for the
    /// supplied <paramref name="system"/> namespace. Does not read <c>EnableTelemetry</c>; a connector passes its
    /// context to the overload that does.
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
        Tag(activity, systemAttribute, systemValue, endpointUri, destination, operation);
        return activity;
    }

    /// <summary>
    /// <see cref="StartTransportSpan(string, ActivityKind, string, string, string, string?, string?)"/> for an
    /// endpoint of <paramref name="context"/>: <c>null</c> when the context has tracing off.
    /// </summary>
    /// <param name="context">The route context of the endpoint (<c>ComponentBase.Context</c>).</param>
    /// <param name="name">Span name.</param>
    /// <param name="kind">Activity kind (Producer / Client).</param>
    /// <param name="systemAttribute">Semantic-convention attribute name.</param>
    /// <param name="systemValue">Value of the <paramref name="systemAttribute"/>.</param>
    /// <param name="endpointUri">The endpoint URI, emitted sanitized as <c>redb.route.endpoint</c>.</param>
    /// <param name="destination">Optional destination, emitted sanitized as <c>messaging.destination.name</c>.</param>
    /// <param name="operation">Optional verb emitted as <c>messaging.operation</c>.</param>
    public static Activity? StartTransportSpan(
        IRouteContext? context,
        string name,
        ActivityKind kind,
        string systemAttribute,
        string systemValue,
        string endpointUri,
        string? destination = null,
        string? operation = null)
        => context.IsTracingEnabled()
            ? StartTransportSpan(name, kind, systemAttribute, systemValue, endpointUri, destination, operation)
            : null;

    /// <summary>
    /// Opens the span of an inbound message or request. Its parent is chosen by <paramref name="parent"/>; with
    /// <see cref="InboundParent.Carrier"/> the W3C fields are read from <paramref name="carrier"/> and, when absent,
    /// the span is a root — never a child of whatever activity the receiving thread happens to hold. The baggage
    /// the sender put on its activity is put back on this one, so the route's own spans and sends carry it on.
    /// </summary>
    /// <param name="context">The route context of the endpoint; tracing off opens nothing.</param>
    /// <param name="name">Span name, e.g. <c>"orders receive"</c>.</param>
    /// <param name="kind"><see cref="ActivityKind.Consumer"/> for a message, <see cref="ActivityKind.Server"/> for a request.</param>
    /// <param name="systemAttribute">Semantic-convention attribute name.</param>
    /// <param name="systemValue">Value of the <paramref name="systemAttribute"/>.</param>
    /// <param name="endpointUri">The endpoint URI, emitted sanitized as <c>redb.route.endpoint</c>.</param>
    /// <param name="carrier">The incoming message's headers or properties.</param>
    /// <param name="readHeader">Reads one field from <paramref name="carrier"/>.</param>
    /// <param name="parent">Where the parent comes from.</param>
    /// <param name="destination">Optional destination, emitted sanitized as <c>messaging.destination.name</c>.</param>
    /// <param name="operation">Optional verb emitted as <c>messaging.operation</c>.</param>
    /// <param name="links">Links to other contexts, e.g. every message of a batch (<see cref="ExtractTraceContext{TCarrier}"/>).</param>
    public static TransportSpan StartConsumerSpan<TCarrier>(
        IRouteContext? context,
        string name,
        ActivityKind kind,
        string systemAttribute,
        string systemValue,
        string endpointUri,
        TCarrier carrier,
        TraceHeaderReader<TCarrier> readHeader,
        InboundParent parent = InboundParent.Carrier,
        string? destination = null,
        string? operation = null,
        IEnumerable<ActivityLink>? links = null)
    {
        ArgumentNullException.ThrowIfNull(readHeader);
        if (!context.IsTracingEnabled())
            return default;

        var ambient = Activity.Current;
        Activity? activity;
        if (parent == InboundParent.HostRequest && ambient is not null)
        {
            activity = RouteActivitySource.Source.StartActivity(name, kind, parentContext: default, tags: null, links);
        }
        else
        {
            var fields = new CarrierReader<TCarrier>(carrier, readHeader);
            var remote = Extract(fields);
            // StartActivity with a default parent context falls back to Activity.Current rather than starting a root
            // (the house pattern, see DetachedDispatch.Enter): clear it for the start, give it back on dispose.
            Activity.Current = null;
            activity = RouteActivitySource.Source.StartActivity(name, kind, remote, tags: null, links);
            if (activity is null)
            {
                Activity.Current = ambient;
            }
            else if (DistributedContextPropagator.Current.ExtractBaggage(fields, Getter) is { } baggage)
            {
                // AddBaggage puts an item first; adding in reverse keeps the sender's order.
                foreach (var (key, value) in baggage.Reverse())
                    activity.AddBaggage(key, value);
            }
        }

        Tag(activity, systemAttribute, systemValue, endpointUri, destination, operation);
        return new TransportSpan(activity, ambient);
    }

    /// <summary>
    /// The W3C context <paramref name="carrier"/> holds, or <c>default</c> when it holds none or an invalid one. For
    /// links: a batch span links the context of every message it carries.
    /// </summary>
    /// <param name="carrier">The message's headers or properties.</param>
    /// <param name="readHeader">Reads one field from <paramref name="carrier"/>.</param>
    public static ActivityContext ExtractTraceContext<TCarrier>(TCarrier carrier, TraceHeaderReader<TCarrier> readHeader)
    {
        ArgumentNullException.ThrowIfNull(readHeader);
        return Extract(new CarrierReader<TCarrier>(carrier, readHeader));
    }

    /// <summary>
    /// Writes the trace context of <paramref name="activity"/> — or, without one, of the ambient activity — to an
    /// outgoing message: <c>traceparent</c>, <c>tracestate</c> and the baggage, through the process's
    /// <see cref="DistributedContextPropagator"/>. A context the route received is passed on even with tracing off,
    /// so a redb hop does not cut the trace of the systems around it. Writes nothing when there is no context.
    /// </summary>
    /// <param name="activity">The send span; <c>null</c> falls back to <see cref="Activity.Current"/>.</param>
    /// <param name="carrier">The outgoing message's headers or properties.</param>
    /// <param name="writeHeader">Writes one field, replacing a value already there.</param>
    public static void InjectTraceContext<TCarrier>(Activity? activity, TCarrier carrier, TraceHeaderWriter<TCarrier> writeHeader)
    {
        ArgumentNullException.ThrowIfNull(writeHeader);
        activity ??= Activity.Current;
        if (activity is null)
            return;
        DistributedContextPropagator.Current.Inject(activity, new CarrierWriter<TCarrier>(carrier, writeHeader), Setter);
    }

    /// <summary>
    /// Marks the span failed by <paramref name="exception"/>: status <see cref="ActivityStatusCode.Error"/> and an
    /// OpenTelemetry <c>exception</c> event — the shape the route spans use, so a failed call shows red on the
    /// transport span, not only on the route above it.
    /// </summary>
    /// <param name="activity">The span; <c>null</c> does nothing.</param>
    /// <param name="exception">What failed.</param>
    public static void RecordFailure(this Activity? activity, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (activity is null)
            return;
        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        if (activity.IsAllDataRequested)
            activity.RecordException(exception);
    }

    private static void Tag(Activity? activity, string systemAttribute, string systemValue, string endpointUri,
        string? destination, string? operation)
    {
        if (activity is not { IsAllDataRequested: true })
            return;
        activity.SetTag(systemAttribute, systemValue);
        activity.SetTag("redb.route.endpoint", EndpointUri.Sanitize(endpointUri));
        // The destination is often a full URL and may carry a userinfo password or a
        // sensitive query value; Sanitize is format-preserving and leaves plain names
        // (queues, topics) byte-for-byte.
        if (!string.IsNullOrEmpty(destination))
            activity.SetTag("messaging.destination.name", EndpointUri.Sanitize(destination));
        if (!string.IsNullOrEmpty(operation))
            activity.SetTag("messaging.operation", operation);
    }

    private static ActivityContext Extract(ICarrierReader fields)
    {
        DistributedContextPropagator.Current.ExtractTraceIdAndState(fields, Getter, out var traceParent, out var traceState);
        return !string.IsNullOrEmpty(traceParent) && ActivityContext.TryParse(traceParent, traceState, out var parsed)
            ? parsed
            : default;
    }

    private interface ICarrierReader
    {
        string? Read(string name);
    }

    private sealed class CarrierReader<TCarrier>(TCarrier carrier, TraceHeaderReader<TCarrier> read) : ICarrierReader
    {
        public string? Read(string name) => read(carrier, name);
    }

    private interface ICarrierWriter
    {
        void Write(string name, string value);
    }

    private sealed class CarrierWriter<TCarrier>(TCarrier carrier, TraceHeaderWriter<TCarrier> write) : ICarrierWriter
    {
        public void Write(string name, string value) => write(carrier, name, value);
    }

    private static readonly DistributedContextPropagator.PropagatorGetterCallback Getter =
        static (object? carrier, string name, out string? value, out IEnumerable<string>? values) =>
        {
            values = null;
            value = (carrier as ICarrierReader)?.Read(name);
        };

    private static readonly DistributedContextPropagator.PropagatorSetterCallback Setter =
        static (carrier, name, value) => (carrier as ICarrierWriter)?.Write(name, value);
}

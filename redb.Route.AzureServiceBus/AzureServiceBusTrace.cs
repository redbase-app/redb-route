using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.AzureServiceBus;

/// <summary>
/// The trace carrier of a Service Bus message: its application properties. Azure names the W3C
/// <c>traceparent</c> field <c>Diagnostic-Id</c> (same value format), so that one name is mapped; <c>tracestate</c>
/// and <c>baggage</c> go under their own names. One explicit mapping, read and written the same way.
/// </summary>
internal static class AzureServiceBusTrace
{
    /// <summary>The application property Azure keeps the <c>traceparent</c> value in.</summary>
    internal const string DiagnosticId = "Diagnostic-Id";

    private const string TraceParent = "traceparent";

    private static string PropertyName(string field)
        => string.Equals(field, TraceParent, StringComparison.Ordinal) ? DiagnosticId : field;

    /// <summary>Reads one trace field from the application properties of a received message.</summary>
    internal static string? Read(IReadOnlyDictionary<string, object> properties, string field)
        => properties.TryGetValue(PropertyName(field), out var value) ? value?.ToString() : null;

    /// <summary>Writes one trace field to the application properties of an outgoing message, replacing a copied value.</summary>
    internal static void Write(IDictionary<string, object> properties, string field, string value)
        => properties[PropertyName(field)] = value;

    /// <summary>
    /// Marks <paramref name="span"/> failed by <paramref name="exception"/>, unless it is our own stop: a cancellation
    /// by <paramref name="ours"/> (the processor stopping, the caller's token). Any other cancellation, a timeout inside
    /// the route for one, is a failure.
    /// </summary>
    internal static void RecordFailure(Activity? span, Exception exception, CancellationToken ours)
    {
        if (exception is OperationCanceledException && ours.IsCancellationRequested)
            return;
        span.RecordFailure(exception);
    }

    /// <summary>
    /// Opens the receive span of <paramref name="message"/>: a child of the sender's context, or a root when the
    /// message carries none.
    /// </summary>
    internal static TransportSpan StartReceiveSpan(AzureServiceBusEndpoint endpoint, ServiceBusReceivedMessage message)
    {
        var span = RouteTelemetryExtensions.StartConsumerSpan(
            (endpoint.Component as ComponentBase)?.Context,
            $"{endpoint.EntityName} receive", ActivityKind.Consumer,
            "messaging.system", "azureservicebus", endpoint.Uri.NormalizedKey,
            message.ApplicationProperties, Read,
            destination: endpoint.EntityName, operation: "receive");
        if (span.Activity is { IsAllDataRequested: true } activity && !string.IsNullOrEmpty(message.MessageId))
            activity.SetTag("messaging.message.id", message.MessageId);
        return span;
    }
}

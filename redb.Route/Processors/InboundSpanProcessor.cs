using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Keeps the inbound span of the transport that delivered the exchange on the exchange while its route runs, for
/// <c>.SpanTag(..., SpanTarget.Inbound)</c>. The consumer opened that span around the route
/// (<see cref="RouteTelemetryExtensions.StartConsumerSpan{TCarrier}"/>), so on entry it is
/// <see cref="Activity.Current"/>. A route entered through <c>direct:</c> finds one of the caller's internal spans
/// there and keeps the caller's inbound span. Restored on exit, as <see cref="ContextStampProcessor"/> does the context.
/// </summary>
internal sealed class InboundSpanProcessor : IProcessor
{
    private readonly IProcessor _inner;

    public InboundSpanProcessor(IProcessor inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (exchange is not Exchange stampable || Activity.Current is not { } current || !IsInbound(current))
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
            return;
        }

        var previous = stampable.InboundSpan;
        stampable.InboundSpan = current;
        try
        {
            await _inner.Process(exchange, ct).ConfigureAwait(false);
        }
        finally
        {
            stampable.InboundSpan = previous;
        }
    }

    private static bool IsInbound(Activity span)
        => span.Source == RouteActivitySource.Source && span.Kind is ActivityKind.Server or ActivityKind.Consumer;
}

using System.Diagnostics;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// The single producer-side statistics funnel. Every core-owned send — a routed <c>.To()</c>
/// (<see cref="Processors.ToProcessor"/>), a template send (<see cref="ProducerTemplate"/>), and
/// the EIPs that drive a producer directly (RecipientList, Enrich/PollEnrich, DeadLetterChannel,
/// RoutingSlip, DynamicRouter) — records MessagesOut / Errors / ProcessingTime on the target
/// endpoint through this helper, exactly once. Connectors do not self-record these numbers; they
/// record only what the core cannot see (wire bytes, transport-level failures, producer-side
/// inbound operations).
/// </summary>
internal static class CountedSend
{
    internal static async Task Process(IEndpoint endpoint, IProducer producer, IExchange exchange,
        CancellationToken ct = default)
    {
        var stats = endpoint as IEndpointStatistics;
        var sw = stats is not null ? Stopwatch.StartNew() : null;
        try
        {
            await producer.Process(exchange, ct).ConfigureAwait(false);
            stats?.RecordMessageOut();
        }
        catch (Exception ex)
        {
            // Same reading as the consumer-side wrapper (BR-10): the caller cancelling its own
            // send is not the target endpoint's failure.
            if (StatisticsProcessor.IsCooperativeCancellation(ex, ct))
                stats?.RecordCancelled();
            else
                stats?.RecordError(ex);
            throw;
        }
        finally
        {
            if (sw is not null)
            {
                sw.Stop();
                stats!.RecordProcessingTime(sw.Elapsed);
            }
        }
    }
}

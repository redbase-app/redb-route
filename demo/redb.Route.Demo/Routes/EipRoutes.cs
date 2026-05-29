using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using static redb.Route.Demo.Routes.DemoHelpers;

namespace redb.Route.Demo.Routes;

/// <summary>
/// Enterprise Integration Patterns: Aggregator, Multicast, RecipientList, DynamicRouter,
/// Loop, Resequencer, Content Enricher, IdempotentConsumer, Throttle.
/// </summary>
internal sealed class EipRoutes : RouteBuilder
{
    private readonly ILogger? _log;
    public EipRoutes(ILogger? log) => _log = log;

    protected override void Configure()
    {
        ConfigureAggregatorRoute();
        ConfigureMulticastRoute();
        ConfigureRecipientListRoute();
        ConfigureDynamicRouterRoute();
        ConfigureLoopRoute();
        ConfigureResequencerRoute();
        ConfigureEnrichRoute();
        ConfigureIdempotentRoute();
        ConfigureThrottleRoute();
    }

    /// <summary>
    /// Aggregator — collects 3 messages with same batchId, merges into one.
    /// Timer sends events → Aggregate → when 3 collected → emit combined.
    /// </summary>
    private void ConfigureAggregatorRoute()
    {
        From("timer://agg-source?period=2000&repeatCount=9")
            .RouteId("demo-aggregator")
            .SetHeader("batchId", e => $"batch-{DateTime.UtcNow.Second % 3}")
            .SetBody(e => $"event-{DateTime.UtcNow:ss.fff}")
            .Log("[AGG] ▶ Event: batchId=${header.batchId}, body=${body}")

            .Aggregate(
                correlationKey: e => GetHeader(e, "batchId") ?? "default",
                aggregationStrategy: (oldEx, newEx) =>
                {
                    var oldBody = oldEx.In.Body?.ToString() ?? "";
                    var newBody = newEx.In.Body?.ToString() ?? "";
                    oldEx.In.Body = oldBody + " + " + newBody;
                    var count = oldEx.Properties.TryGetValue("agg.count", out var c) ? (int)c! : 1;
                    oldEx.Properties["agg.count"] = count + 1;
                    return oldEx;
                },
                completionPredicate: e =>
                    e.Properties.TryGetValue("agg.count", out var c) && (int)c! >= 3)

            .Log("[AGG] ✔ Aggregated 3 events: ${body}");
    }

    /// <summary>
    /// Multicast — send one message to multiple endpoints in parallel.
    /// Like a TV broadcast: one input, many outputs.
    /// </summary>
    private void ConfigureMulticastRoute()
    {
        From("direct://demo-multicast")
            .RouteId("demo-multicast")
            .Log("[MCAST] ▶ Broadcasting to 3 endpoints...")
            .Multicast(
                new[] { "direct://mcast-a", "direct://mcast-b", "direct://mcast-c" },
                parallelProcessing: true)
            .Log("[MCAST] ◀ All endpoints received the message");

        From("direct://mcast-a")
            .RouteId("demo-mcast-a")
            .Log("[MCAST-A] ▶ Received copy: ${body}")
            .SetHeader("mcast.a", "done");

        From("direct://mcast-b")
            .RouteId("demo-mcast-b")
            .Log("[MCAST-B] ▶ Received copy: ${body}")
            .Delay(TimeSpan.FromMilliseconds(100))
            .Log("[MCAST-B] ◀ Done after 100ms delay");

        From("direct://mcast-c")
            .RouteId("demo-mcast-c")
            .Log("[MCAST-C] ▶ Received copy: ${body}")
            .SetHeader("mcast.c", "done");
    }

    /// <summary>
    /// RecipientList — dynamically choose endpoints from the exchange.
    /// Header "targets" = "a,b" → routes to direct://rcpt-a, direct://rcpt-b.
    /// </summary>
    private void ConfigureRecipientListRoute()
    {
        From("direct://demo-recipient-list")
            .RouteId("demo-recipient-list")
            .Log("[RCPT] ▶ Routing to dynamic recipients: targets=${header.targets}")
            .RecipientList(
                e =>
                {
                    var targets = GetHeader(e, "targets") ?? "a";
                    return targets.Split(',').Select(t => $"direct://rcpt-{t.Trim()}");
                },
                parallelProcessing: true)
            .Log("[RCPT] ◀ All recipients processed");

        From("direct://rcpt-a")
            .RouteId("demo-rcpt-a")
            .Log("[RCPT-A] ▶ Got message: ${body}");

        From("direct://rcpt-b")
            .RouteId("demo-rcpt-b")
            .Log("[RCPT-B] ▶ Got message: ${body}");
    }

    /// <summary>
    /// DynamicRouter — state machine routing. The routing function decides
    /// the next endpoint after each hop. Returns null to stop.
    /// </summary>
    private void ConfigureDynamicRouterRoute()
    {
        From("direct://demo-dynamic-router")
            .RouteId("demo-dynamic-router")
            .SetProperty("router.step", 0)
            .Log("[DROUTER] ▶ Starting dynamic routing...")

            .DynamicRouter(e =>
            {
                var step = e.Properties.TryGetValue("router.step", out var s) ? (int)s! : 0;
                e.Properties["router.step"] = step + 1;
                return step switch
                {
                    0 => "direct://drouter-validate",
                    1 => "direct://drouter-transform",
                    2 => "direct://drouter-store",
                    _ => null  // null = stop routing
                };
            })

            .Log("[DROUTER] ◀ Complete, went through ${property.router.step} hops");

        From("direct://drouter-validate")
            .RouteId("demo-drouter-validate")
            .Log("[DROUTER] ▸ Step 1: Validate")
            .SetHeader("validated", "true");

        From("direct://drouter-transform")
            .RouteId("demo-drouter-transform")
            .Log("[DROUTER] ▸ Step 2: Transform")
            .Process(e => e.In.Body = e.In.Body?.ToString()?.ToUpperInvariant());

        From("direct://drouter-store")
            .RouteId("demo-drouter-store")
            .Log("[DROUTER] ▸ Step 3: Store — body=${body}");
    }

    /// <summary>
    /// Loop — repeat a block of processing N times.
    /// Each iteration appends "→loop" to the body.
    /// </summary>
    private void ConfigureLoopRoute()
    {
        From("direct://demo-loop")
            .RouteId("demo-loop")
            .Log("[LOOP] ▶ Looping 3 times...")

            .Loop(3, body =>
            {
                body
                    .Log("[LOOP]   iteration...")
                    .Process(e =>
                    {
                        var current = e.In.Body?.ToString() ?? "";
                        e.In.Body = current + "→loop";
                    });
            })

            .Log("[LOOP] ◀ Result: ${body}");
    }

    /// <summary>
    /// Resequencer — collects messages, sorts by seqNum header, delivers in order.
    /// Messages arrive 3,1,2 → leave 1,2,3.
    /// </summary>
    private void ConfigureResequencerRoute()
    {
        From("direct://demo-resequencer")
            .RouteId("demo-resequencer")
            .Log("[RESEQ] ▶ Received seq=${header.seqNum}: ${body}")

            .Resequence(
                e => long.TryParse(GetHeader(e, "seqNum"), out var n) ? n : 0,
                batchSize: 5,
                timeout: TimeSpan.FromSeconds(3))

            .Log("[RESEQ] ◀ Delivered in order: seq=${header.seqNum}, body=${body}");
    }

    /// <summary>
    /// Content Enricher — call a direct endpoint, merge the result into
    /// the current message as a header.
    /// </summary>
    private void ConfigureEnrichRoute()
    {
        From("direct://demo-enrich")
            .RouteId("demo-enrich")
            .Log("[ENRICH] ▶ Original body: ${body}")
            .Enrich("direct://enrichment-source", (original, enriched) =>
            {
                original.In.Headers["enriched.data"] = enriched.In.Body?.ToString();
                return original;
            })
            .Log("[ENRICH] ◀ Enriched: body=${body}, enriched.data=${header.enriched.data}");

        From("direct://enrichment-source")
            .RouteId("demo-enrichment-source")
            .Log("[ENRICH-SRC] ▶ Providing enrichment data")
            .SetBody(e => $"extra-info-for-{GetHeader(e, "traceId") ?? "unknown"}")
            .Log("[ENRICH-SRC] ◀ Returning: ${body}");
    }

    /// <summary>
    /// IdempotentConsumer — deduplicates by messageId header.
    /// Second call with same ID is silently skipped.
    /// </summary>
    private void ConfigureIdempotentRoute()
    {
        From("direct://demo-idempotent")
            .RouteId("demo-idempotent")
            .Log("[IDEMP] ▶ messageId=${header.messageId}")
            .IdempotentConsumer(
                e => GetHeader(e, "messageId") ?? Guid.NewGuid().ToString(),
                new InMemoryIdempotentRepository(),
                skipDuplicate: true)
            .Log("[IDEMP] ✓ First time seeing this message, processing...")
            .Process(e => e.In.Headers["idempotent.processed"] = "true")
            .Log("[IDEMP] ◀ Done");
    }

    /// <summary>
    /// Throttle as standalone route — 5 requests/sec rate limit.
    /// </summary>
    private void ConfigureThrottleRoute()
    {
        From("direct://demo-throttle")
            .RouteId("demo-throttle")
            .Log("[THROTTLE] ▶ Incoming request...")
            .Throttle(5, TimeSpan.FromSeconds(1))
            .Log("[THROTTLE] ✓ Passed rate limiter (5/sec)")
            .Log("[THROTTLE] ◀ Done");
    }
}

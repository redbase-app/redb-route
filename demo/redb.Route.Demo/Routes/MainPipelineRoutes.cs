using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using static redb.Route.Demo.Routes.DemoEndpoints;
using static redb.Route.Demo.Routes.DemoHelpers;

namespace redb.Route.Demo.Routes;

/// <summary>
/// Main pipeline: HTTP entry, cross-transport pipeline (RabbitMQ → AMQP → gRPC →
/// WMQ → DirectVM → SQL → Kafka/File/VM/Redis/MQTT/SEDA WireTaps) and 6 RPC workers.
/// Owns the GLOBAL <c>OnException</c> handler shared by every demo route.
/// </summary>
internal sealed class MainPipelineRoutes : RouteBuilder
{
    private readonly ILogger? _log;
    public MainPipelineRoutes(ILogger? log) => _log = log;

    protected override void Configure()
    {
        // ── Global error handler ────────────────────────────────────────
        // Any unhandled exception in ANY route → retry 2× → log → mark handled
        OnException<Exception>()
            .MaximumRedeliveries(2)
            .RedeliveryDelay(TimeSpan.FromSeconds(1))
            .UseExponentialBackOff()
            .BackOffMultiplier(2.0)
            .Handled(true)
            .Process(e =>
            {
                _log?.LogError(
                    "[GLOBAL-ERR] routeId={RouteId}, exType={ExType}, exMsg={ExMsg}, bodyType={BodyType}, body={Body}, headers={Headers}",
                    e.RouteId ?? "(null)",
                    e.Exception?.GetType().Name ?? "(null)",
                    Trunc(e.Exception?.Message),
                    e.In.Body?.GetType().Name ?? "(null)",
                    Trunc(e.In.Body?.ToString()) ?? "(null)",
                    string.Join(", ", e.In.Headers.Select(h => $"{h.Key}={h.Value}")));
            })
            .Log("[GLOBAL-ERR] ✖ ${exception.message} → handled after retries");

        ConfigureHttpEntry();
        ConfigurePipeline();
        ConfigureRabbitWorker();
        ConfigureAmqpWorker();
        ConfigureGrpcWorker();
        ConfigureWmqWorker();
        ConfigureDirectVmEnricher();
        ConfigureVmAuditConsumer();
    }

    /// <summary>
    /// Route 1: HTTP entry point.
    /// POST /api/demo → validate → throttle → dedup → pipeline
    /// </summary>
    private void ConfigureHttpEntry()
    {
        From("http:0.0.0.0:5088/api/demo?inOut=true")
            .RouteId("demo-http-entry")

            // HTTP body arrives as byte[] — convert to string first
            .ConvertBody<string>()

            // Throttle: max 10 requests per second
            .Throttle(10)
            .Log("[1-HTTP] ▶ Received: body=${body}, contentType=${contentType}")

            // Generate trace ID
            .SetHeader("traceId", e => Guid.NewGuid().ToString("N")[..12])
            .SetHeader("startedAt", e => DateTime.UtcNow.ToString("o"))
            .Log("[1-HTTP]   traceId=${header.traceId}, mode=${header.mode}, priority=${header.priority}")

            // Validate — body must be JSON with "message" field
            .ValidateJsonSchema(MessageSchema)
            .Log("[1-HTTP] ✓ JSON schema valid")

            // Idempotent Consumer — dedup by traceId (same message won't process twice)
            .IdempotentConsumer(
                e => GetHeader(e, "traceId") ?? "",
                IdempotentRepo)
            .Log("[1-HTTP] ✓ Not a duplicate")

            // Forward to pipeline
            .To("direct://pipeline");
    }

    /// <summary>
    /// Route 2: Main processing pipeline.
    /// Filter → Choice → Traced(RabbitMQ → AMQP → gRPC RPC) → DirectVM enrich
    /// → Metered(BeginTx → SQL INSERT → SQL SELECT → CommitTx)
    /// → WireTap(Kafka, File, VM) → build response
    /// </summary>
    private void ConfigurePipeline()
    {
        From("direct://pipeline")
            .RouteId("demo-pipeline")
            .Log("[2-PIPE] ▶ Pipeline started, traceId=${header.traceId}")

            // ── Filter — drop empty bodies ──
            .Filter(e => !string.IsNullOrEmpty(e.In.Body?.ToString()))
            .Log("[2-PIPE] ✓ Body is non-empty")

            // ── Store processing context in exchange properties ──
            .SetProperty("pipelineStartMs", e => Environment.TickCount64)
            .Log("[2-PIPE]   pipelineStartMs = ${property.pipelineStartMs}")

            // ── Choice — branch by mode header (nested-lambda DSL) ──
            .Choice(choice => choice
                .When(e => GetHeader(e, "mode") == "full", full => full
                    .Process(e => e.In.Headers["fastTrack"] = GetHeader(e, "priority") == "high" ? "true" : "false"))
                .When(e => GetHeader(e, "mode") == "short", shortMode => shortMode
                    .SetHeader("fastTrack", "false"))
                .Otherwise(other => other
                    .SetHeader("mode", "default")
                    .SetHeader("fastTrack", "false")))

            .Log("[2-PIPE] ▸ mode=${header.mode}, fastTrack=${header.fastTrack}")

            // ── Traced block: wrap broker calls in a single trace span ──
            .Traced("broker-roundtrips")

                // RabbitMQ RPC
                .Log("[3-RABBIT] → Sending to RabbitMQ RPC...")
                .To(RabbitProducer)
                .Log("[3-RABBIT] ← stamp.rabbit=${header.stamp.rabbit}")

                // AMQP RPC
                .Log("[4-AMQP] → Sending to AMQP/Artemis RPC...")
                .To(AmqpProducer)
                .Log("[4-AMQP] ← stamp.amqp=${header.stamp.amqp}")

                // gRPC
                .Log("[5-GRPC] → Sending to gRPC server...")
                .To(GrpcProducer)
                .Log("[5-GRPC] ← stamp.grpc=${header.stamp.grpc}")

                // IBM MQ RPC
                .Log("[6-WMQ] → Sending to IBM MQ RPC...")
                .To(WmqRpcProducer)
                .Log("[6-WMQ] ← stamp.wmq=${header.stamp.wmq}")

            .EndTraced()

            // ── ConvertBody — gRPC returns byte[], convert to string ──
            .ConvertBody<string>()
            .Log("[5-GRPC] ✓ Body converted to string: ${body}")

            // ── DirectVM enrichment (cross-context sync call) ──
            .Log("[5b-DVM] → Calling direct-vm://enricher...")
            .To("direct-vm://enricher")
            .Log("[5b-DVM] ← stamp.vm=${header.stamp.vm}")

            // ── Metered block: wrap SQL operations with metrics ──
            // Save original body — SQL SELECT will replace it with List<Dict>
            .SetProperty("originalBody", e => e.In.Body)

            .Metered("sql-operations")

                // Begin imperative transaction
                .BeginTransaction()
                .Log("[6-TX] ▶ Transaction opened")

                // SQL INSERT
                .Log("[6-SQL] → INSERT demo_log: traceId=${header.traceId}, mode=${header.mode}")
                .To(SqlInsert)
                .Log("[6-SQL] ← Insert complete")

                // SQL SELECT — read last 5 rows
                .Log("[6b-SQL] → SELECT last 5 rows from demo_log")
                .To(SqlSelect)
                .Split(Body())
                    .Log("[6b-SQL]  row: id=${body['id']}, msg=${body['message']}, status=${body['status']}")
                .End()

                // Commit transaction
                .CommitTransaction()
                .Log("[6-TX] ✔ Transaction committed")

            .EndMetered()

            // Restore original body after SQL SELECT replaced it
            .SetBody(e => e.Properties.TryGetValue("originalBody", out var b) ? b : e.In.Body)
            .Log("[6-RESTORE] Body restored: ${body}")

            // ── Calculate elapsed ──
            .SetProperty("pipelineElapsedMs", e =>
                Environment.TickCount64 - (long)(e.Properties.TryGetValue("pipelineStartMs", out var s) ? s! : 0L))

            // ── WireTap — async forks ──
            .WireTap(KafkaWireTap)
            .Log("[7-TAP] → Kafka: demo-audit topic")
            .WireTap(FileWireTap, newBodyFactory: e => BuildResponse(e))
            .Log("[7-TAP] → File: output/${header.traceId}.json")
            .WireTap("vm://audit-log")
            .Log("[7-TAP] → VM: async audit-log queue")

            // ── Transport showcases — wire standalone routes into the flow ──
            .WireTap("direct://demo-redis-pub")
            .Log("[7-TAP] → Redis: demo-events channel")
            .WireTap("direct://demo-mqtt-pub")
            .Log("[7-TAP] → MQTT: demo/telemetry topic")
            .WireTap("direct://demo-wmq-pub")
            .Log("[7-TAP] → WMQ: demo/telemetry topic")
            .WireTap("direct://demo-seda-send")
            .Log("[7-TAP] → SEDA: work-queue")

            // ── Build HTTP response ──
            .Log("[8-DONE] ✔ Pipeline complete for traceId=${header.traceId}, elapsed=${property.pipelineElapsedMs}ms")
            .RemoveHeader("fastTrack")           // cleanup internal header
            .SetHeader("Content-Type", "application/json")
            .SetBody(e => BuildResponse(e));
    }

    // ── Workers ─────────────────────────────────────────────────────────────

    /// <summary>Route 3: RabbitMQ echo — stamp and reply.</summary>
    private void ConfigureRabbitWorker()
    {
        From(RabbitConsumer)
            .RouteId("demo-rabbit-worker")
            .Log("[RABBIT-W] ▶ Received: ${body}")
            .SetHeader("stamp.rabbit", e => $"ok:{DateTime.UtcNow:HH:mm:ss.fff}")
            .Log("[RABBIT-W] ◀ Stamped, replying");
    }

    /// <summary>Route 4: AMQP echo — stamp and reply.</summary>
    private void ConfigureAmqpWorker()
    {
        From(AmqpConsumer)
            .RouteId("demo-amqp-worker")
            .Log("[AMQP-W] ▶ Received: ${body}")
            .SetHeader("stamp.amqp", e => $"ok:{DateTime.UtcNow:HH:mm:ss.fff}")
            .Log("[AMQP-W] ◀ Stamped, replying");
    }

    /// <summary>Route 5: gRPC echo — stamp and reply.</summary>
    private void ConfigureGrpcWorker()
    {
        From(GrpcConsumer)
            .RouteId("demo-grpc-worker")
            .Log("[GRPC-W] ▶ Received: ${body}")
            .SetHeader("stamp.grpc", e => $"ok:{DateTime.UtcNow:HH:mm:ss.fff}")
            .Log("[GRPC-W] ◀ Stamped, replying");
    }

    /// <summary>Route 6: IBM MQ echo — stamp and reply (RPC worker).</summary>
    private void ConfigureWmqWorker()
    {
        From(WmqRpcConsumer)
            .RouteId("demo-wmq-worker")
            .Log("[WMQ-W] ▶ Received: ${body}")
            .SetHeader("stamp.wmq", e => $"ok:{DateTime.UtcNow:HH:mm:ss.fff}")
            .Log("[WMQ-W] ◀ Stamped, replying");
    }

    /// <summary>Route 7: DirectVM enricher — can live in another module.</summary>
    private void ConfigureDirectVmEnricher()
    {
        From("direct-vm://enricher")
            .RouteId("demo-vm-enricher")
            .Log("[DVM-W] ▶ Enriching: traceId=${header.traceId}")
            .SetHeader("stamp.vm", e => $"enriched:{DateTime.UtcNow:HH:mm:ss.fff}")
            .Log("[DVM-W] ◀ Enriched, returning");
    }

    /// <summary>Route 8: VM async audit consumer — processes WireTap events.</summary>
    private void ConfigureVmAuditConsumer()
    {
        From("vm://audit-log?concurrentConsumers=2")
            .RouteId("demo-vm-audit")
            .Log("[VM-AUDIT] ▶ Audit event: traceId=${header.traceId}, mode=${header.mode}")
            .Process(e =>
            {
                var traceId = GetHeader(e, "traceId");
                var mode = GetHeader(e, "mode");
                e.In.Headers["audit.processed"] = $"{traceId}:{mode}:{DateTime.UtcNow:HH:mm:ss.fff}";
            })
            .Log("[VM-AUDIT] ◀ Processed: ${header.audit.processed}");
    }
}

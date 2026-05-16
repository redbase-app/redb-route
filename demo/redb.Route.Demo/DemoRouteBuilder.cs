using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RedbCore.Extensions;
using redb.Route.IbmMq;
using redb.Route.MqttNet;
using redb.Route.Processors;
using redb.Route.Serialization;

namespace redb.Route.Demo;

// ═══════════════════════════════════════════════════════════════════════════════
//   DemoRouteBuilder — FULL DSL SHOWCASE
// ═══════════════════════════════════════════════════════════════════════════════
//
//   Read each route top-to-bottom: the message flows like water through pipes.
//   Every step has a Log() so you can see exactly what happens when it runs.
//
//   Windows (cmd/PowerShell):
//     curl -X POST http://localhost:5088/api/demo -H "Content-Type: application/json" -H "mode: full" -H "priority: high" -d "{\"message\":\"hello\"}"
//   Linux/macOS:
//     curl -X POST http://localhost:5088/api/demo -H "Content-Type: application/json" -H "mode: full" -H "priority: high" -d '{"message":"hello"}'
//
// ═════════════════════════════════════════════════════════════════════════════

public class DemoRouteBuilder : RouteBuilder
{
    private readonly ILogger? _log;

    public DemoRouteBuilder(ILogger? logger = null) => _log = logger;

    // ── Connection strings ──────────────────────────────────────────────────

    // RabbitMQ
    private const string RabbitConsumer =
        "rabbitmq://demo-rpc-queue?host=localhost&port=5672&username=admin&password=admin&declare=true";
    private const string RabbitProducer =
        "rabbitmq://demo-rpc-queue?host=localhost&port=5672&username=admin&password=admin&replyTo=true&timeout=15";

    // AMQP 1.0 / Artemis
    private const string AmqpConsumer =
        "amqp://demo-amqp-queue?host=localhost&port=5673&user=admin&password=admin";
    private const string AmqpProducer =
        "amqp://demo-amqp-queue?host=localhost&port=5673&user=admin&password=admin&replyTo=true&timeout=15";

    // IBM MQ (RPC queue + topic pub/sub)
    private const string WmqRpcConsumer =
        "wmq:DEV.QUEUE.3?host=localhost&port=1414&channel=DEV.APP.SVRCONN&queueManager=QM1&user=app&password=admin";
    private const string WmqRpcProducer =
        "wmq:DEV.QUEUE.3?host=localhost&port=1414&channel=DEV.APP.SVRCONN&queueManager=QM1&user=app&password=admin&replyTo=true&timeout=15";

    // gRPC
    private const string GrpcConsumer = "grpc:0.0.0.0:50051";
    private const string GrpcProducer = "grpc:127.0.0.1:50051?plaintext=true";

    // Kafka (fire-and-forget via WireTap)
    private const string KafkaWireTap = "kafka://demo-audit?brokers=localhost:29092";

    // File (snapshot via WireTap)
    private const string FileWireTap =
        "file:///C:/Work/cp_tmp/redb.Route.Demo/output?fileName=${header.traceId}.json&autoCreate=true";

    // SQL
    private const string SqlInsert =
        "sql:INSERT INTO demo_log(exchange_id, message, status) VALUES(@exchange_id, @message, @status)"
        + "?dataSource=#pg-demo"
        + "&param.exchange_id=${header.traceId}"
        + "&param.message=${body}"
        + "&param.status=${header.mode}";

    private const string SqlSelect =
        "sql:SELECT id, exchange_id, message, status, created_at FROM demo_log ORDER BY id DESC LIMIT 5"
        + "?dataSource=#pg-demo&outputType=SelectList";

    // Redis
    private const string RedisPub = "redis:PUBLISH:demo-events?host=localhost";
    private const string RedisSub = "redis:SUBSCRIBE:demo-events?host=localhost";

    // TCP
    private const string TcpServer = "tcp://0.0.0.0:9099";
    private const string TcpProducer = "tcp://localhost:9099?reconnect=true";

    // WebSocket
    private const string WsServer = "ws://0.0.0.0:9091/demo";

    // MQTT
    // MQTT
    private const string MqttPub = "mqtt:demo/telemetry?mode=Publish&server=localhost&port=11883";
    private const string MqttSub = "mqtt:demo/telemetry?mode=Subscribe&server=localhost&port=11883";

    // Dead-letter queue (SEDA in-memory)
    private const string DeadLetterQueue = "seda://dead-letters?size=1000";

    // JSON Schema for validation
    private const string MessageSchema = """
        {
            "type": "object",
            "required": ["message"],
            "properties": {
                "message": { "type": "string", "minLength": 1 }
            }
        }
        """;

    // Idempotent repository (dedup)
    private static readonly InMemoryIdempotentRepository IdempotentRepo = new();


    // ════════════════════════════════════════════════════════════════════════
    //  CONFIGURE — the master wiring of all routes
    // ════════════════════════════════════════════════════════════════════════

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
            .Process(e => _log?.LogError(
                "[GLOBAL-ERR] routeId={RouteId}, exType={ExType}, exMsg={ExMsg}, bodyType={BodyType}, body={Body}, headers={Headers}",
                e.RouteId ?? "(null)",
                e.Exception?.GetType().Name ?? "(null)",
                e.Exception?.Message?[..Math.Min(200, e.Exception?.Message?.Length ?? 0)],
                e.In.Body?.GetType().Name ?? "(null)",
                e.In.Body?.ToString()?[..Math.Min(200, e.In.Body?.ToString()?.Length ?? 0)] ?? "(null)",
                string.Join(", ", e.In.Headers.Select(h => $"{h.Key}={h.Value}"))))
            .Log("[GLOBAL-ERR] ✖ ${exception.message} → handled after retries");

        // ── Section 1: Main Pipeline (11 transports) ────────────────────
        ConfigureHttpEntry();
        ConfigurePipeline();
        ConfigureRabbitWorker();
        ConfigureAmqpWorker();
        ConfigureGrpcWorker();
        ConfigureWmqWorker();
        ConfigureDirectVmEnricher();
        ConfigureVmAuditConsumer();

        // ── Section 2: Error Handling ───────────────────────────────────
        ConfigureTryCatchRoute();
        ConfigureCircuitBreakerRoute();
        ConfigureRetryRoute();
        ConfigureDeadLetterRoute();

        // ── Section 3: EIP Patterns ─────────────────────────────────────
        ConfigureAggregatorRoute();
        ConfigureMulticastRoute();
        ConfigureRecipientListRoute();
        ConfigureDynamicRouterRoute();
        ConfigureLoopRoute();
        ConfigureResequencerRoute();
        ConfigureEnrichRoute();
        ConfigureIdempotentRoute();
        ConfigureThrottleRoute();

        // ── Section 4: Transport Showcase ───────────────────────────────
        ConfigureTimerHeartbeat();
        ConfigureCronJob();
        ConfigureSedaProcessing();
        ConfigureRedisRoutes();
        ConfigureTcpEchoServer();
        ConfigureWebSocketServer();
        ConfigureMqttRoutes();
        ConfigureWmqRoutes();
        ConfigureSlowProcessRoute();

        // ── Section 5: Data & Observability ─────────────────────────────
        ConfigureValidationRoute();
        ConfigureMarshalRoute();
        ConfigureObservabilityRoute();
        ConfigureExpressionShowcase();
        ConfigurePredicateShowcase();

        // ── Section 6: Transactions ─────────────────────────────────────
        ConfigureTransactedRoute();
        ConfigureImperativeTxRoute();

        // ── Section 7: Policies & Lifecycle ─────────────────────────────
        ConfigurePolicyShowcaseRoute();
        ConfigureClusterReadyRoute();

        // ── Section 8: Named Redb Instances ─────────────────────────────
        ConfigureNamedRedbRoute();

        // ── Section 9: Scope Diagnostics ────────────────────────────────
        ConfigureScopeDiagRoute();
    }


    // ════════════════════════════════════════════════════════════════════════
    //  SECTION 1: MAIN PIPELINE — 11 transports in one chain
    // ════════════════════════════════════════════════════════════════════════

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
                e => e.In.Headers.TryGetValue("traceId", out var id) ? id?.ToString() ?? "" : "",
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

            // ── Choice — branch by mode header ──
            .Choice()
                .When(e => GetHeader(e, "mode") == "full")
                    .Log("[2-CHOICE] ★ Mode=FULL selected")
                    .Choice()
                        .When(e => GetHeader(e, "priority") == "high")
                            .Log("[2-NESTED] ★★ High priority → fastTrack=true")
                            .SetHeader("fastTrack", "true")
                        .Otherwise()
                            .Log("[2-NESTED] Normal priority in FULL mode")
                            .SetHeader("fastTrack", "false")
                    .EndChoice()

                .When(e => GetHeader(e, "mode") == "short")
                    .Log("[2-CHOICE] ⚡ Mode=SHORT selected")
                    .SetHeader("fastTrack", "false")

                .Otherwise()
                    .Log("[2-CHOICE] Default mode (no header)")
                    .SetHeader("mode", "default")
                    .SetHeader("fastTrack", "false")
            .EndChoice()

            .Log("[2-PIPE] ▸ mode=${header.mode}, fastTrack=${header.fastTrack}")

            // ── Traced block: wrap broker calls in a single trace span ──
            .Traced("broker-roundtrips")

                // RabbitMQ RPC
                .Log("[3-RABBIT] → Sending to RabbitMQ RPC...")
                .To(RabbitProducer)
                .Log("[3-RABBIT] ← stamp.rabbit=${header.stamp.rabbit}")
                .Log(LogLevel.Information)
                    .Message("[3-DIAG] body after Rabbit: ${body}")
                    .Header("stamp.rabbit")
                    .ShowRouteId()
                .EndLog()

                // AMQP RPC
                .Log("[4-AMQP] → Sending to AMQP/Artemis RPC...")
                .To(AmqpProducer)
                .Log("[4-AMQP] ← stamp.amqp=${header.stamp.amqp}")
                .Log(LogLevel.Information)
                    .Message("[4-DIAG] body after AMQP: ${body}")
                    .Header("stamp.amqp")
                    .ShowRouteId()
                .EndLog()

                // gRPC
                .Log("[5-GRPC] → Sending to gRPC server...")
                .To(GrpcProducer)
                .Log("[5-GRPC] ← stamp.grpc=${header.stamp.grpc}")

                // IBM MQ RPC
                .Log("[6-WMQ] → Sending to IBM MQ RPC...")
                .To(WmqRpcProducer)
                .Log("[6-WMQ] ← stamp.wmq=${header.stamp.wmq}")
                .Log(LogLevel.Information)
                    .Message("[6-DIAG] body after WMQ: ${body}")
                    .Header("stamp.wmq")
                    .ShowRouteId()
                .EndLog()

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

    /// <summary>Route 7: VM async audit consumer — processes WireTap events.</summary>
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


    // ════════════════════════════════════════════════════════════════════════
    //  SECTION 2: ERROR HANDLING
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// DoTry / DoCatch / DoFinally — structured error handling inside a route.
    /// Message flows: try body → catch if error → finally always.
    /// </summary>
    private void ConfigureTryCatchRoute()
    {
        From("direct://demo-try-catch")
            .RouteId("demo-try-catch")
            .Log("[TRY-CATCH] ▶ Starting risky operation...")

            .DoTry()
                .Log("[TRY-CATCH]   try: parsing body...")
                .Process(e =>
                {
                    var body = e.In.Body?.ToString() ?? "";
                    if (body.Contains("BOOM")) throw new InvalidOperationException("Body contains BOOM!");
                    e.In.Headers["parsed"] = "true";
                })
                .Log("[TRY-CATCH]   try: ✓ parsed OK")

            .DoCatch<InvalidOperationException>()
                .Log("[TRY-CATCH]   catch: ✖ InvalidOperation — ${exception.message}")
                .SetHeader("parsed", "false")
                .SetHeader("error", e => e.Exception?.Message ?? "unknown")

            .DoCatch<Exception>()
                .Log("[TRY-CATCH]   catch: ✖ General error — ${exception.message}")
                .SetHeader("parsed", "false")

            .DoFinally()
                .Log("[TRY-CATCH]   finally: cleanup, parsed=${header.parsed}")
                .RemoveHeader("tempData")

            .End()

            .Log("[TRY-CATCH] ◀ Done, parsed=${header.parsed}");
    }

    /// <summary>
    /// CircuitBreaker — protects against cascading failures.
    /// 3 failures → circuit opens → fallback → auto-recovery after 10s.
    /// </summary>
    private void ConfigureCircuitBreakerRoute()
    {
        From("direct://demo-circuit-breaker")
            .RouteId("demo-circuit-breaker")
            .Log("[CB] ▶ Calling unreliable service...")

            .CircuitBreaker(cb => cb
                .Threshold(3)
                .ResetTimeout(TimeSpan.FromSeconds(10))
                .HalfOpenMaxCalls(1)
                .FallBack(fb => fb
                    .Log("[CB] ⚡ FALLBACK: circuit is open, returning cached response")
                    .SetBody(e => "{\"source\":\"cache\",\"note\":\"circuit breaker active\"}")
                    .SetHeader("cb.state", "open")))

            .Log("[CB]   → calling external API...")
            .Process(e =>
            {
                if (GetHeader(e, "fail") == "true")
                    throw new TimeoutException("External API timed out!");
            })
            .Log("[CB] ◀ Response: ${body}");
    }

    /// <summary>
    /// Retry — automatic retry with exponential backoff.
    /// Fails twice, succeeds on third attempt.
    /// </summary>
    private void ConfigureRetryRoute()
    {
        From("direct://demo-retry")
            .RouteId("demo-retry")
            .Log("[RETRY] ▶ Processing with retry policy...")

            .Retry(3, TimeSpan.FromMilliseconds(500))
            .Log("[RETRY]   → attempt...")
            .Process(e =>
            {
                var attempt = e.Properties.TryGetValue("RetryAttempt", out var a) ? (int)a! : 0;
                e.In.Headers["retry.attempt"] = attempt.ToString();
                if (attempt < 2) throw new TimeoutException($"Timeout on attempt {attempt}");
            })
            .Log("[RETRY] ✓ Succeeded on attempt ${header.retry.attempt}")

            .Log("[RETRY] ◀ Done");
    }

    /// <summary>
    /// DeadLetterChannel — failed messages go to a dead-letter SEDA queue
    /// instead of being lost. Separate consumer processes them.
    /// </summary>
    private void ConfigureDeadLetterRoute()
    {
        From("direct://demo-dead-letter")
            .RouteId("demo-dead-letter")
            .Log("[DLQ] ▶ Processing message...")
            .DeadLetterChannel(DeadLetterQueue)
            .Process(e =>
            {
                if (GetHeader(e, "poison") == "true")
                    throw new InvalidOperationException("Poison message detected!");
            })
            .Log("[DLQ] ✓ Message processed successfully");

        // Dead-letter consumer
        From(DeadLetterQueue)
            .RouteId("demo-dlq-consumer")
            .Log("[DLQ-SINK] ▶ Dead letter received: ${body}")
            .Log("[DLQ-SINK]   exception: ${exception.message}")
            .SetHeader("dlq.receivedAt", e => DateTime.UtcNow.ToString("o"))
            .Log("[DLQ-SINK] ◀ Archived");

        // Poison message sender — cron uses this to exercise the DLQ path
        From("direct://demo-dead-letter-poison")
            .RouteId("demo-dead-letter-poison")
            .SetHeader("poison", "true")
            .Log("[DLQ-POISON] ▶ Sending poison message to DLQ route...")
            .To("direct://demo-dead-letter");
    }


    // ════════════════════════════════════════════════════════════════════════
    //  SECTION 3: EIP PATTERNS
    // ════════════════════════════════════════════════════════════════════════

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


    // ════════════════════════════════════════════════════════════════════════
    //  SECTION 4: TRANSPORT SHOWCASE
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Timer — fires every 10s. Good for heartbeats and health checks.
    /// </summary>
    private void ConfigureTimerHeartbeat()
    {
        From("timer://heartbeat?period=10000")
            .RouteId("demo-timer-heartbeat")
            .Log("[TIMER] ♥ Heartbeat at ${header.firedTime}")
            .SetBody(e => new { alive = true, ts = DateTime.UtcNow })
            .Marshal(typeof(JsonMessageSerializer))
            .ConvertBody<string>()
            .Log("[TIMER] ♥ Serialized: ${body}")

            // TCP roundtrip — send heartbeat JSON to TCP echo, get UPPERCASE back
            .To(TcpProducer)
            .Log("[TIMER]   → TCP echo: ${body}");
    }

    /// <summary>
    /// Quartz cron — fires every 30 seconds on a schedule.
    /// </summary>
    /// <summary>
    /// Quartz cron — fires every 30 seconds.
    /// Acts as the self-test driver: exercises all showcase routes
    /// so every route appears in the logs during normal operation.
    /// </summary>
    private void ConfigureCronJob()
    {
        From("cron://demo-cron?schedule=0/30 * * * * ?")
            .RouteId("demo-cron-job")
            .Log("[CRON] ⏰ Cron fired at ${header.firedTime}")

            // Prepare self-test body and headers for downstream routes
            .SetBody(e => "{\"message\":\"cron-self-test\"}")
            .SetHeader("traceId", e => $"cron-{DateTime.UtcNow:HHmmss}")
            .SetHeader("mode", "cron")
            .SetHeader("messageId", e => Guid.NewGuid().ToString("N")[..8])
            .SetHeader("targets", "a,b")
            .SetHeader("seqNum", e => (DateTime.UtcNow.Second % 5 + 1).ToString())
            .Log("[CRON] ▶ Self-test: exercising all showcase routes...")

            // ── Section 2: Error handling ──
            // try-catch catches internally, circuit-breaker has fallback,
            // DLQ only throws on poison=true (not set by cron),
            // retry throws 2× then succeeds → 2 WRN lines are expected showcase behavior
            .WireTap("direct://demo-try-catch")
            .WireTap("direct://demo-circuit-breaker")
            .WireTap("direct://demo-retry")
            .WireTap("direct://demo-dead-letter")
            .WireTap("direct://demo-dead-letter-poison")
            .WireTap("direct://demo-ws-ping")
            .Log("[CRON]   ✓ Error handling routes exercised")

            // ── Section 3: EIP patterns ──
            .WireTap("direct://demo-multicast")
            .WireTap("direct://demo-recipient-list")
            .WireTap("direct://demo-dynamic-router")
            .WireTap("direct://demo-loop")
            .WireTap("direct://demo-resequencer")
            .WireTap("direct://demo-enrich")
            .WireTap("direct://demo-idempotent")
            .WireTap("direct://demo-throttle")
            .Log("[CRON]   ✓ EIP pattern routes exercised")

            // ── Section 5: Data & observability ──
            .WireTap("direct://demo-validation")
            .WireTap("direct://demo-marshal")
            .WireTap("direct://demo-observability")
            .WireTap("direct://demo-expressions")
            .Log("[CRON]   ✓ Data routes exercised")

            // ── Section 5b: Predicate showcase ──
            .WireTap("direct://demo-predicates-compare")
            .WireTap("direct://demo-predicates-string")
            .WireTap("direct://demo-predicates-null")
            .WireTap("direct://demo-predicates-logic")
            .WireTap("direct://demo-predicates-string-expr")
            .WireTap("direct://demo-predicates-choice")
            .WireTap("direct://demo-predicates-jpath")
            .Log("[CRON]   ✓ Predicate routes exercised")

            // ── Section 6: Transactions ──
            .WireTap("direct://demo-transacted")
            .WireTap("direct://demo-imperative-tx")
            .Log("[CRON]   ✓ Transaction routes exercised")

            // ── Full pipeline — exercises HTTP→RabbitMQ→AMQP→gRPC→SQL→Kafka→File→VM→Redis→MQTT→SEDA ──
            //   curl -X POST http://localhost:5088/api/demo -H "Content-Type: application/json" -H "mode: full" -H "priority: high" -d "{\"message\":\"cron-self-test\"}"
            .SetHeader("mode", "full")
            .SetHeader("priority", "high")
            .WireTap("direct://pipeline")
            .Log("[CRON]   ✓ Full pipeline exercised (RabbitMQ→AMQP→gRPC→WMQ→SQL→Kafka→File→Redis→MQTT→WMQ→SEDA)")

            .Log("[CRON] ✔ Self-test complete — ALL routes exercised, zero dead ends");
    }

    /// <summary>
    /// SEDA — in-memory async queue. Fire-and-forget between routes.
    /// Producer queues → consumer picks up asynchronously.
    /// </summary>
    private void ConfigureSedaProcessing()
    {
        From("direct://demo-seda-send")
            .RouteId("demo-seda-producer")
            .Log("[SEDA] ▶ Sending to async queue...")
            .To("seda://work-queue?size=500")
            .Log("[SEDA] ✓ Queued (fire-and-forget)");

        From("seda://work-queue?concurrentConsumers=3")
            .RouteId("demo-seda-consumer")
            .Log("[SEDA-W] ▶ Processing from queue: ${body}")
            .Delay(TimeSpan.FromMilliseconds(50))
            .Log("[SEDA-W] ◀ Done");
    }

    /// <summary>
    /// Redis Pub/Sub — publish events, subscribe and process.
    /// </summary>
    private void ConfigureRedisRoutes()
    {
        From("direct://demo-redis-pub")
            .RouteId("demo-redis-publisher")
            .Log("[REDIS] ▶ Publishing to channel: ${body}")
            .To(RedisPub)
            .Log("[REDIS] ✓ Published");

        From(RedisSub)
            .RouteId("demo-redis-subscriber")
            .Log("[REDIS-SUB] ▶ Received from channel: ${body}")
            .SetHeader("redis.receivedAt", e => DateTime.UtcNow.ToString("o"))
            .Log("[REDIS-SUB] ◀ Processed");
    }

    /// <summary>
    /// TCP echo — raw TCP server. Echoes everything back UPPERCASED.
    /// </summary>
    private void ConfigureTcpEchoServer()
    {
        From(TcpServer)
            .RouteId("demo-tcp-echo")
            .ConvertBody<string>()
            .Log("[TCP] ▶ Received: ${body}")
            .Process(e => e.In.Body = e.In.Body?.ToString()?.ToUpperInvariant())
            .Log("[TCP] ◀ Echoing: ${body}");
    }

    /// <summary>
    /// WebSocket server — listens for WS connections, echoes messages.
    /// </summary>
    private void ConfigureWebSocketServer()
    {
        From(WsServer)
            .RouteId("demo-websocket")
            .Log("[WS] ▶ WebSocket message: ${body}")
            .SetHeader("ws.echo", e => $"echo:{e.In.Body}")
            .Log("[WS] ◀ Responding: ${header.ws.echo}");

        // WS ping — cron sends a message through the WS server as a client
        From("direct://demo-ws-ping")
            .RouteId("demo-ws-ping")
            .SetBody(e => $"{{\"ping\":\"cron\",\"ts\":\"{DateTime.UtcNow:o}\"}}")
            .Log("[WS-PING] → Sending to WebSocket server...")
            .To("ws://localhost:9091/demo")
            .Log("[WS-PING] ← Response: ${body}");
    }

    /// <summary>
    /// MQTT Pub/Sub — IoT-style telemetry topic.
    /// </summary>
    private void ConfigureMqttRoutes()
    {
        // Fluent DSL: Mqtt.Publish("topic").Server(...).Port(...) builds the URI
        From("direct://demo-mqtt-pub")
            .RouteId("demo-mqtt-publisher")
            .Log("[MQTT] ▶ Publishing telemetry: ${body}")
            .To(Mqtt.Publish("demo/telemetry").Server(Constant("localhost")).Port(11883))
            .Log("[MQTT] ✓ Published to topic");

        // Fluent DSL: Mqtt.Subscribe("topic").Server(...).Port(...)
        From(Mqtt.Subscribe("demo/telemetry").Server(Constant("localhost")).Port(11883))
            .RouteId("demo-mqtt-subscriber")
            .Log("[MQTT-SUB] ▶ Telemetry received: ${body}")
            .SetHeader("mqtt.ts", e => DateTime.UtcNow.ToString("o"))
            .Log("[MQTT-SUB] ◀ Processed");
    }

    /// <summary>
    /// IBM MQ Topic Pub/Sub — enterprise messaging topic.
    /// Fluent DSL: Wmq.Topic(...).Host(...).QueueManager(...)
    /// </summary>
    private void ConfigureWmqRoutes()
    {
        // Publisher — triggered via WireTap from the main pipeline
        From("direct://demo-wmq-pub")
            .RouteId("demo-wmq-publisher")
            .Log("[WMQ] ▶ Publishing to topic: ${body}")
            .To(Wmq.Topic("demo/events")
                .Host("localhost").Port(1414)
                .Channel("DEV.APP.SVRCONN").QueueManager("QM1")
                .User("app").Password("admin"))
            .Log("[WMQ] ✓ Published to topic");

        // Subscriber — fluent DSL builds wmq: URI with destinationType=Topic
        From(Wmq.Topic("demo/events")
            .Host("localhost").Port(1414)
            .Channel("DEV.APP.SVRCONN").QueueManager("QM1")
            .User("app").Password("admin")
            .WaitInterval(5000))
            .RouteId("demo-wmq-subscriber")
            .Log("[WMQ-SUB] ▶ Topic event received: ${body}")
            .SetHeader("wmq.ts", e => DateTime.UtcNow.ToString("o"))
            .Log("[WMQ-SUB] ◀ Processed");
    }

    /// <summary>
    /// Slow-process route — simulates a long-running exchange for Watchdog testing.
    /// Timer fires every 20s, each exchange takes 180s → 9 concurrent exchanges,
    /// always 6+ past the suspected threshold. Suspected after 1 min, Hung after 2 min.
    /// </summary>
    private void ConfigureSlowProcessRoute()
    {
        From("timer://slow-process?period=30000")
            .RouteId("demo-slow-process")
            .Log("[SLOW] ▶ Starting long-running operation (180s delay)...")
            .SetBody(e => new { kind = "slow-task", started = DateTime.UtcNow })
            .Delay(TimeSpan.FromSeconds(180))
            .Log("[SLOW] ◀ Long-running operation completed");
    }


    // ════════════════════════════════════════════════════════════════════════
    //  SECTION 5: DATA & OBSERVABILITY
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Validation — JSON Schema + predicate checks.
    /// Invalid messages get rejected before entering the pipeline.
    /// </summary>
    private void ConfigureValidationRoute()
    {
        From("direct://demo-validation")
            .RouteId("demo-validation")
            .Log("[VALID] ▶ Validating message...")

            // JSON Schema (throws on failure)
            .ValidateJsonSchema(MessageSchema)
            .Log("[VALID] ✓ JSON schema OK")

            // Predicate (custom business rule)
            .Validate(
                e => e.In.Body?.ToString()?.Length < 10000,
                errorMessage: "Body too large (max 10KB)")
            .Log("[VALID] ✓ Size check OK")

            .Log("[VALID] ◀ All validations passed");
    }

    /// <summary>
    /// Marshal / ConvertBody — serialization pipeline.
    /// Object → JSON bytes → string.
    /// </summary>
    private void ConfigureMarshalRoute()
    {
        From("direct://demo-marshal")
            .RouteId("demo-marshal")
            .Log("[MARSHAL] ▶ Starting serialization demo...")

            // Create a typed object
            .SetBody(e => new { name = "demo", value = 42, ts = DateTime.UtcNow })
            .Log("[MARSHAL]   Created anonymous object")

            // Marshal → JSON bytes
            .Marshal(typeof(JsonMessageSerializer))
            .Log("[MARSHAL]   After Marshal (JSON): contentType=${contentType}")

            // ConvertBody → string
            .ConvertBody<string>()
            .Log("[MARSHAL]   After ConvertBody<string>: ${body}")

            .Log("[MARSHAL] ◀ Serialization round-trip complete");
    }

    /// <summary>
    /// Traced + Metered blocks — observability without code changes.
    /// Traced creates spans, Metered records counters/histograms.
    /// </summary>
    private void ConfigureObservabilityRoute()
    {
        From("direct://demo-observability")
            .RouteId("demo-observability")
            .Log("[OBS] ▶ Starting observable pipeline...")

            // Traced block — all steps inside get one span
            .Traced("demo-traced-block")
                .Log("[OBS]   traced: step 1 — validate")
                .Validate(e => e.In.Body != null, "Body required")
                .Log("[OBS]   traced: step 2 — transform")
                .Process(e => e.In.Body = e.In.Body?.ToString()?.ToUpperInvariant())
            .EndTraced()

            // Metered block — records execution time + counter
            .Metered("demo-metered-block")
                .Log("[OBS]   metered: step 1 — enrich")
                .SetHeader("obs.enriched", "true")
                .Log("[OBS]   metered: step 2 — delay 10ms")
                .Delay(TimeSpan.FromMilliseconds(10))
            .EndMetered()

            // Inline traced — single step with action
            .Traced("final-stamp", e => e.In.Headers["obs.traced"] = "done")

            // Inline metered — single step with action
            .Metered("final-count", e => e.In.Headers["obs.metered"] = "done")

            .Log("[OBS] ◀ Observable pipeline complete");
    }

    /// <summary>
    /// Expression showcase — all the ways to read/write exchange data.
    /// JPath, XPath, Expr(), Body(), Header(), Property(), Constant(), Exchange().
    /// </summary>
    private void ConfigureExpressionShowcase()
    {
        From("direct://demo-expressions")
            .RouteId("demo-expressions")

            // ── JSON body for JPath ──
            .SetBody(e => "{\"user\":{\"name\":\"Alice\",\"age\":30},\"items\":[\"a\",\"b\",\"c\"]}")
            .Log("[EXPR] ▶ JSON body: ${body}")

            // JPath — extract from JSON body
            .SetHeader("user.name", JPath("$.user.name"))
            .SetHeader("user.age", JPath<int>("$.user.age"))
            .Log("[EXPR]   JPath: name=${header.user.name}, age=${header.user.age}")

            // Property + Constant expressions
            .SetProperty("origin", Constant("demo-route"))
            .SetProperty("computed", Exchange(e => $"from-{GetHeader(e, "user.name")}"))
            .Log("[EXPR]   Property: origin=${property.origin}, computed=${property.computed}")

            // Expr() — template expression as IExpression
            .SetHeader("greeting", Expr("Hello ${header.user.name}, age ${header.user.age}!"))
            .Log("[EXPR]   Expr: ${header.greeting}")

            // Header() expression used in Filter predicate
            .Filter(Header("user.name").contains("Alice"))
            .Log("[EXPR] ✓ User is Alice (Filter + contains)")

            // Body() expression snapshot
            .SetProperty("bodySnapshot", Body())
            .Log("[EXPR]   Body snapshot saved to property")

            // ── XML body for XPath ──
            .SetBody(e => "<order><item id='1'>Widget</item><item id='2'>Gadget</item></order>")
            .Log("[EXPR]   XML body: ${body}")

            // XPath — extract from XML body
            .SetHeader("firstItem", XPath("/order/item[1]"))
            .Log("[EXPR]   XPath: first item = ${header.firstItem}")

            // RemoveHeader / RemoveProperty cleanup
            .RemoveHeader("tempData")
            .RemoveProperty("bodySnapshot")

            .Log("[EXPR] ◀ Expression showcase complete");
    }

    /// <summary>
    /// Predicate showcase — the entire Expression predicate DSL.
    /// Each predicate is a fluent method on an Expression: Header("x").isEqualTo("y").
    /// Reads like plain English — that's the whole point of the DSL.
    /// </summary>
    private void ConfigurePredicateShowcase()
    {
        // ── Route 1: Comparison predicates ──────────────────────────────
        From("direct://demo-predicates-compare")
            .RouteId("demo-predicates-compare")
            .Log("[PRED] ▶ Comparison predicates showcase")

            // set up test data
            .SetHeader("score", 85)
            .SetHeader("grade", "B+")
            .SetHeader("level", "senior")

            // isEqualTo — exact match
            .Filter(Header("grade").isEqualTo("B+"))
            .Log("[PRED]   ✓ Header('grade').isEqualTo('B+') → passed")

            // isNotEqualTo — not this value
            .Filter(Header("level").isNotEqualTo("junior"))
            .Log("[PRED]   ✓ Header('level').isNotEqualTo('junior') → passed")

            // isGreaterThan — strictly greater
            .Filter(Header("score").isGreaterThan(50))
            .Log("[PRED]   ✓ Header('score').isGreaterThan(50) → passed (score=85)")

            // isLessThan — strictly less
            .Filter(Header("score").isLessThan(100))
            .Log("[PRED]   ✓ Header('score').isLessThan(100) → passed (score=85)")

            // isGreaterThanOrEqualTo — inclusive lower bound
            .Filter(Header("score").isGreaterThanOrEqualTo(85))
            .Log("[PRED]   ✓ Header('score').isGreaterThanOrEqualTo(85) → passed")

            // isLessThanOrEqualTo — inclusive upper bound
            .Filter(Header("score").isLessThanOrEqualTo(100))
            .Log("[PRED]   ✓ Header('score').isLessThanOrEqualTo(100) → passed")

            // isBetween — range check (inclusive)
            .Filter(Header("score").isBetween(70, 90))
            .Log("[PRED]   ✓ Header('score').isBetween(70, 90) → passed (score=85)")

            .Log("[PRED] ◀ Comparison predicates done");


        // ── Route 2: String predicates ──────────────────────────────────
        From("direct://demo-predicates-string")
            .RouteId("demo-predicates-string")
            .Log("[PRED] ▶ String predicates showcase")

            .SetHeader("email", "alice@example.com")
            .SetHeader("filename", "report-2024.pdf")
            .SetHeader("tag", "urgent-task-x42")

            // contains — substring match
            .Filter(Header("email").contains("@example"))
            .Log("[PRED]   ✓ Header('email').contains('@example') → passed")

            // startsWith — prefix check
            .Filter(Header("email").startsWith("alice"))
            .Log("[PRED]   ✓ Header('email').startsWith('alice') → passed")

            // endsWith — suffix check
            .Filter(Header("filename").endsWith(".pdf"))
            .Log("[PRED]   ✓ Header('filename').endsWith('.pdf') → passed")

            // regex — pattern match
            .Filter(Header("tag").regex(@"^urgent-.*-x\d+$"))
            .Log("[PRED]   ✓ Header('tag').regex('^urgent-.*-x\\d+$') → passed")

            // In — set membership
            .Filter(Header("filename").In("report-2024.pdf", "summary.pdf", "data.csv"))
            .Log("[PRED]   ✓ Header('filename').In('report-2024.pdf', 'summary.pdf', 'data.csv') → passed")

            .Log("[PRED] ◀ String predicates done");


        // ── Route 3: Null checks ────────────────────────────────────────
        From("direct://demo-predicates-null")
            .RouteId("demo-predicates-null")
            .Log("[PRED] ▶ Null-check predicates showcase")

            .SetHeader("existing", "value")
            .RemoveHeader("missing")

            // isNotNull — header exists
            .Filter(Header("existing").isNotNull())
            .Log("[PRED]   ✓ Header('existing').isNotNull() → passed")

            // isNull — header absent
            .Filter(Header("missing").isNull())
            .Log("[PRED]   ✓ Header('missing').isNull() → passed")

            .Log("[PRED] ◀ Null-check predicates done");


        // ── Route 4: Logical composition — and / or / not ───────────────
        //
        //  and() / or() / not() are methods on Expression, not IPredicate.
        //  Left side = Expression (evaluates to bool), right side = IPredicate.
        //
        From("direct://demo-predicates-logic")
            .RouteId("demo-predicates-logic")
            .Log("[PRED] ▶ Logical composition predicates showcase")

            .SetHeader("role", "admin")
            .SetHeader("active", true)
            .SetHeader("disabled", false)
            .SetHeader("trust", 9)

            // and — Expression is truthy AND predicate matches
            //   Header("active") → true  AND  Header("role").isEqualTo("admin") → true
            .Filter(Header("active").and(Header("role").isEqualTo("admin")))
            .Log("[PRED]   ✓ Header('active').and(Header('role').isEqualTo('admin')) → passed")

            // or — Expression is truthy OR predicate matches
            //   Header("disabled") → false  OR  Header("role").isEqualTo("admin") → true
            .Filter(Header("disabled").or(Header("role").isEqualTo("admin")))
            .Log("[PRED]   ✓ Header('disabled').or(Header('role').isEqualTo('admin')) → passed")

            // not — negates Expression
            //   Header("disabled") → false → NOT false → true
            .Filter(Header("disabled").not())
            .Log("[PRED]   ✓ Header('disabled').not() → passed (disabled=false)")

            // complex: active AND trust >= 5
            .Filter(Header("active").and(Header("trust").isGreaterThanOrEqualTo(5)))
            .Log("[PRED]   ✓ Header('active').and(Header('trust').isGreaterThanOrEqualTo(5)) → passed")

            .Log("[PRED] ◀ Logical composition done");


        // ── Route 5: String expressions — Filter(string) & When(string) ─
        //
        //  LogicalPredicate compiles "${header.x}" expressions at runtime.
        //  This is the purely declarative way: no lambdas, just strings.
        //
        From("direct://demo-predicates-string-expr")
            .RouteId("demo-predicates-string-expr")
            .Log("[PRED] ▶ String expression predicates (LogicalPredicate)")

            .SetHeader("status", "active")
            .SetHeader("count", "42")

            // Filter(string) — string expression evaluated as boolean
            .Filter("${header.status}")
            .Log("[PRED]   ✓ Filter('$${header.status}') → 'active' is truthy")

            // Choice + When(string) — declarative branching
            .Choice()
                .When("${header.status}")
                    .Log("[PRED]   ✓ When('$${header.status}') → branch taken (status=active)")
                .Otherwise()
                    .Log("[PRED]   ✗ Otherwise → status was falsy")
            .EndChoice()

            .Log("[PRED] ◀ String expression predicates done");


        // ── Route 6: Predicates in a Choice (fluent Expression predicates) ─
        From("direct://demo-predicates-choice")
            .RouteId("demo-predicates-choice")
            .Log("[PRED] ▶ Choice with Expression predicates")

            .SetHeader("amount", 750)

            .Choice()
                // When(IPredicate) — expression-based condition
                .When(Header("amount").isGreaterThanOrEqualTo(1000))
                    .Log("[PRED]   → amount >= 1000 → premium tier")
                .When(Header("amount").isBetween(500, 999))
                    .Log("[PRED]   → amount 500..999 → standard tier")
                .When(Header("amount").isLessThan(500))
                    .Log("[PRED]   → amount < 500 → basic tier")
                .Otherwise()
                    .Log("[PRED]   → fallback tier")
            .EndChoice()

            .Log("[PRED] ◀ Choice with predicates done");


        // ── Route 7: JPath predicates — conditions on JSON body fields ──
        From("direct://demo-predicates-jpath")
            .RouteId("demo-predicates-jpath")
            .Log("[PRED] ▶ JPath predicate showcase")

            .SetBody(e => "{\"order\":{\"total\":299.99,\"currency\":\"USD\",\"priority\":\"express\"}}")
            .Log("[PRED]   JSON body: ${body}")

            // JPath expression + predicate chain
            .Filter(JPath("$.order.currency").isEqualTo("USD"))
            .Log("[PRED]   ✓ JPath('$.order.currency').isEqualTo('USD') → passed")

            .Filter(JPath("$.order.priority").In("express", "overnight"))
            .Log("[PRED]   ✓ JPath('$.order.priority').In('express','overnight') → passed")

            .Filter(JPath("$.order.priority").startsWith("exp"))
            .Log("[PRED]   ✓ JPath('$.order.priority').startsWith('exp') → passed")

            .Log("[PRED] ◀ JPath predicates done");
    }


    // ════════════════════════════════════════════════════════════════════════
    //  SECTION 6: TRANSACTIONS
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Transacted() — wraps the entire route in a TransactionScope.
    /// All SQL operations participate in one distributed transaction.
    /// </summary>
    private void ConfigureTransactedRoute()
    {
        From("direct://demo-transacted")
            .RouteId("demo-transacted")
            .Log("[TX-AUTO] ▶ Entering transacted route...")
            .Transacted()
            .Log("[TX-AUTO]   → SQL INSERT inside auto-transaction...")
            .To(SqlInsert)
            .Log("[TX-AUTO]   → VM audit inside auto-transaction...")
            .To("vm://audit-log")
            .Log("[TX-AUTO] ◀ Route complete → auto-commit");
    }

    /// <summary>
    /// BeginTransaction / CommitTransaction / RollbackTransaction —
    /// imperative control. You decide when to commit or rollback.
    /// DoTry/DoCatch wraps the transaction for safety.
    /// </summary>
    private void ConfigureImperativeTxRoute()
    {
        From("direct://demo-imperative-tx")
            .RouteId("demo-imperative-tx")
            .Log("[TX-IMP] ▶ Starting imperative transaction...")

            .BeginTransaction()
            .Log("[TX-IMP]   → Transaction opened")

            .DoTry()
                .Log("[TX-IMP]   → SQL INSERT #1...")
                .To(SqlInsert)
                .Log("[TX-IMP]   → SQL INSERT #2...")
                .To(SqlInsert)
                .CommitTransaction()
                .Log("[TX-IMP]   ✔ Both inserts committed!")

            .DoCatch<Exception>()
                .Log("[TX-IMP]   ✖ Error: ${exception.message}")
                .RollbackTransaction()
                .Log("[TX-IMP]   ✖ Transaction rolled back")

            .End()

            .Log("[TX-IMP] ◀ Done");
    }


    // ════════════════════════════════════════════════════════════════════════
    //  SECTION 7: POLICIES & LIFECYCLE
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Route with explicit <see cref="DemoRoutePolicy"/> — logs OnInit / OnStart / OnStop
    /// and per-exchange OnExchangeBegin / OnExchangeDone hooks.
    /// </summary>
    private void ConfigurePolicyShowcaseRoute()
    {
        From("timer://policy-demo?period=30000&delay=5000")
            .RouteId("demo-policy-showcase")
            .RoutePolicy(new DemoRoutePolicy(_log))
            .Log("[POLICY-DEMO] ▶ Timer tick — exchange ${exchangeId}")
            .SetBody(e => $"Policy demo at {DateTime.UtcNow:O}")
            .Log("[POLICY-DEMO] ◀ Done: ${body}");
    }

    /// <summary>
    /// Route marked with <c>.Cluster(true)</c> — when running under Tsak with cluster
    /// mode enabled, the <see cref="Abstractions.IRoutePolicyFactory"/> will automatically
    /// assign a <see cref="Abstractions.IRoutePolicy"/> that only lets one node run it.
    /// In standalone Demo mode (no cluster), `.Cluster(true)` is a no-op marker.
    /// </summary>
    private void ConfigureClusterReadyRoute()
    {
        From("timer://cluster-demo?period=60000&delay=10000")
            .RouteId("demo-cluster-ready")
            .Cluster(true)
            .Log("[CLUSTER-DEMO] ▶ Singleton tick (only one node runs this when clustered)")
            .SetBody(e => $"Cluster singleton at {DateTime.UtcNow:O}")
            .Log("[CLUSTER-DEMO] ◀ Done: ${body}");
    }


    // ════════════════════════════════════════════════════════════════════════
    //  SECTION 8: NAMED REDB INSTANCES
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Route: Named IRedbService showcase.
    /// Every 30s reads DB info from "pg-test" (Postgres Pro) and "mssql-test" (MSSql OS).
    /// Named instances are auto-created from "Redb" section in redb.Route.Demo.config.json.
    /// </summary>
    private void ConfigureNamedRedbRoute()
    {
        From("timer://named-redb-check?period=30000&delay=5000")
            .RouteId("demo-named-redb-pgsql")
            .AutoStart(false)
            .Log("[NAMED-REDB] ▶ Checking named IRedbService instances...")

            // ── DB info ──
            .ProcessWithRedb("pg-test", (redb, ex) =>
            {
                ex.In.Headers["pg-db-type"] = redb.dbType ?? "n/a";
                ex.In.Headers["pg-db-version"] = redb.dbVersion ?? "n/a";
                ex.In.Headers["pg-cache-domain"] = redb.CacheDomain ?? "n/a";
            })
            .Log("[NAMED-REDB] pg-test: type=${header.pg-db-type}, ver=${header.pg-db-version}, cache=${header.pg-cache-domain}")

            // ── CRUD: Save ──
            .ProcessWithRedb("pg-test", async (redb, ex, ct) =>
            {
                var item = new RedbObject<DemoItemProps>
                {
                    name = $"Demo-{DateTime.UtcNow:HHmmss}",
                    Props = new DemoItemProps
                    {
                        Title = "Named Redb Demo",
                        Description = $"Created at {DateTime.UtcNow:O}",
                        Priority = Random.Shared.Next(1, 10)
                    }
                };
                var id = await redb.SaveAsync(item);
                ex.In.Headers["crud-saved-id"] = id.ToString();
            })
            .Log("[NAMED-REDB] CRUD Save → id=${header.crud-saved-id}")

            // ── CRUD: Load ──
            .ProcessWithRedb("pg-test", async (redb, ex, ct) =>
            {
                var idStr = ex.In.Headers["crud-saved-id"]?.ToString();
                if (long.TryParse(idStr, out var id))
                {
                    var obj = await redb.LoadAsync<DemoItemProps>(id);
                    ex.In.Headers["crud-loaded"] = obj != null
                        ? $"{obj.name} (priority={obj.Props?.Priority})"
                        : "NOT FOUND";
                }
            })
            .Log("[NAMED-REDB] CRUD Load → ${header.crud-loaded}")

            // ── CRUD: Query ──
            .ProcessWithRedb("pg-test", async (redb, ex, ct) =>
            {
                var items = await redb.Query<DemoItemProps>()
                    .Where(p => p.Priority > 3)
                    .Take(5)
                    .ToListAsync();
                ex.In.Headers["crud-query-count"] = items.Count.ToString();
            })
            .Log("[NAMED-REDB] CRUD Query (Priority>3) → ${header.crud-query-count} items")

            // ── CRUD: Delete ──
            .ProcessWithRedb("pg-test", async (redb, ex, ct) =>
            {
                var idStr = ex.In.Headers["crud-saved-id"]?.ToString();
                if (long.TryParse(idStr, out var id))
                {
                    var deleted = await redb.DeleteAsync(id);
                    ex.In.Headers["crud-deleted"] = deleted.ToString();
                }
            })
            .Log("[NAMED-REDB] CRUD Delete id=${header.crud-saved-id} → ${header.crud-deleted}")

            .Log("[NAMED-REDB] ◀ Done — PG CRUD cycle complete");

        // ── MSSql CRUD (same pattern, different named instance) ──
        From("timer://named-redb-mssql?period=30000&delay=8000")
            .RouteId("demo-named-redb-mssql")
            .AutoStart(false)
            .Log("[NAMED-REDB-MSSQL] ▶ Starting CRUD on mssql-test...")

            .ProcessWithRedb("mssql-test", (redb, ex) =>
            {
                ex.In.Headers["ms-db-type"] = redb.dbType ?? "n/a";
                ex.In.Headers["ms-db-version"] = redb.dbVersion ?? "n/a";
            })
            .Log("[NAMED-REDB-MSSQL] info: type=${header.ms-db-type}, ver=${header.ms-db-version}")

            .ProcessWithRedb("mssql-test", async (redb, ex, ct) =>
            {
                var item = new RedbObject<DemoItemProps>
                {
                    name = $"MsDemo-{DateTime.UtcNow:HHmmss}",
                    Props = new DemoItemProps
                    {
                        Title = "MSSql Named Redb",
                        Description = $"Created at {DateTime.UtcNow:O}",
                        Priority = Random.Shared.Next(1, 10)
                    }
                };
                var id = await redb.SaveAsync(item);
                ex.In.Headers["ms-saved-id"] = id.ToString();
            })
            .Log("[NAMED-REDB-MSSQL] Save → id=${header.ms-saved-id}")

            .ProcessWithRedb("mssql-test", async (redb, ex, ct) =>
            {
                if (long.TryParse(ex.In.Headers["ms-saved-id"]?.ToString(), out var id))
                {
                    var obj = await redb.LoadAsync<DemoItemProps>(id);
                    ex.In.Headers["ms-loaded"] = obj != null
                        ? $"{obj.name} (priority={obj.Props?.Priority})"
                        : "NOT FOUND";
                }
            })
            .Log("[NAMED-REDB-MSSQL] Load → ${header.ms-loaded}")

            .ProcessWithRedb("mssql-test", async (redb, ex, ct) =>
            {
                var items = await redb.Query<DemoItemProps>()
                    .Where(p => p.Priority > 3)
                    .Take(5)
                    .ToListAsync();
                ex.In.Headers["ms-query-count"] = items.Count.ToString();
            })
            .Log("[NAMED-REDB-MSSQL] Query (Priority>3) → ${header.ms-query-count} items")

            .ProcessWithRedb("mssql-test", async (redb, ex, ct) =>
            {
                if (long.TryParse(ex.In.Headers["ms-saved-id"]?.ToString(), out var id))
                {
                    var deleted = await redb.DeleteAsync(id);
                    ex.In.Headers["ms-deleted"] = deleted.ToString();
                }
            })
            .Log("[NAMED-REDB-MSSQL] Delete id=${header.ms-saved-id} → ${header.ms-deleted}")

            .Log("[NAMED-REDB-MSSQL] ◀ Done — MSSql CRUD cycle complete");
    }


    // ════════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ════════════════════════════════════════════════════════════════════════

    // ════════════════════════════════════════════════════════════════════════
    //  SECTION 9: SCOPE DIAGNOSTICS — parallel splitter connection tracking
    // ════════════════════════════════════════════════════════════════════════

    private static int _scopeDiagConcurrent;
    private static int _scopeDiagPeakConcurrent;
    private static int _scopeDiagTotalScopes;

    private void ConfigureScopeDiagRoute()
    {
        // Timer fires once after 10s, then every 60s (change period as needed)
        From("timer://scope-diag?period=60000&delay=10000")
            .RouteId("demo-scope-diag")
            .AutoStart(false)
            .Log("[SCOPE-DIAG] ▶ Starting parallel split: 50 items, maxDop=3")

            // Reset counters
            .Process(e =>
            {
                Interlocked.Exchange(ref _scopeDiagConcurrent, 0);
                Interlocked.Exchange(ref _scopeDiagPeakConcurrent, 0);
                Interlocked.Exchange(ref _scopeDiagTotalScopes, 0);
                e.In.Body = Enumerable.Range(1, 50).Cast<object>().ToList();
                e.In.Headers["diag-start-ms"] = Environment.TickCount64;
            })

            // Parallel split: 500 items, maxDop=3 — classic Camel fluent chain
            .Split(Body())
                .ParallelProcessing()
                .MaxDegreeOfParallelism(5)
                .ProcessWithRedb(async (redb, ex, ct) =>
                {
                    var idx = ex.In.Body;
                    var concurrent = Interlocked.Increment(ref _scopeDiagConcurrent);
                    var total = Interlocked.Increment(ref _scopeDiagTotalScopes);

                    // Track peak
                    int peak;
                    do
                    {
                        peak = Volatile.Read(ref _scopeDiagPeakConcurrent);
                    } while (concurrent > peak &&
                             Interlocked.CompareExchange(ref _scopeDiagPeakConcurrent, concurrent, peak) != peak);

                    var scopeHash = ex.ServiceProvider?.GetHashCode().ToString("X8") ?? "NO-SCOPE";

                    _log?.LogInformation(
                        "[SCOPE-DIAG] item={Item}, concurrent={Concurrent}, total={Total}, scopeHash={ScopeHash}",
                        idx, concurrent, total, scopeHash);

                    try
                    {
                        // Real EAV CRUD: Save → Load → Delete
                        var item = new RedbObject<DemoItemProps>
                        {
                            name = $"ScopeDiag-{idx}-{DateTime.UtcNow:HHmmssfff}",
                            Props = new DemoItemProps
                            {
                                Title = $"Scope Diag Item {idx}",
                                Description = $"Parallel split test item {idx}",
                                Priority = (int)idx!
                            }
                        };
                        var savedId = await redb.SaveAsync(item);

                        var loaded = await redb.LoadAsync<DemoItemProps>(savedId);
                        var loadedName = loaded?.name ?? "NOT FOUND";

                        await redb.DeleteAsync(savedId);

                        // Raw SQL: actual TCP connections to this DB right now
                        var pgConns = await redb.Context.ExecuteScalarAsync<int>(
                            "SELECT count(*)::int FROM pg_stat_activity WHERE datname = current_database()");

                        _log?.LogInformation(
                            "[SCOPE-DIAG] item={Item}, savedId={SavedId}, loaded={Loaded}, deleted=true, pg_connections={PgConns}",
                            idx, savedId, loadedName, pgConns);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _scopeDiagConcurrent);
                    }
                })
            .End()

            // Summary — final pg_stat_activity snapshot via raw SQL
            .ProcessWithRedb(async (redb, e, ct) =>
            {
                var elapsed = Environment.TickCount64 - (long)(e.In.Headers["diag-start-ms"] ?? 0L);
                var peak = Volatile.Read(ref _scopeDiagPeakConcurrent);
                var total = Volatile.Read(ref _scopeDiagTotalScopes);
                var pgConns = await redb.Context.ExecuteScalarAsync<int>(
                    "SELECT count(*)::int FROM pg_stat_activity WHERE datname = current_database()");
                _log?.LogWarning(
                    "[SCOPE-DIAG] ◀ DONE: peak_concurrent={Peak}, total_scopes={Total}, elapsed={Elapsed}ms, pg_connections_after={PgConns}",
                    peak, total, elapsed, pgConns);
            });
    }

    /// <summary>Читает значение заголовка из exchange (null если нет).</summary>
    private static string? GetHeader(IExchange e, string key)
        => e.In.Headers.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string BuildResponse(IExchange e)
    {
        return JsonSerializer.Serialize(new
        {
            success = true,
            traceId = GetHeader(e, "traceId"),
            mode = GetHeader(e, "mode"),
            stamps = new
            {
                rabbit = GetHeader(e, "stamp.rabbit"),
                amqp = GetHeader(e, "stamp.amqp"),
                grpc = GetHeader(e, "stamp.grpc"),
                wmq = GetHeader(e, "stamp.wmq"),
                vm = GetHeader(e, "stamp.vm"),
            },
            pipeline = "HTTP → Direct → RabbitMQ → AMQP → gRPC → WMQ → DirectVM → "
                     + "SQL(tx) → Kafka(tap) → File(tap) → VM(tap) → WMQ(tap)",
            startedAt = GetHeader(e, "startedAt"),
        }, JsonOpts);
    }
}

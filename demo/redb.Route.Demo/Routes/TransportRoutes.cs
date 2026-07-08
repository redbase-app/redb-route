using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.IbmMq;
using redb.Route.MqttNet;
using redb.Route.Processors;
using redb.Route.Serialization;
using static redb.Route.Demo.Routes.DemoEndpoints;

namespace redb.Route.Demo.Routes;

/// <summary>
/// Transport showcase: Timer, Quartz cron, SEDA, Redis Pub/Sub, raw TCP, WebSocket,
/// MQTT (fluent DSL), IBM MQ topic pub/sub (fluent DSL) and a slow-process simulator.
/// </summary>
internal sealed class TransportRoutes : RouteBuilder
{
    private readonly ILogger? _log;
    public TransportRoutes(ILogger? log) => _log = log;

    protected override void Configure()
    {
        ConfigureTimerHeartbeat();
        ConfigureCronJob();
        ConfigureSedaProcessing();
        ConfigureRedisRoutes();
        ConfigureTcpEchoServer();
        ConfigureWebSocketServer();
        ConfigureMqttRoutes();
        ConfigureWmqRoutes();
        ConfigureSlowProcessRoute();
    }

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
    /// Quartz cron — fires every 30 seconds.
    /// Acts as the self-test driver: exercises all showcase routes so every route
    /// appears in the logs during normal operation.
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
            .To(WsClient)
            .Log("[WS-PING] ← Response: ${body}");
    }

    /// <summary>
    /// MQTT Pub/Sub — IoT-style telemetry topic.
    /// Uses the fluent <c>Mqtt.Publish(...)</c>/<c>Mqtt.Subscribe(...)</c> URI builder.
    /// </summary>
    private void ConfigureMqttRoutes()
    {
        From("direct://demo-mqtt-pub")
            .RouteId("demo-mqtt-publisher")
            .Log("[MQTT] ▶ Publishing telemetry: ${body}")
            .To(Mqtt.Publish(MqttTopic).Server(Constant(MqttHost)).Port(MqttPort))
            .Log("[MQTT] ✓ Published to topic");

        From(Mqtt.Subscribe(MqttTopic).Server(Constant(MqttHost)).Port(MqttPort))
            .RouteId("demo-mqtt-subscriber")
            .Log("[MQTT-SUB] ▶ Telemetry received: ${body}")
            .SetHeader("mqtt.ts", e => DateTime.UtcNow.ToString("o"))
            .Log("[MQTT-SUB] ◀ Processed");
    }

    /// <summary>
    /// IBM MQ Topic Pub/Sub — enterprise messaging topic.
    /// Uses the fluent <c>Wmq.Topic(...)</c> URI builder.
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

        // Subscriber
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
    /// Timer fires every 30s, each exchange takes 180s → ~6 concurrent exchanges,
    /// always past the suspected threshold. Suspected after 1 min, Hung after 2 min.
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
}

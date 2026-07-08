using redb.Route.Processors;

namespace redb.Route.Demo.Routes;

/// <summary>
/// Central registry of every endpoint URI and shared resource used by the demo.
/// Keeping all of this in one file makes it trivial to retune the demo for
/// another environment without touching route code.
/// </summary>
internal static class DemoEndpoints
{
    // ── RabbitMQ ────────────────────────────────────────────────────────────
    public const string RabbitConsumer =
        "rabbitmq://demo-rpc-queue?host=localhost&port=5672&username=admin&password=admin&declare=true";
    // RPC timeout=15s for demo; tune per environment for production.
    public const string RabbitProducer =
        "rabbitmq://demo-rpc-queue?host=localhost&port=5672&username=admin&password=admin&replyTo=true&timeout=15";

    // ── AMQP 1.0 / Artemis ──────────────────────────────────────────────────
    public const string AmqpConsumer =
        "amqp://demo-amqp-queue?host=localhost&port=5673&user=admin&password=admin";
    public const string AmqpProducer =
        "amqp://demo-amqp-queue?host=localhost&port=5673&user=admin&password=admin&replyTo=true&timeout=15";

    // ── IBM MQ (RPC queue + topic pub/sub) ──────────────────────────────────
    // waitInterval=200 — короткий цикл MQGET, чтобы worker не дремал лишние ~1.5 с
    // после простоя очереди (по умолчанию WaitInterval=5000 мс).
    public const string WmqRpcConsumer =
        "wmq:DEV.QUEUE.3?host=localhost&port=1414&channel=DEV.APP.SVRCONN&queueManager=QM1&user=app&password=admin&waitInterval=100";
    public const string WmqRpcProducer =
        "wmq:DEV.QUEUE.3?host=localhost&port=1414&channel=DEV.APP.SVRCONN&queueManager=QM1&user=app&password=admin&replyTo=true&timeout=15";

    // ── gRPC ────────────────────────────────────────────────────────────────
    public const string GrpcConsumer = "grpc:0.0.0.0:50051";
    public const string GrpcProducer = "grpc:127.0.0.1:50051?plaintext=true";

    // ── Kafka (fire-and-forget via WireTap) ─────────────────────────────────
    public const string KafkaWireTap = "kafka://demo-audit?brokers=localhost:29092";

    // ── File (snapshot via WireTap) ─────────────────────────────────────────
    public const string FileWireTap =
        "file:///C:/Work/cp_tmp/redb.Route.Demo/output?fileName=${header.traceId}.json&autoCreate=true";

    // ── SQL ─────────────────────────────────────────────────────────────────
    public const string SqlInsert =
        "sql:INSERT INTO demo_log(exchange_id, message, status) VALUES(@exchange_id, @message, @status)"
        + "?dataSource=#pg-demo"
        + "&param.exchange_id=${header.traceId}"
        + "&param.message=${body}"
        + "&param.status=${header.mode}";

    public const string SqlSelect =
        "sql:SELECT id, exchange_id, message, status, created_at FROM demo_log ORDER BY id DESC LIMIT 5"
        + "?dataSource=#pg-demo&outputType=SelectList";

    // ── Redis ───────────────────────────────────────────────────────────────
    public const string RedisPub = "redis:PUBLISH:demo-events?host=localhost";
    public const string RedisSub = "redis:SUBSCRIBE:demo-events?host=localhost";

    // ── TCP ─────────────────────────────────────────────────────────────────
    public const string TcpServer = "tcp://0.0.0.0:9099";
    public const string TcpProducer = "tcp://localhost:9099?reconnect=true";

    // ── WebSocket ───────────────────────────────────────────────────────────
    public const string WsServer = "ws://0.0.0.0:9091/demo";
    public const string WsClient = "ws://localhost:9091/demo";

    // ── MQTT ────────────────────────────────────────────────────────────────
    public const string MqttHost = "localhost";
    public const int MqttPort = 11883;
    public const string MqttTopic = "demo/telemetry";

    // ── SEDA dead-letter queue ──────────────────────────────────────────────
    public const string DeadLetterQueue = "seda://dead-letters?size=1000";

    // ── JSON Schema for input validation ────────────────────────────────────
    public const string MessageSchema = """
        {
            "type": "object",
            "required": ["message"],
            "properties": {
                "message": { "type": "string", "minLength": 1 }
            }
        }
        """;

    // ── Shared idempotent repository (dedup) ────────────────────────────────
    public static readonly InMemoryIdempotentRepository IdempotentRepo = new();
}

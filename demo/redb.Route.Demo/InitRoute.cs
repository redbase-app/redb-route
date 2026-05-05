using System.Data.Common;
using Npgsql;
using redb.Route.Abstractions;
using redb.Route.Amqp;
using redb.Route.Core;
using redb.Route.File;
using redb.Route.Grpc;
using redb.Route.Http;
using redb.Route.IbmMq;
using redb.Route.Kafka;
using redb.Route.Mail;
using redb.Route.MqttNet;
using redb.Route.Quartz;
using redb.Route.RabbitMQ;
using redb.Route.Redis;
using redb.Route.Sftp;
using redb.Route.Sql;
using redb.Route.Sql.Connection;
using redb.Route.Tcp;
using redb.Route.WebSocket;
using Microsoft.Extensions.Logging;

namespace redb.Route.Demo;

/// <summary>
/// Tsak module entry point. Discovered automatically via namespace + class name "InitRoute".
/// Full DSL showcase: 18 transports × 50+ EIP patterns × observability × transactions.
/// </summary>
public static class InitRoute
{
    private const string PgConn =
        "Host=localhost;Port=5432;Username=postgres;Password=1;Database=test_redb_route_sql";

    public static IRouteContext main(IRouteContext context)
    {
        // ── Log module config loaded via Tsak 5-layer pipeline ──
        LogContextConfig(context);

        // ── Register transport components ──
        var httpComponent = new HttpComponent
        {
            ServerManager = new SharedHttpServerManager()
        };
        context.AddComponent(httpComponent);
        context.AddComponent(new RabbitMQComponent());
        context.AddComponent(new AmqpComponent());
        context.AddComponent(new GrpcComponent());
        context.AddComponent(new IbmMqComponent());
        context.AddComponent(new KafkaComponent());
        context.AddComponent(new SqlComponent());
        context.AddComponent(new FileComponent());
        context.AddComponent(new RedisComponent());
        context.AddComponent(new TcpComponent());
        context.AddComponent(new WsComponent());
        context.AddComponent(new MqttComponent());
        context.AddComponent(new QuartzTimerComponent());
        context.AddComponent(new CronComponent());
        context.AddComponent(new SmtpComponent());
        context.AddComponent(new SftpComponent());

        // ── Register Npgsql ADO.NET provider for SQL component ──
        DbProviderFactories.RegisterFactory("Npgsql", NpgsqlFactory.Instance);

        // ── Register named data source in context registry ──
        context.AddToRegistry("pg-demo", (ISqlConnectionFactory)new SqlConnectionFactory(
            new SqlConnectionOptions
            {
                ConnectionString = PgConn,
                ProviderName = "Npgsql"
            }));

        // ── Register lifecycle listener (logs context & route events) ──
        var logger = context.GetService<ILogger>();
        context.AddLifecycleListener(new DemoLifecycleListener(logger));

        // ── Create demo_log table (idempotent) ──
        CreateTable();

        // ── Register all demo routes ──
        ((RouteContext)context).AddRoutes(new DemoRouteBuilder(logger));

        return context;
    }

    private static void CreateTable()
    {
        using var conn = new NpgsqlConnection(PgConn);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS demo_log (
                id            SERIAL PRIMARY KEY,
                exchange_id   TEXT NOT NULL,
                message       TEXT,
                status        TEXT,
                created_at    TIMESTAMP DEFAULT NOW()
            )
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads and logs context properties injected by Tsak config pipeline
    /// (from redb.Route.Demo.config.json → Layer 4).
    /// </summary>
    private static void LogContextConfig(IRouteContext context)
    {
        var logger = context.GetService<ILogger>();
        if (logger == null) return;

        logger.LogInformation("═══ redb.Route.Demo — Module Configuration ═══");

        // Nested object: DemoSettings
        var demoSettings = context.GetProperty<IDictionary<string, object?>>("DemoSettings");
        if (demoSettings != null)
        {
            logger.LogInformation("[CONFIG] DemoSettings:");
            foreach (var (key, value) in demoSettings)
                logger.LogInformation("  {Key} = {Value}", key, value);
        }
        else
        {
            logger.LogWarning("[CONFIG] DemoSettings: NOT FOUND (config file not loaded?)");
        }

        // Nested object: RabbitMQ
        var rabbitConfig = context.GetProperty<IDictionary<string, object?>>("RabbitMQ");
        if (rabbitConfig != null)
        {
            logger.LogInformation("[CONFIG] RabbitMQ:");
            foreach (var (key, value) in rabbitConfig)
                logger.LogInformation("  {Key} = {Value}", key, value);
        }

        // Nested object: Redis
        var redisConfig = context.GetProperty<IDictionary<string, object?>>("Redis");
        if (redisConfig != null)
        {
            logger.LogInformation("[CONFIG] Redis:");
            foreach (var (key, value) in redisConfig)
                logger.LogInformation("  {Key} = {Value}", key, value);
        }

        // Nested object: FeatureFlags
        var features = context.GetProperty<IDictionary<string, object?>>("FeatureFlags");
        if (features != null)
        {
            logger.LogInformation("[CONFIG] FeatureFlags:");
            foreach (var (key, value) in features)
                logger.LogInformation("  {Key} = {Value}", key, value);
        }

        // Simple properties (from appsettings layers)
        var autoStart = context.GetProperty<string>("AutoStart");
        if (autoStart != null)
            logger.LogInformation("[CONFIG] AutoStart = {AutoStart}", autoStart);

        logger.LogInformation("═══ End of Module Configuration ═══");
    }
}

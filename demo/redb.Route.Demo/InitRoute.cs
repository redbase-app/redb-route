using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using redb.Route.Abstractions;
using redb.Route.Amqp;
using redb.Route.Core;
using redb.Route.Exec;
using redb.Route.File;
using redb.Route.Grpc;
using redb.Route.Http;
using redb.Route.IbmMq;
using redb.Route.Kafka;
using redb.Route.Llm;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Storage.Redb;
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
using redb.Route.Demo.Routes;

namespace redb.Route.Demo;

/// <summary>
/// Tsak module entry point. Discovered automatically via namespace + class name "InitRoute".
/// Full DSL showcase: 18 transports × 50+ EIP patterns × observability × transactions.
/// </summary>
public static class InitRoute
{
    private const string PgConn =
        "Host=localhost;Port=5432;Username=postgres;Password=1;Database=redb";

    public static IRouteContext main(IRouteContext context)
    {
        var logger = context.GetService<ILogger>();

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
        context.AddComponent(new LlmComponent());
        context.AddComponent(new ExecComponent());

        // ── LLM wiring (stub provider — no API key required) ──
        // Factory in registry → resolved by `llm://demo-stub` and `.Llm("demo-stub")`.
        context.AddToRegistry("demo-stub", new LlmConnectionFactory
        {
            Name = "demo-stub",
            Provider = "stub",
            ModelId = "stub-model",
            Temperature = 0.0
        });

        // ── Live Claude factory for the HTTP LLM showcase ──
        // Skipped silently when REDB_LLM_ANT_API03_KEY is not set so the rest
        // of the demo module still loads cleanly in environments without keys.
        var anthropicKey = Environment.GetEnvironmentVariable("REDB_LLM_ANT_API03_KEY");
        if (!string.IsNullOrWhiteSpace(anthropicKey))
        {
            context.AddToRegistry("haiku", new LlmConnectionFactory
            {
                Name = "haiku",
                Provider = "anthropic",
                ModelId = "claude-haiku-4-5",
                ApiKey = anthropicKey,
                Temperature = 0.0,
                MaxTokens = 512
            });
            logger?.LogInformation("[LLM] 'haiku' factory wired (anthropic / claude-haiku-4-5)");
        }
        else
        {
            logger?.LogWarning("[LLM] REDB_LLM_ANT_API03_KEY not set — /api/llm/* endpoints will 500. " +
                               "Set it in redb.Tsak/publish/keys/.env.local or as an env var.");
        }
        // Tool descriptor registry — populated by `.AsLlmTool(...)` at route build time.
        context.AddService(typeof(IToolDescriptorRegistry), new ToolDescriptorRegistry());
        // Producer template + agent engine — needed for tool-using LLM routes.
        // The template is started by ProducerTemplateStarter once the context is up.
        var producerTemplate = new ProducerTemplate(context);
        context.AddService(typeof(IProducerTemplate), producerTemplate);

        // Conversation store — Tsak registers an IServiceScopeFactory per named redb
        // instance under "redb-factory:{name}" (see NamedRedbRoutes for usage).
        // RedbConversationStore now takes IRouteContext directly: per-call it asks
        // context.GetRedbService("pg-test", exchange) which:
        //   1) reuses the per-exchange scope cached in exchange.Properties on repeat calls,
        //   2) creates a fresh scope from "redb-factory:pg-test" and caches it on first call,
        //   3) falls back to a singleton via "redb:pg-test" when no exchange is in scope.
        // Result: no manual scope-factory plumbing here, scope is auto-disposed with the exchange.
        // The string "pg-test" must match the named instance configured in
        // redb.Route.Demo.config.json under the Redb section.
        context.AddService(typeof(IAgentEngine), new AgentEngine(
            logger: null,
            producerTemplate: producerTemplate,
            observer: null,
            budget: null,
            approval: null,
            redaction: null,
            shadow: null,
            conversation: new RedbConversationStore(context, defaultRedbName: "pg-test"),
            //conversation: new InMemoryConversationStore(),
            idempotency: null,
            approvalStore: null));
        context.AddLifecycleListener(new ProducerTemplateStarter(producerTemplate, logger));

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
        context.AddLifecycleListener(new DemoLifecycleListener(logger));

        // ── Create demo_log table (idempotent) ──
        CreateTable();

        // ── Register all demo routes ──
        ((RouteContext)context)
            .AddRoutes(new MainPipelineRoutes(logger))
            .AddRoutes(new ErrorHandlingRoutes(logger))
            .AddRoutes(new EipRoutes(logger))
            .AddRoutes(new TransportRoutes(logger))
            .AddRoutes(new DataObservabilityRoutes(logger))
            .AddRoutes(new TransactionRoutes(logger))
            .AddRoutes(new LifecycleRoutes(logger))
            .AddRoutes(new NamedRedbRoutes(logger))
            .AddRoutes(new ScopeDiagRoutes(logger))
            .AddRoutes(new DeepDslShowcaseRoutes(logger))
            .AddRoutes(new LlmDemoRoutes(logger))
            .AddRoutes(new LlmHttpRoutes(logger))
            .AddRoutes(new EchoRoutes(logger));

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

using System.Collections.Concurrent;
using System.Collections.Specialized;
using Microsoft.Extensions.Logging;
using Npgsql;
using Quartz;
using Quartz.Impl;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Quartz;

namespace redb.Route.Tests.Quartz;

/// <summary>
/// A database the job store shares — between the nodes of a cluster, or with the node's next start — is the
/// schedule's home, not the node's: a node never deletes or pauses what it holds as a side effect. Stopping a route
/// stops this node's consumer only; a fire with no consumer on the node that took it is skipped and logged, not
/// "cleaned up" out of the database. Runs on the <c>pgdb</c> container of <c>docker-compose.tests.yml</c>: the standard
/// Quartz tables in a schema of their own, a clustered <c>AdoJobStore</c> as redb.Tsak configures it.
/// </summary>
public sealed class QuartzPersistentStoreTests : IClassFixture<PostgresQuartzStore>
{
    private readonly PostgresQuartzStore _store;

    public QuartzPersistentStoreTests(PostgresQuartzStore store) => _store = store;

    [Fact]
    public async Task StoppingTheRoute_KeepsTheJobInTheDatabase()
    {
        var scheduler = await _store.NewScheduler();
        try
        {
            await using var ctx = new RouteContext();
            ctx.AddService(typeof(IScheduler), scheduler);
            ctx.AddComponent(new QuartzTimerComponent());
            ctx.AddRoutes(r => r.From("qtimer://pg-stop?period=60000").RouteId("pg-stop").Process(_ => { }));
            await ctx.Start();
            (await scheduler.CheckExists(new JobKey("pg-stop", "redb"))).Should().BeTrue();

            await ctx.StopRoute("pg-stop");

            (await scheduler.CheckExists(new JobKey("pg-stop", "redb"))).Should().BeTrue(
                "this node stopped; the schedule belongs to the database (deleteJob=true by default)");
            (await scheduler.CheckExists(new TriggerKey("pg-stop", "redb"))).Should().BeTrue();
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    public async Task StoppingTheRoute_WithPauseJob_DoesNotPauseTheSharedTrigger()
    {
        var scheduler = await _store.NewScheduler();
        try
        {
            await using var ctx = new RouteContext();
            ctx.AddService(typeof(IScheduler), scheduler);
            ctx.AddComponent(new QuartzTimerComponent());
            ctx.AddRoutes(r => r.From("qtimer://pg-pause?period=60000&pauseJob=true&deleteJob=false").RouteId("pg-pause")
                .Process(_ => { }));
            await ctx.Start();

            await ctx.StopRoute("pg-pause");

            (await scheduler.GetTriggerState(new TriggerKey("pg-pause", "redb"))).Should().NotBe(TriggerState.Paused,
                "pausing the shared trigger would stop the schedule on every node");
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    public async Task AFireWithoutItsConsumerOnThisNode_IsSkipped_NotDeleted()
    {
        var scheduler = await _store.NewScheduler();
        try
        {
            var fires = 0;
            await using var ctx = new RouteContext();
            ctx.AddService(typeof(IScheduler), scheduler);
            ctx.AddComponent(new QuartzTimerComponent());
            ctx.AddRoutes(r => r.From("qtimer://pg-orphan?period=200&deleteJob=false").RouteId("pg-orphan")
                .Process(_ => Interlocked.Increment(ref fires)));
            await ctx.Start();
            await WaitFor(() => Volatile.Read(ref fires) >= 1);

            // The route stops on this node: its consumer leaves; the trigger keeps firing on the scheduler.
            await ctx.StopRoute("pg-orphan");
            await Task.Delay(1000);

            (await scheduler.CheckExists(new JobKey("pg-orphan", "redb"))).Should().BeTrue(
                "a node without the consumer — a rolling upgrade, a route stopped on one node — must not delete the shared job");
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    public async Task ALeaderPolicyOnAQuartzRoute_OverAClusteredStore_IsAWarning()
    {
        var warnings = await WarningsOfAClusterRoute("pg-leader", new LeaderPolicyFactory());

        warnings.Should().Contain(m => m.Contains("Route 'pg-leader'") && m.Contains("Drop .Cluster(true)"),
            "Quartz already fires the job on one node; on the leader only, the fires other nodes take are lost");
    }

    [Fact]
    public async Task AClusterRouteTheFactoryDeclined_IsNoWarning()
    {
        // Tsak's policy factory leaves Quartz routes on every node: the clustered store gives one fire per cluster.
        var warnings = await WarningsOfAClusterRoute("pg-declined", new DecliningPolicyFactory());

        warnings.Should().NotContain(m => m.Contains("Drop .Cluster(true)"));
    }

    private async Task<string[]> WarningsOfAClusterRoute(string name, IRoutePolicyFactory factory)
    {
        var scheduler = await _store.NewScheduler();
        var log = new WarningLog();
        try
        {
            using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(log));
            await using var ctx = new RouteContext(loggerFactory: loggerFactory);
            ctx.AddService(typeof(IScheduler), scheduler);
            ctx.AddRoutePolicyFactory(factory);
            ctx.AddComponent(new QuartzTimerComponent());
            ctx.AddRoutes(r => r.From($"qtimer://{name}?period=60000").RouteId(name).Cluster(true).Process(_ => { }));
            await ctx.Start();
            return log.Warnings.ToArray();
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
        }
    }

    private sealed class LeaderPolicyFactory : IRoutePolicyFactory
    {
        public IRoutePolicy? CreateRoutePolicy(IRouteContext context, string routeId, RouteDefinition definition)
            => definition.GetCluster() ? new LeaderPolicy() : null;
    }

    private sealed class DecliningPolicyFactory : IRoutePolicyFactory
    {
        public IRoutePolicy? CreateRoutePolicy(IRouteContext context, string routeId, RouteDefinition definition) => null;
    }

    private sealed class LeaderPolicy : IRoutePolicy { }

    private sealed class WarningLog : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                Warnings.Enqueue(formatter(state, exception));
        }

        public void Dispose() { }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }
}

/// <summary>
/// The standard Quartz tables in a schema of their own on the <c>pgdb</c> container, dropped afterwards; a scheduler
/// factory configured as redb.Tsak configures a clustered <c>AdoJobStore</c>.
/// </summary>
public sealed class PostgresQuartzStore : IAsyncLifetime
{
    private const string Server = "Host=localhost;Port=5432;Username=postgres;Password=1;Database=postgres";
    private readonly string _schema = "qrtz_" + Guid.NewGuid().ToString("N")[..12];

    private string ConnectionString => $"{Server};Search Path={_schema}";

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(Server);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {_schema}", connection))
            await create.ExecuteNonQueryAsync();

        await using var script = typeof(PostgresQuartzStore).Assembly.GetManifestResourceStream("QuartzSchema.Postgres")!;
        using var reader = new StreamReader(script);
        await using var apply = new NpgsqlCommand($"SET search_path TO {_schema};\n" + await reader.ReadToEndAsync(), connection);
        await apply.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(Server);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE", connection);
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>A clustered scheduler on the store, not started; each one is a cluster of its own (its own name).</summary>
    public Task<IScheduler> NewScheduler()
        => new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = "redb-route-pg-" + Guid.NewGuid().ToString("N"),
            ["quartz.scheduler.instanceId"] = "AUTO",
            ["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.JobStoreTX, Quartz",
            ["quartz.jobStore.driverDelegateType"] = "Quartz.Impl.AdoJobStore.PostgreSQLDelegate, Quartz",
            ["quartz.jobStore.dataSource"] = "default",
            ["quartz.jobStore.tablePrefix"] = "QRTZ_",
            ["quartz.jobStore.clustered"] = "true",
            ["quartz.dataSource.default.provider"] = "Npgsql",
            ["quartz.dataSource.default.connectionString"] = ConnectionString,
            ["quartz.serializer.type"] = "newtonsoft",
            ["quartz.threadPool.maxConcurrency"] = "4",
        }).GetScheduler();
}

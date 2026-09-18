using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Core;
using redb.Core.Models.Configuration;
using redb.Core.Pro.Extensions;
using redb.MSSql.Pro.Extensions;
using redb.Postgres.Pro.Extensions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.SQLite.Pro.Extensions;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>
/// Provider-substituted Pro fixture for the redb.Route.Llm storage integration tests: one
/// fixture, one set of test classes, the provider slid underneath by REDB_PROVIDER
/// (<c>sqlite</c> — default, since it needs no stand and is where the dialect's own planner guards
/// matter — or <c>postgres</c> / <c>mssql</c>), following the redb.Tests.Integration matrix convention.
/// Uses the Pro free tier (1024 queries, no JWT license required).
/// <para>
/// Cleanup is <b>scoped to the LLM schemes</b> only — never <c>DELETE FROM _objects</c>,
/// which would nuke unrelated data shared with other test runs against the same DB.
/// We enumerate rows of each LLM <c>*Props</c> scheme through the redb query API
/// and remove them via the bulk <c>DeleteAsync(ids)</c> primitive (which cascades
/// to <c>_values</c> and <c>_tree</c>).
/// </para>
/// </summary>
public sealed class StorageProFixture : IAsyncLifetime
{
    public IRedbService Redb { get; private set; } = null!;
    public ServiceProvider ServiceProvider { get; private set; } = null!;
    public IServiceScopeFactory ScopeFactory => ServiceProvider.GetRequiredService<IServiceScopeFactory>();

    /// <summary>
    /// Real <see cref="RouteContext"/> with the fixture's <see cref="IRedbService"/>
    /// registered as the default unnamed instance. Stores resolve it via
    /// <c>context.GetRedbService(name, exchange)</c> — when <c>name</c> is empty
    /// (the test path), the extension falls back to <c>context.GetService&lt;IRedbService&gt;()</c>
    /// which returns the fixture's redb. No per-exchange scoping is needed for
    /// these unit tests — they call store methods with <c>exchange: null</c>.
    /// </summary>
    public IRouteContext RouteContext { get; private set; } = null!;

    /// <summary>Which backend this run rides on: <c>sqlite</c> (default), <c>postgres</c> or <c>mssql</c>.</summary>
    public string Provider { get; } =
        Environment.GetEnvironmentVariable("REDB_PROVIDER")?.ToLowerInvariant() is { Length: > 0 } p
            ? p
            : "sqlite";

    private static string RequireConnectionString(IConfiguration config, string name)
        => config.GetConnectionString(name)
           ?? throw new InvalidOperationException($"ConnectionStrings:{name} missing in appsettings.json");

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddRedbPro(options =>
        {
            // The provider is slid underneath, not copy-pasted around: the same test classes run
            // against whichever backend the environment names.
            var builder = Provider switch
            {
                "mssql" => options.UseMsSql(RequireConnectionString(config, "MsSql")),
                "postgres" => options.UsePostgres(RequireConnectionString(config, "Postgres")),
                // SQLite needs no stand: the file lives next to the test assembly. Full provider parity
                // with the other two, so the same claims are checked on it rather than assumed.
                "sqlite" => options.UseSqlite(RequireConnectionString(config, "Sqlite")),
                _ => throw new InvalidOperationException(
                    $"REDB_PROVIDER '{Provider}' is not one of: postgres, mssql, sqlite.")
            };
            builder.Configure(c =>
            {
                c.PropsSaveStrategy = PropsSaveStrategy.ChangeTracking;
                c.SkipHashValidationOnCacheCheck = false;
                c.EnablePropsCache = false;
                // PVT prefilter: a cutting step that narrows the value-side scan before the aggregation.
                // Measured on 97k objects at 3x with fewer buffers (docs/PVT_PREFILTER_PLAN.md §13,
                // variant D). The plan says it landed in all three Pro providers, so it is on here for all
                // three — SQLite included, whose dialect guards the planner itself (SqlitePrefilterGuards).
                c.EnablePvtPrefilter = true;
            });
            // Free tier: 1024 queries — no .WithLicense() needed.
        });

        ServiceProvider = services.BuildServiceProvider();
        Redb = ServiceProvider.GetRequiredService<IRedbService>();

        RouteContext = new RouteContext();
        RouteContext.SetServiceProvider(ServiceProvider);
        RouteContext.AddService(typeof(IRedbService), Redb);

        try { await Redb.InitializeAsync(ensureCreated: true); }
        catch { await Redb.InitializeAsync(); }

        await SyncSchemes();
        await Cleanup();
    }

    private async Task SyncSchemes()
    {
        await Redb.SyncSchemeAsync<ConversationProps>();
        await Redb.SyncSchemeAsync<MessageProps>();
        await Redb.SyncSchemeAsync<ApprovalProps>();
        await Redb.SyncSchemeAsync<CostBudgetProps>();
        await Redb.SyncSchemeAsync<ToolCacheProps>();
        await Redb.SyncSchemeAsync<ToolAuditProps>();
        await Redb.SyncSchemeAsync<KnowledgeChunkProps>();
        await Redb.SyncSchemeAsync<PromptTemplateProps>();
        await Redb.SyncSchemeAsync<LlmBatchProps>();
        await Redb.SyncSchemeAsync<EvalRunProps>();
        await Redb.SyncSchemeAsync<ToolIdempotencyProps>();
    }

    /// <summary>
    /// Removes only LLM-scheme rows; never touches unrelated objects.
    /// <c>DeleteWithPurgeAsync</c> cascades automatically — soft-delete on a
    /// conversation root pulls every <see cref="MessageProps"/> descendant under
    /// the same trash container, so we only enumerate the roots / flat tables.
    /// </summary>
    public async Task Cleanup()
    {
        var ids = new List<long>();
        ids.AddRange((await Redb.Query<ConversationProps>().ToListAsync()).Select(o => o.id));
        ids.AddRange((await Redb.Query<ApprovalProps>().ToListAsync()).Select(o => o.id));
        ids.AddRange((await Redb.Query<CostBudgetProps>().ToListAsync()).Select(o => o.id));
        ids.AddRange((await Redb.Query<ToolCacheProps>().ToListAsync()).Select(o => o.id));
        ids.AddRange((await Redb.Query<ToolAuditProps>().ToListAsync()).Select(o => o.id));
        ids.AddRange((await Redb.Query<KnowledgeChunkProps>().ToListAsync()).Select(o => o.id));

        if (ids.Count > 0)
            await Redb.DeleteWithPurgeAsync(ids);
    }

    public async Task DisposeAsync()
    {
        RouteContext?.Dispose();
        if (ServiceProvider is IAsyncDisposable ad)
            await ad.DisposeAsync();
        else
            ServiceProvider?.Dispose();
    }
}

[CollectionDefinition("StoragePro")]
public sealed class StorageProCollection : ICollectionFixture<StorageProFixture> { }

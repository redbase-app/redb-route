using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using redb.Core;
using redb.Core.Models.Configuration;
using redb.Core.Pro.Extensions;
using redb.Postgres.Pro.Extensions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Storage.Redb.Schemas;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>
/// Postgres Pro fixture for the redb.Route.Llm storage integration tests.
/// Uses the Pro free tier (1024 queries, no JWT license required).
/// <para>
/// Cleanup is <b>scoped to the LLM schemes</b> only — never <c>DELETE FROM _objects</c>,
/// which would nuke unrelated data shared with other test runs against the same DB.
/// We enumerate rows of each LLM <c>*Props</c> scheme through the redb query API
/// and remove them via the bulk <c>DeleteAsync(ids)</c> primitive (which cascades
/// to <c>_values</c> and <c>_tree</c>).
/// </para>
/// </summary>
public sealed class PostgresProFixture : IAsyncLifetime
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

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();

        var cs = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres missing in appsettings.json");

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddRedbPro(options =>
        {
            options.UsePostgres(cs)
                .Configure(c =>
                {
                    c.PropsSaveStrategy = PropsSaveStrategy.ChangeTracking;
                    c.SkipHashValidationOnCacheCheck = false;
                    c.EnableLazyLoadingForProps = false;
                    c.EnablePropsCache = false;
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

[CollectionDefinition("PostgresPro")]
public sealed class PostgresProCollection : ICollectionFixture<PostgresProFixture> { }

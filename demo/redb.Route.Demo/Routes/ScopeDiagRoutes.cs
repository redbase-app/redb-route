using Microsoft.Extensions.Logging;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Demo.Routes;

/// <summary>
/// Scope diagnostics — parallel splitter connection tracking. Verifies that each parallel
/// scope gets its own <see cref="IRedbService"/> and own DB connection lifecycle.
/// </summary>
internal sealed class ScopeDiagRoutes : RouteBuilder
{
    private readonly ILogger? _log;
    public ScopeDiagRoutes(ILogger? log) => _log = log;

    private static int _scopeDiagConcurrent;
    private static int _scopeDiagPeakConcurrent;
    private static int _scopeDiagTotalScopes;

    protected override void Configure()
    {
        ConfigureScopeDiagRoute();
    }

    private void ConfigureScopeDiagRoute()
    {
        // Timer fires once after 10s, then every 60s (change period as needed)
        From("timer://scope-diag?period=60000&delay=10000")
            .RouteId("demo-scope-diag")
            .AutoStart(false)
            .Log("[SCOPE-DIAG] ▶ Starting parallel split: 50 items, maxDop=5")

            // Reset counters
            .Process(e =>
            {
                Interlocked.Exchange(ref _scopeDiagConcurrent, 0);
                Interlocked.Exchange(ref _scopeDiagPeakConcurrent, 0);
                Interlocked.Exchange(ref _scopeDiagTotalScopes, 0);
                e.In.Body = Enumerable.Range(1, 50).Cast<object>().ToList();
                e.In.Headers["diag-start-ms"] = Environment.TickCount64;
            })

            // Parallel split: 50 items, maxDop=5 — classic Camel fluent chain
            .Split(Body())
                .ParallelProcessing()
                .MaxParallelism(5)
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
                        // Real redb CRUD: Save → Load → Delete
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
            .EndSplit()

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
}

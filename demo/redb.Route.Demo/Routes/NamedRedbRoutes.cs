using Microsoft.Extensions.Logging;
using redb.Core.Models.Entities;
using redb.Route.Core;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Demo.Routes;

/// <summary>
/// Named redb instances showcase — pg-test and mssql-test CRUD via <c>ProcessWithRedb(name, …)</c>.
/// Named instances are auto-created from the "Redb" section in <c>redb.Route.Demo.config.json</c>.
/// </summary>
internal sealed class NamedRedbRoutes : RouteBuilder
{
    private readonly ILogger? _log;
    public NamedRedbRoutes(ILogger? log) => _log = log;

    protected override void Configure()
    {
        ConfigureNamedRedbRoute();
    }

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
}

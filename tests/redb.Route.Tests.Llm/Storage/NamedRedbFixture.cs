using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using redb.Core;
using redb.Core.Models.Configuration;
using redb.Core.Pro.Extensions;
using redb.MSSql.Pro.Extensions;
using redb.Postgres.Pro.Extensions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RedbCore.Extensions;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.SQLite.Pro.Extensions;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>
/// A second redb Pro instance in a database of its own, next to the default one of <see cref="StorageProFixture"/>: the
/// "agent memory" database of a host that keeps the agent's data apart from its business data. Registered on a route
/// context under <see cref="Name"/> with <see cref="AttachTo"/>, so <c>?redb=</c> and a registration name can pick it.
/// The same provider as the default instance (<c>REDB_PROVIDER</c>); the database is created when it is missing.
/// </summary>
public sealed class NamedRedbFixture : IAsyncLifetime
{
    /// <summary>The name the instance is registered under.</summary>
    public const string Name = "agent-memory";

    private const string Database = "redb_llm_agent_memory";

    private ServiceProvider? _services;

    /// <summary>The named instance.</summary>
    public IRedbService Redb { get; private set; } = null!;

    private static string Provider =>
        Environment.GetEnvironmentVariable("REDB_PROVIDER")?.ToLowerInvariant() is { Length: > 0 } p ? p : "sqlite";

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        switch (Provider)
        {
            case "postgres":
            {
                var cs = new NpgsqlConnectionStringBuilder(
                    global::redb.Route.Tests.Shared.TestDatabases.Postgres(config.GetConnectionString("Postgres")!));
                await EnsurePostgresDatabaseAsync(cs);
                cs.Database = Database;
                services.AddRedbPro(o => o.UsePostgres(cs.ConnectionString).Configure(Settings));
                break;
            }
            case "mssql":
            {
                var cs = new SqlConnectionStringBuilder(
                    global::redb.Route.Tests.Shared.TestDatabases.MsSql(config.GetConnectionString("MsSql")!));
                await EnsureMsSqlDatabaseAsync(cs);
                cs.InitialCatalog = Database;
                services.AddRedbPro(o => o.UseMsSql(cs.ConnectionString).Configure(Settings));
                break;
            }
            default:
                services.AddRedbPro(o => o.UseSqlite($"Data Source={Database}.db").Configure(Settings));
                break;
        }

        _services = services.BuildServiceProvider();
        Redb = _services.GetRequiredService<IRedbService>();
        try { await Redb.InitializeAsync(ensureCreated: true); }
        catch { await Redb.InitializeAsync(); }

        await Redb.SyncSchemeAsync<ConversationProps>();
        await Redb.SyncSchemeAsync<MessageProps>();
        await Redb.SyncSchemeAsync<CostBudgetProps>();
        await Redb.SyncSchemeAsync<ToolAuditProps>();
        await Redb.SyncSchemeAsync<ToolIdempotencyProps>();
    }

    private static void Settings(RedbServiceConfiguration c)
    {
        c.PropsSaveStrategy = PropsSaveStrategy.ChangeTracking;
        c.EnablePropsCache = false;
    }

    /// <summary>Registers the instance on <paramref name="context"/> under <see cref="Name"/>.</summary>
    public void AttachTo(IRouteContext context) => context.RegisterRedbService(Name, Redb);

    private static async Task EnsurePostgresDatabaseAsync(NpgsqlConnectionStringBuilder target)
    {
        var admin = new NpgsqlConnectionStringBuilder(target.ConnectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", connection);
        exists.Parameters.AddWithValue("n", Database);
        if (await exists.ExecuteScalarAsync() is null)
        {
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{Database}\"", connection);
            await create.ExecuteNonQueryAsync();
        }
    }

    private static async Task EnsureMsSqlDatabaseAsync(SqlConnectionStringBuilder target)
    {
        var admin = new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var create = new SqlCommand($"IF DB_ID(N'{Database}') IS NULL CREATE DATABASE [{Database}]", connection);
        await create.ExecuteNonQueryAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_services is not null)
            await _services.DisposeAsync();
    }
}

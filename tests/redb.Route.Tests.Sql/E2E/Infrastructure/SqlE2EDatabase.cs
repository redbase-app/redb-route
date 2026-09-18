using System.Data.Common;
using System.Net.Sockets;
using redb.Route.Sql.Connection;

namespace redb.Route.Tests.Sql.E2E.Infrastructure;

/// <summary>
/// A uniquely named table on one provider for the lifetime of a test. The three target frameworks run the
/// suites in parallel against the same servers, so tests share nothing but the server itself. The table is
/// dropped on dispose; a failing drop is not swallowed.
/// </summary>
public sealed class SqlE2EDatabase : IAsyncDisposable
{
    private bool _created;

    private SqlE2EDatabase(SqlE2EProvider provider)
    {
        Provider = provider;
        Table = "rsql_" + Guid.NewGuid().ToString("N")[..12];
    }

    /// <summary>The provider this table lives on.</summary>
    public SqlE2EProvider Provider { get; }

    /// <summary>Unique table name.</summary>
    public string Table { get; }

    /// <summary>Creates the table with the provider's default DDL, or with <paramref name="createTableSql"/>.</summary>
    public static async Task<SqlE2EDatabase> CreateAsync(SqlE2EProvider provider, Func<string, string>? createTableSql = null)
    {
        var database = new SqlE2EDatabase(provider);
        await database.ExecuteAsync((createTableSql ?? provider.CreateTableSql)(database.Table));
        database._created = true;
        return database;
    }

    /// <summary>Opens a connection with the provider's session settings applied.</summary>
    /// <exception cref="InvalidOperationException">The server is not reachable; the message says how to start it.</exception>
    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = Provider.Factory.CreateConnection()
            ?? throw new InvalidOperationException($"{Provider.Factory.GetType().Name}.CreateConnection() returned null.");
        var ready = false;
        try
        {
            connection.ConnectionString = Provider.ConnectionString;
            try
            {
                await connection.OpenAsync(ct);
            }
            catch (Exception ex) when (ex is DbException or SocketException or TimeoutException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"SQL E2E: provider '{Provider.Name}' is not reachable. {Provider.StartHint}", ex);
            }

            if (Provider.SessionInitSql is { } init)
                await ExecuteAsync(connection, null, init, ct);

            ready = true;
            return connection;
        }
        finally
        {
            if (!ready)
                await connection.DisposeAsync();
        }
    }

    /// <summary>Connection factory for the connector: every connection it hands out is opened by <see cref="OpenAsync"/>.</summary>
    public RecordingConnectionFactory CreateConnectionFactory() => new(this);

    /// <summary>Executes a non-query on an open connection.</summary>
    public static async Task<int> ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Executes a non-query on a fresh connection.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, null, sql);
    }

    /// <summary>Ids in the table, ascending, read on a fresh connection — what is actually persisted.</summary>
    public async Task<int[]> ReadIdsAsync()
    {
        var rows = await QueryAsync($"SELECT id FROM {Table} ORDER BY id");
        return rows.Select(r => Convert.ToInt32(r[0])).ToArray();
    }

    /// <summary>All rows of a query on a fresh connection; <see cref="DBNull"/> becomes null.</summary>
    public async Task<List<object?[]>> QueryAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_created)
            await ExecuteAsync($"DROP TABLE {Table}");
    }
}

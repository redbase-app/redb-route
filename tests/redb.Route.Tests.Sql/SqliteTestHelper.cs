using System.Data.Common;
using Microsoft.Data.Sqlite;
using redb.Route.Sql.Connection;

namespace redb.Route.Tests.Sql;

/// <summary>
/// Helper for creating in-memory SQLite connections for integration tests.
/// Each instance maintains a shared in-memory database that persists until disposed.
/// </summary>
internal sealed class SqliteTestHelper : IDisposable
{
    private readonly SqliteConnection _keepAlive;

    /// <summary>Connection string for the shared in-memory database.</summary>
    public string ConnectionString { get; }

    public SqliteTestHelper()
    {
        var dbName = $"test_{Guid.NewGuid():N}";
        ConnectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";

        // Keep one connection open to preserve the in-memory DB
        _keepAlive = new SqliteConnection(ConnectionString);
        _keepAlive.Open();
    }

    /// <summary>Creates a new opened connection to the shared in-memory database.</summary>
    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    /// <summary>Executes a non-query SQL statement.</summary>
    public int Execute(string sql)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Executes a scalar query.</summary>
    public object? ExecuteScalar(string sql)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    /// <summary>Executes a query and returns all rows as dictionaries.</summary>
    public List<Dictionary<string, object?>> Query(string sql)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Creates a connection factory pointing at this in-memory database.</summary>
    public ISqlConnectionFactory CreateFactory()
    {
        return new SqliteConnectionFactory(ConnectionString);
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
    }
}

/// <summary>
/// Simple ISqlConnectionFactory for SQLite. Bypasses DbProviderFactories.
/// </summary>
internal sealed class SqliteConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string connectionString) => _connectionString = connectionString;

    public async Task<DbConnection> CreateConnectionAsync(bool readOnly = false, CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}

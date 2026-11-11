using System.Data.Common;

namespace redb.Route.Tests.Shared;

/// <summary>
/// Where the database suites find PostgreSQL and SQL Server. The connection strings in the tests and their
/// appsettings stay as they are; <c>REDB_TEST_PG</c> (<c>host:port</c>) and <c>REDB_TEST_MSSQL</c> (<c>host,port</c>)
/// move them to another server, for example the release gate's fresh test databases instead of a machine's permanent
/// ones. Without the variables a connection string comes back unchanged. A variable that carries a whole connection
/// string of its own (ROUTE_SQL_PG_CS, REDB_MSSQL_CS and the like) still wins: these apply only to the defaults.
/// </summary>
internal static class TestDatabases
{
    public const string PostgresVariable = "REDB_TEST_PG";
    public const string MsSqlVariable = "REDB_TEST_MSSQL";

    /// <summary>The PostgreSQL connection string, with host and port from <c>REDB_TEST_PG</c> when it is set.</summary>
    public static string Postgres(string connectionString)
        => Postgres(connectionString, Environment.GetEnvironmentVariable(PostgresVariable));

    /// <summary>The PostgreSQL connection string moved to <paramref name="target"/> (<c>host:port</c>); null or empty keeps it.</summary>
    public static string Postgres(string connectionString, string? target)
    {
        target = target?.Trim();
        if (string.IsNullOrEmpty(target)) return connectionString;

        var colon = target.LastIndexOf(':');
        var host = colon > 0 ? target[..colon] : target;
        var port = colon > 0 ? target[(colon + 1)..] : "5432";

        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (var key in new[] { "Host", "Server", "Port" })
            builder.Remove(key);
        builder["Host"] = host;
        builder["Port"] = port;
        return builder.ConnectionString;
    }

    /// <summary>The SQL Server connection string, with its server from <c>REDB_TEST_MSSQL</c> when it is set.</summary>
    public static string MsSql(string connectionString)
        => MsSql(connectionString, Environment.GetEnvironmentVariable(MsSqlVariable));

    /// <summary>The SQL Server connection string moved to <paramref name="target"/> (<c>host,port</c>); null or empty keeps it.</summary>
    public static string MsSql(string connectionString, string? target)
    {
        target = target?.Trim();
        if (string.IsNullOrEmpty(target)) return connectionString;

        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (var key in new[] { "Server", "Data Source", "Address", "Addr", "Network Address" })
            builder.Remove(key);
        builder["Server"] = target;
        return builder.ConnectionString;
    }
}

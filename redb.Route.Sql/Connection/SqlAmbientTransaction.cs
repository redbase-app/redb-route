using System.Data.Common;
using System.Globalization;
using System.Transactions;

namespace redb.Route.Sql.Connection;

/// <summary>
/// A <c>sql:</c> step inside a <c>.Transacted()</c> block writes through the block's transaction only if its connection
/// enlists in it. Npgsql, SqlClient, MySqlConnector and the Oracle provider do so by default; the Firebird client only
/// with <c>Enlist=true</c>; Microsoft.Data.Sqlite never does; and any provider with <c>Enlist=false</c> or
/// <c>AutoEnlist=false</c> in its connection string opts out.
/// Such a connection would run the statement in autocommit and a rolled-back block would leave its rows behind, so the
/// step refuses before it writes.
/// </summary>
internal static class SqlAmbientTransaction
{
    /// <summary>
    /// The refusal to throw when an ambient transaction is running and <paramref name="connection"/> stays out of it;
    /// <c>null</c> when the step may write. The caller disposes the connection before it throws.
    /// </summary>
    public static InvalidOperationException? Refusal(DbConnection connection)
    {
        if (Transaction.Current is null || Joins(connection))
            return null;

        return new InvalidOperationException(
            $"This sql: step runs inside a .Transacted() block, and its {connection.GetType().Name} connection does not join " +
            "the block's transaction: the statement would commit on its own and stay when the block rolls back. Use a " +
            "provider that enlists in System.Transactions (PostgreSQL, SQL Server), remove Enlist=false from the connection " +
            "string, or run the step outside the block.");
    }

    private static bool Joins(DbConnection connection)
    {
        // Microsoft.Data.Sqlite has no System.Transactions support at all.
        if (connection.GetType().FullName == "Microsoft.Data.Sqlite.SqliteConnection")
            return false;

        var settings = new DbConnectionStringBuilder { ConnectionString = connection.ConnectionString };

        // The Firebird client enlists only when asked to: Enlist is off by default there.
        if (connection.GetType().Name == "FbConnection")
            return settings.TryGetValue("Enlist", out var fbEnlist)
                && Convert.ToString(fbEnlist, CultureInfo.InvariantCulture)?.Trim().ToLowerInvariant() is "true" or "yes";

        foreach (var key in new[] { "Enlist", "AutoEnlist", "Auto Enlist" })
        {
            if (settings.TryGetValue(key, out var value)
                && Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim().ToLowerInvariant() is "false" or "no")
                return false;
        }
        return true;
    }
}

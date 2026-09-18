using System.Data.Common;
using redb.Route.Sql.Connection;

namespace redb.Route.Tests.Sql.Batch.Fakes;

/// <summary>Hands out one prepared connection instance and counts the requests.</summary>
internal sealed class FixedConnectionFactory(DbConnection connection) : ISqlConnectionFactory
{
    /// <summary>How many times a connection was requested.</summary>
    public int Requests { get; private set; }

    public Task<DbConnection> CreateConnectionAsync(bool readOnly = false, CancellationToken ct = default)
    {
        Requests++;
        return Task.FromResult(connection);
    }
}

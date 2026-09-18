using System.Data;
using System.Data.Common;
using redb.Route.Sql.Connection;

namespace redb.Route.Tests.Sql.E2E.Infrastructure;

/// <summary>
/// <see cref="ISqlConnectionFactory"/> over <see cref="SqlE2EDatabase.OpenAsync"/>, so connector connections get the
/// provider's session settings. Records the read-only hint of every request so routing decisions can be asserted, and counts
/// the connections it handed out that are still open, so a test can see a connection that was never returned.
/// </summary>
public sealed class RecordingConnectionFactory : ISqlConnectionFactory
{
    private readonly SqlE2EDatabase _database;
    private readonly List<bool> _readOnlyRequests = [];
    private int _openConnections;

    internal RecordingConnectionFactory(SqlE2EDatabase database) => _database = database;

    /// <summary>The <c>readOnly</c> argument of every request, in order.</summary>
    public IReadOnlyList<bool> ReadOnlyRequests
    {
        get { lock (_readOnlyRequests) return [.. _readOnlyRequests]; }
    }

    /// <summary>Connections handed out and not closed yet.</summary>
    public int OpenConnections => Volatile.Read(ref _openConnections);

    /// <inheritdoc />
    public async Task<DbConnection> CreateConnectionAsync(bool readOnly = false, CancellationToken ct = default)
    {
        lock (_readOnlyRequests) _readOnlyRequests.Add(readOnly);
        var connection = await _database.OpenAsync(ct);

        Interlocked.Increment(ref _openConnections);
        connection.StateChange += (_, change) =>
        {
            if (change.OriginalState == ConnectionState.Open && change.CurrentState == ConnectionState.Closed)
                Interlocked.Decrement(ref _openConnections);
        };
        return connection;
    }
}

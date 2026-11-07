using System.Data.Common;

namespace redb.Route.Sql.Connection;

/// <summary>
/// Abstraction for creating <see cref="DbConnection"/> instances.
/// Implementations may provide pooling, read/write splitting, or failover.
/// </summary>
public interface ISqlConnectionFactory
{
    /// <summary>Creates an opened database connection.</summary>
    /// <param name="readOnly">Hint for read/write split: true = read replica, false = primary.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An opened <see cref="DbConnection"/>.</returns>
    Task<DbConnection> CreateConnectionAsync(bool readOnly = false, CancellationToken ct = default);
}

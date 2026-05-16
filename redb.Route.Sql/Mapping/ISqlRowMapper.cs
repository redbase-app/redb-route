using System.Data.Common;

namespace redb.Route.Sql.Mapping;

/// <summary>
/// Maps a <see cref="DbDataReader"/> row to type <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">Target type.</typeparam>
public interface ISqlRowMapper<out T>
{
    /// <summary>Maps the current row of the reader to an object.</summary>
    /// <param name="reader">Positioned reader (ReadAsync already called).</param>
    /// <returns>Mapped object.</returns>
    T Map(DbDataReader reader);
}

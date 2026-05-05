using System.Data.Common;

namespace redb.Route.Sql.Mapping;

/// <summary>
/// Maps the first column of the current <see cref="DbDataReader"/> row to a scalar value.
/// Returns <c>default(T)</c> if the value is <see cref="DBNull"/>.
/// </summary>
/// <typeparam name="T">Scalar value type.</typeparam>
public sealed class ScalarMapper<T> : ISqlRowMapper<T?>
{
    /// <inheritdoc />
    public T? Map(DbDataReader reader)
    {
        if (reader.IsDBNull(0))
            return default;

        var value = reader.GetValue(0);

        if (value is T typed)
            return typed;

        return (T)Convert.ChangeType(value, typeof(T));
    }
}

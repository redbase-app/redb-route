using System.Data.Common;

namespace redb.Route.Sql.Mapping;

/// <summary>
/// Maps the first column of the current <see cref="DbDataReader"/> row to a scalar value.
/// A <see cref="DBNull"/> is <c>null</c> for a nullable or reference <typeparamref name="T"/> and an error for a value type;
/// other values convert only when that is lossless and unambiguous, as <see cref="PocoRowMapper{T}"/> does.
/// </summary>
/// <typeparam name="T">Scalar value type.</typeparam>
public sealed class ScalarMapper<T> : ISqlRowMapper<T?>
{
    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The value cannot be read as <typeparamref name="T"/> without loss or guessing.</exception>
    public T? Map(DbDataReader reader)
    {
        if (reader.IsDBNull(0))
        {
            if (!SqlValueConverter.AcceptsNull(typeof(T)))
                throw new SqlRowMappingException(
                    $"Column '{reader.GetName(0)}' is NULL and cannot be read as {SqlValueConverter.TypeName(typeof(T))}: " +
                    "use a nullable type.");
            return default;
        }

        var value = reader.GetValue(0);
        if (!SqlValueConverter.TryConvert(value, typeof(T), out var converted, out var reason))
            throw new SqlRowMappingException(
                $"Column '{reader.GetName(0)}' ({value.GetType().Name}) cannot be read as {SqlValueConverter.TypeName(typeof(T))}: {reason}.");

        return (T?)converted;
    }
}

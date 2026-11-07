using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;

namespace redb.Route.Sql.Mapping;

/// <summary>
/// Maps a <see cref="DbDataReader"/> row to a POCO of type <typeparamref name="T"/>
/// via reflection. Caches property accessors per type for performance.
/// Supports case-insensitive and snake_case → PascalCase column matching.
/// </summary>
/// <remarks>
/// A column without a writable property is skipped. A value reaches its property only when the conversion is lossless and
/// unambiguous (see <see cref="SqlValueConverter"/>); a NULL sets a nullable or reference property to null. Anything else —
/// a NULL for a value type, an overflow, a timestamp without a time zone for a <see cref="DateTimeOffset"/> — throws, as
/// Apache Camel's <c>BeanPropertyRowMapper</c> refuses a type mismatch.
/// </remarks>
/// <typeparam name="T">POCO type with a parameterless constructor.</typeparam>
public sealed class PocoRowMapper<T> : ISqlRowMapper<T> where T : new()
{
    private static readonly ConcurrentDictionary<string, PropertyInfo?> _propertyCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly PropertyInfo[] _allProps = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// A column's value cannot be assigned to its property without loss or guessing. The message names the column, the
    /// property and both types, never the value.
    /// </exception>
    public T Map(DbDataReader reader)
    {
        var obj = new T();
        var fieldCount = reader.FieldCount;

        for (var i = 0; i < fieldCount; i++)
        {
            var columnName = reader.GetName(i);
            var prop = ResolveProperty(columnName);
            if (prop == null || !prop.CanWrite) continue;

            if (reader.IsDBNull(i))
            {
                if (!SqlValueConverter.AcceptsNull(prop.PropertyType))
                    throw MappingError(columnName, "NULL", prop, "a NULL cannot be assigned to a non-nullable value type; make the property nullable");

                prop.SetValue(obj, null);
                continue;
            }

            var value = reader.GetValue(i);
            if (!SqlValueConverter.TryConvert(value, prop.PropertyType, out var converted, out var reason))
                throw MappingError(columnName, value.GetType().Name, prop, reason);

            prop.SetValue(obj, converted);
        }

        return obj;
    }

    private static SqlRowMappingException MappingError(string column, string sourceType, PropertyInfo prop, string reason) =>
        new($"Column '{column}' ({sourceType}) cannot be assigned to {typeof(T).Name}.{prop.Name} " +
            $"({SqlValueConverter.TypeName(prop.PropertyType)}): {reason}.");

    private static PropertyInfo? ResolveProperty(string columnName) =>
        _propertyCache.GetOrAdd(columnName, name => ColumnNameMatcher.Find(_allProps, name));
}

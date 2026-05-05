using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;

namespace redb.Route.Sql.Mapping;

/// <summary>
/// Maps a <see cref="DbDataReader"/> row to a POCO of type <typeparamref name="T"/>
/// via reflection. Caches property accessors per type for performance.
/// Supports case-insensitive and snake_case → PascalCase column matching.
/// </summary>
/// <typeparam name="T">POCO type with a parameterless constructor.</typeparam>
public sealed class PocoRowMapper<T> : ISqlRowMapper<T> where T : new()
{
    private static readonly ConcurrentDictionary<string, PropertyInfo?> _propertyCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly PropertyInfo[] _allProps = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    /// <inheritdoc />
    public T Map(DbDataReader reader)
    {
        var obj = new T();
        var fieldCount = reader.FieldCount;

        for (var i = 0; i < fieldCount; i++)
        {
            if (reader.IsDBNull(i)) continue;

            var columnName = reader.GetName(i);
            var prop = ResolveProperty(columnName);
            if (prop == null || !prop.CanWrite) continue;

            var value = reader.GetValue(i);

            try
            {
                if (prop.PropertyType.IsInstanceOfType(value))
                {
                    prop.SetValue(obj, value);
                }
                else
                {
                    var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                    var converted = Convert.ChangeType(value, targetType);
                    prop.SetValue(obj, converted);
                }
            }
            catch
            {
                // Skip columns that can't be converted
            }
        }

        return obj;
    }

    private static PropertyInfo? ResolveProperty(string columnName)
    {
        return _propertyCache.GetOrAdd(columnName, name =>
        {
            // Direct case-insensitive match
            var prop = Array.Find(_allProps, p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (prop != null) return prop;

            // [Column] attribute match
            prop = Array.Find(_allProps, p =>
                p.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.ColumnAttribute>()
                    ?.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
            if (prop != null) return prop;

            // snake_case → PascalCase: user_name → UserName
            var pascal = SnakeToPascal(name);
            return Array.Find(_allProps, p => p.Name.Equals(pascal, StringComparison.OrdinalIgnoreCase));
        });
    }

    private static string SnakeToPascal(string snake)
    {
        var parts = snake.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p =>
            string.Concat(char.ToUpperInvariant(p[0]).ToString(), p.AsSpan(1))));
    }
}

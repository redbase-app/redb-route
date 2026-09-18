using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

namespace redb.Route.Sql.Mapping;

/// <summary>
/// Which property a column or parameter name refers to: a property of that name ignoring case, then a property whose
/// <see cref="ColumnAttribute"/> carries the name, then the snake_case name read as PascalCase (<c>user_name</c> →
/// <c>UserName</c>). Result rows are mapped to objects and batch items to parameters by this one rule.
/// </summary>
internal static class ColumnNameMatcher
{
    /// <summary>The property among <paramref name="properties"/> that <paramref name="name"/> refers to, or null.</summary>
    internal static PropertyInfo? Find(PropertyInfo[] properties, string name)
    {
        // Direct case-insensitive match
        var property = Array.Find(properties, p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (property != null) return property;

        // [Column] attribute match
        property = Array.Find(properties, p =>
            p.GetCustomAttribute<ColumnAttribute>()?.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
        if (property != null) return property;

        // snake_case → PascalCase: user_name → UserName
        var pascal = SnakeToPascal(name);
        return Array.Find(properties, p => p.Name.Equals(pascal, StringComparison.OrdinalIgnoreCase));
    }

    private static string SnakeToPascal(string snake)
    {
        var parts = snake.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p =>
            string.Concat(char.ToUpperInvariant(p[0]).ToString(), p.AsSpan(1))));
    }
}

using Google.Cloud.Firestore;

namespace redb.Route.Firebase;

/// <summary>
/// Shared Firestore query building logic used by both producer and consumer.
/// Eliminates code duplication for Where/OrderBy filter parsing.
/// </summary>
internal static class FirestoreQueryHelper
{
    /// <summary>
    /// Parses "field==value;field2&gt;10" into chained Firestore query filters.
    /// Supports: ==, !=, &lt;, &lt;=, &gt;, &gt;=, array-contains
    /// </summary>
    internal static Query ApplyWhereFilters(Query query, string where)
    {
        var conditions = where.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var condition in conditions)
        {
            var (field, op, value) = ParseCondition(condition);

            query = op switch
            {
                "==" => query.WhereEqualTo(field, ParseValue(value)),
                "!=" => query.WhereNotEqualTo(field, ParseValue(value)),
                ">=" => query.WhereGreaterThanOrEqualTo(field, ParseValue(value)),
                "<=" => query.WhereLessThanOrEqualTo(field, ParseValue(value)),
                ">" => query.WhereGreaterThan(field, ParseValue(value)),
                "<" => query.WhereLessThan(field, ParseValue(value)),
                "array-contains" => query.WhereArrayContains(field, ParseValue(value)),
                _ => throw new InvalidOperationException($"Unknown Firestore filter operator: {op}")
            };
        }

        return query;
    }

    /// <summary>
    /// Parses "fieldName [desc]" into an OrderBy clause.
    /// </summary>
    internal static Query ApplyOrderBy(Query query, string orderBy)
    {
        var parts = orderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var field = parts[0];
        var desc = parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);

        return desc ? query.OrderByDescending(field) : query.OrderBy(field);
    }

    internal static (string field, string op, string value) ParseCondition(string condition)
    {
        string[] operators = ["array-contains", "!=", ">=", "<=", "==", ">", "<"];

        foreach (var op in operators)
        {
            var idx = condition.IndexOf(op, StringComparison.Ordinal);
            if (idx > 0)
                return (condition[..idx].Trim(), op, condition[(idx + op.Length)..].Trim());
        }

        throw new InvalidOperationException($"Cannot parse Firestore where condition: {condition}");
    }

    internal static object ParseValue(string value)
    {
        if (int.TryParse(value, out var intVal)) return intVal;
        if (long.TryParse(value, out var longVal)) return longVal;
        if (double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var dblVal)) return dblVal;
        if (bool.TryParse(value, out var boolVal)) return boolVal;
        return value;
    }
}

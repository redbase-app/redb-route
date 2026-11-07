using System.Data.Common;

namespace redb.Route.Sql.Mapping;

/// <summary>
/// Maps a <see cref="DbDataReader"/> row to <c>Dictionary&lt;string, object?&gt;</c>.
/// Column names are keys, values are as-is from the provider (DBNull → null).
/// </summary>
public sealed class DictionaryRowMapper : ISqlRowMapper<Dictionary<string, object?>>
{
    /// <inheritdoc />
    public Dictionary<string, object?> Map(DbDataReader reader)
    {
        var fieldCount = reader.FieldCount;
        var row = new Dictionary<string, object?>(fieldCount, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < fieldCount; i++)
        {
            var name = reader.GetName(i);
            var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
            row[name] = value;
        }

        return row;
    }
}

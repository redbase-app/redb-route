using System.Text.RegularExpressions;

namespace redb.Route.Sql.Repositories;

/// <summary>
/// Configuration for <see cref="SqlIdempotentRepository"/>.
/// </summary>
public sealed class SqlIdempotentOptions
{
    private static readonly Regex ValidTableName = new(@"^[a-zA-Z_][a-zA-Z0-9_.]*$", RegexOptions.Compiled);
    private string _tableName = "redb_idempotent";

    /// <summary>Named data source from the registry.</summary>
    public string DataSource { get; set; } = string.Empty;

    /// <summary>
    /// Table name for storing idempotent keys. Default: "redb_idempotent". The name goes into DDL and statements as written,
    /// so only an identifier (letters, digits, <c>_</c>, <c>.</c>) is accepted.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not an identifier.</exception>
    public string TableName
    {
        get => _tableName;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !ValidTableName.IsMatch(value))
                throw new ArgumentException($"Invalid table name: '{value}'. Must match [a-zA-Z_][a-zA-Z0-9_.]*", nameof(value));
            _tableName = value;
        }
    }

    /// <summary>Scope identifier (route id). Allows multiple routes to share one table.</summary>
    public string ProcessorName { get; set; } = string.Empty;

    /// <summary>Auto-cleanup: delete entries older than TTL. Null = no cleanup.</summary>
    public TimeSpan? Ttl { get; set; }

    /// <summary>Auto-create the table on first use. Default: true.</summary>
    public bool CreateTable { get; set; } = true;
}

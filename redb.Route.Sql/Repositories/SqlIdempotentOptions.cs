namespace redb.Route.Sql.Repositories;

/// <summary>
/// Configuration for <see cref="SqlIdempotentRepository"/>.
/// </summary>
public sealed class SqlIdempotentOptions
{
    /// <summary>Named data source from the registry.</summary>
    public string DataSource { get; set; } = string.Empty;

    /// <summary>Table name for storing idempotent keys. Default: "redb_idempotent".</summary>
    public string TableName { get; set; } = "redb_idempotent";

    /// <summary>Scope identifier (route id). Allows multiple routes to share one table.</summary>
    public string ProcessorName { get; set; } = string.Empty;

    /// <summary>Auto-cleanup: delete entries older than TTL. Null = no cleanup.</summary>
    public TimeSpan? Ttl { get; set; }

    /// <summary>Auto-create the table on first use. Default: true.</summary>
    public bool CreateTable { get; set; } = true;
}

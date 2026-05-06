namespace redb.Route.Sql.Connection;

/// <summary>
/// Configuration options for a named data source connection.
/// </summary>
public sealed class SqlConnectionOptions
{
    /// <summary>Primary connection string (used for writes and default reads).</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Read replica connection string. If set, SELECT queries use this connection.
    /// If not set, all queries go through <see cref="ConnectionString"/>.
    /// </summary>
    public string? ReadConnectionString { get; set; }

    /// <summary>
    /// ADO.NET provider invariant name (e.g. "Microsoft.Data.SqlClient", "Npgsql", "MySqlConnector").
    /// Used to resolve <see cref="System.Data.Common.DbProviderFactory"/> from the provider registry.
    /// </summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>
    /// Explicit <see cref="System.Data.Common.DbProviderFactory"/> instance.
    /// Takes priority over <see cref="ProviderName"/> if both are set.
    /// </summary>
    public System.Data.Common.DbProviderFactory? ProviderFactory { get; set; }

    /// <summary>Minimum connection pool size. Default: 0 (provider default).</summary>
    public int MinPoolSize { get; set; }

    /// <summary>Maximum connection pool size. Default: 100.</summary>
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>Maximum lifetime of a connection. Default: <see cref="TimeSpan.Zero"/> (unlimited).</summary>
    public TimeSpan ConnectionLifetime { get; set; } = TimeSpan.Zero;

    /// <summary>Idle timeout before a pooled connection is closed. Default: 5 minutes.</summary>
    public TimeSpan ConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Validate connection on checkout from pool. Default: false.</summary>
    public bool TestOnBorrow { get; set; }

    /// <summary>SQL used to validate a connection. Default: "SELECT 1".</summary>
    public string ValidationQuery { get; set; } = "SELECT 1";

    /// <summary>Timeout for validation query in seconds. Default: 5.</summary>
    public int ValidationTimeout { get; set; } = 5;

    /// <summary>Timeout for establishing a new connection in seconds. Default: 15.</summary>
    public int ConnectTimeout { get; set; } = 15;

    /// <summary>Default command execution timeout in seconds. Default: 30.</summary>
    public int CommandTimeout { get; set; } = 30;

    /// <summary>Number of retry attempts for transient failures. Default: 0 (no retry).</summary>
    public int MaxRetries { get; set; }

    /// <summary>Delay between retry attempts. Default: 1 second.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Enable automatic retry on transient failures. Default: false.</summary>
    public bool EnableRetryOnFailure { get; set; }
}

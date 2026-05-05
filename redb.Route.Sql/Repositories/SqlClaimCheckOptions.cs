using System;
using System.Text.RegularExpressions;

namespace redb.Route.Sql.Repositories;

/// <summary>
/// Configuration options for <see cref="SqlClaimCheckRepository"/>.
/// </summary>
public sealed class SqlClaimCheckOptions
{
    private static readonly Regex ValidTableName = new(@"^[a-zA-Z_][a-zA-Z0-9_.]*$", RegexOptions.Compiled);
    private string _tableName = "redb_claim_check";

    /// <summary>Table name for claim check entries (default: "redb_claim_check").</summary>
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

    /// <summary>Default TTL for entries. Zero or null means no expiry.</summary>
    public TimeSpan? DefaultTtl { get; set; }

    /// <summary>Auto-create the table on first use (default: true).</summary>
    public bool CreateTable { get; set; } = true;

    /// <summary>
    /// Run cleanup of expired rows every N Store operations. Zero disables automatic cleanup.
    /// </summary>
    public int CleanupInterval { get; set; } = 100;
}

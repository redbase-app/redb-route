using redb.Core.Attributes;

namespace redb.Route.RedbCore.Models;

/// <summary>
/// EAV model for idempotent message tracking.
/// Stored via <see cref="redb.Core.IRedbService"/> — works on any redb-supported database
/// (PostgreSQL, SQL Server) without raw DDL.
/// </summary>
[RedbScheme("Route Idempotent Entry")]
public class IdempotentEntryProps
{
    /// <summary>Route/processor scope identifier.</summary>
    public string ProcessorName { get; set; } = string.Empty;

    /// <summary>Unique message key (correlation ID, dedup key).</summary>
    public string MessageKey { get; set; } = string.Empty;

    /// <summary>Timestamp when the entry was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Whether processing has been confirmed (two-phase commit).</summary>
    public bool Confirmed { get; set; }
}

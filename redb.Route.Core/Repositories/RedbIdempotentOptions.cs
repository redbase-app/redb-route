namespace redb.Route.RedbCore.Repositories;

/// <summary>
/// Configuration options for <see cref="RedbIdempotentRepository"/>.
/// </summary>
public sealed class RedbIdempotentOptions
{
    /// <summary>Scope identifier (route ID or processor name). Allows multiple routes to share one scheme.</summary>
    public string ProcessorName { get; set; } = string.Empty;

    /// <summary>Auto-cleanup: delete entries older than TTL. Null = no cleanup.</summary>
    public TimeSpan? Ttl { get; set; }
}

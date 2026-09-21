using redb.Route.Core;

namespace redb.Route.Redis;

/// <summary>
/// Typed options for <see cref="RedisEndpoint"/>. Bound from URI query parameters.
/// </summary>
public sealed class RedisEndpointOptions : EndpointOptions
{
    // ── Connection ──

    /// <summary>Redis connection string (default: localhost:6379).</summary>
    [Sensitive]
    public string ConnectionString { get; set; } = "localhost:6379";

    /// <summary>Redis database index (default: 0).</summary>
    public int Database { get; set; }

    /// <summary>Redis password (optional).</summary>
    [Sensitive]
    public string? Password { get; set; }

    /// <summary>Name of <see cref="RedisConnectionFactory"/> in the route registry.</summary>
    public string? ConnectionFactory { get; set; }

    // ── Operation parameters ──

    /// <summary>Redis key (for key-based operations). May contain dynamic expressions.</summary>
    public string? Key { get; set; }

    /// <summary>Channel name for Pub/Sub operations.</summary>
    public string? Channel { get; set; }

    /// <summary>Stream name for Redis Streams.</summary>
    public string? StreamName { get; set; }

    /// <summary>Consumer group for Streams.</summary>
    public string? ConsumerGroup { get; set; }

    /// <summary>Consumer name within a group.</summary>
    public string? ConsumerName { get; set; }

    /// <summary>Redis command for COMMAND operation type.</summary>
    public string? Command { get; set; }

    /// <summary>
    /// Subscribe to <see cref="Channel"/> as a pattern. PSUBSCRIBE always does; this makes SUBSCRIBE do it too.
    /// </summary>
    public bool UsePattern { get; set; }

    // ── TTL and score ──

    /// <summary>Time-to-live in seconds for SET/EXPIRE. 0 = no expiry.</summary>
    public int Ttl { get; set; }

    /// <summary>Score for sorted set operations.</summary>
    public double? Score { get; set; }

    /// <summary>Hash field name for HSET/HGET operations.</summary>
    public string? Field { get; set; }

    // ── Range ──

    /// <summary>Range start for LRANGE/ZRANGE operations.</summary>
    public long? Start { get; set; }

    /// <summary>Range stop for LRANGE/ZRANGE operations.</summary>
    public long? Stop { get; set; }

    /// <summary>Min score for ZRANGEBYSCORE.</summary>
    public double? MinScore { get; set; }

    /// <summary>Max score for ZRANGEBYSCORE.</summary>
    public double? MaxScore { get; set; }

    // ── Geospatial ──

    /// <summary>Longitude for GEOADD/GEORADIUS.</summary>
    public double? Longitude { get; set; }

    /// <summary>Latitude for GEOADD/GEORADIUS.</summary>
    public double? Latitude { get; set; }

    /// <summary>First member for GEODIST.</summary>
    public string? Member1 { get; set; }

    /// <summary>Second member for GEODIST.</summary>
    public string? Member2 { get; set; }

    /// <summary>Unit for geo operations: m, km, mi, ft (default: m).</summary>
    public string GeoUnit { get; set; } = "m";

    // ── Bitmap ──

    /// <summary>Bit offset for SETBIT/GETBIT.</summary>
    public long? Offset { get; set; }

    /// <summary>Bit value for SETBIT.</summary>
    public bool? Bit { get; set; }

    // ── Streams ──

    /// <summary>Max stream length for XADD (0 = unlimited).</summary>
    public int StreamMaxLength { get; set; }

    /// <summary>Use approximate max length for XADD trimming.</summary>
    public bool StreamApproximate { get; set; } = true;

    /// <summary>Number of entries to read at a time from a stream.</summary>
    public int StreamReadCount { get; set; } = 10;

    /// <summary>Block time in ms when no stream entries available.</summary>
    public int StreamBlockTimeMs { get; set; } = 1000;

    /// <summary>
    /// A group consumer reads with NOACK: an entry counts as delivered when it is read and never enters the group's
    /// pending list, so an entry whose route failed is not read again (at-most-once). Default <c>false</c>: the consumer
    /// acknowledges (XACK) an entry after its route succeeded, and a failed one stays pending (at-least-once).
    /// </summary>
    public bool StreamNoAck { get; set; }

    /// <summary>
    /// Where a stream consumer starts. Unset: at the entries added from now on — for a group, the group is created at the
    /// stream's end; without one, the consumer pins the stream's last entry when it starts. <c>0</c> reads from the
    /// beginning, an entry id after that entry. <c>&gt;</c> only means something to a group and is refused without one.
    /// </summary>
    public string? StreamStartPosition { get; set; }

    /// <summary>
    /// A group consumer claims (XAUTOCLAIM) entries of its group that have stayed pending for at least this many
    /// milliseconds — a failed entry of its own, or one a consumer that died left behind — and processes them again.
    /// Unset: pending entries are not claimed. Must exceed the longest time a route takes: a slower one would have its
    /// entry claimed while it still works on it. Not with <see cref="StreamNoAck"/>, which leaves nothing pending.
    /// </summary>
    public int? StreamClaimMinIdleMs { get; set; }

    /// <summary>
    /// A BLPOP/BRPOP consumer moves an item into this list (LMOVE) instead of popping it, and removes it from there after
    /// its route succeeded: a failed item goes back to the head of the queue and is processed again, and what a
    /// previous run left in this list is returned to the queue when the consumer starts (at-least-once). Unset: the item
    /// is popped and a failed one is gone (at-most-once). One consumer per processing list.
    /// </summary>
    public string? ProcessingList { get; set; }

    // ── Transactions ──

    /// <summary>
    /// Producer: whether the operation waits for the commit of the enclosing <c>.Transacted()</c> block. PUBLISH and XADD
    /// announce work and follow the block when unset: deferred inside one, at once outside; <c>false</c> sends at once even
    /// inside one. Every other write runs at once unless <c>true</c> defers it; <c>true</c> requires a block and is refused
    /// at start for an operation that exists for what it returns (GET, LPOP and the other reads).
    /// </summary>
    public bool? Transacted { get; set; }

    // ── Resilience ──

    /// <summary>Polling delay in ms when list/stream consumer finds no messages.</summary>
    public int PollDelayMs { get; set; } = 1000;

    /// <inheritdoc />
    public override void Validate()
    {
        if (StreamReadCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(StreamReadCount), "StreamReadCount must be > 0.");
        if (StreamClaimMinIdleMs is <= 0)
            throw new ArgumentOutOfRangeException(nameof(StreamClaimMinIdleMs), "StreamClaimMinIdleMs must be > 0.");
        if (StreamClaimMinIdleMs is not null && StreamNoAck)
            throw new ArgumentException(
                "'streamClaimMinIdleMs' claims pending entries, and 'streamNoAck=true' leaves none: they contradict each other.");
        if (ProcessingList is not null && string.IsNullOrWhiteSpace(ProcessingList))
            throw new ArgumentException("'processingList' is empty: name the list, or leave it unset.");

        // The core binder leaves a parameter it cannot place — a name no option has, or a value that does not convert to
        // the option's type — among the unmapped parameters, where nothing reads it: a typo would drop the option without
        // a word. Each one is refused, by name only: the value may be a secret.
        if (UnmappedParameters.Count > 0)
            throw new ArgumentException(
                string.Join(" ", UnmappedParameters.Keys.Select(DescribeUnmapped)), UnmappedParameters.Keys.First());
    }

    private static string DescribeUnmapped(string name)
    {
        if (name.Equals("streamAutoAck", StringComparison.OrdinalIgnoreCase))
            return "'streamAutoAck' is gone: 'streamAutoAck=false' read with NOACK while its name promised a manual " +
                   "acknowledgement. A group consumer acknowledges after its route succeeded; 'streamNoAck=true' reads " +
                   "with NOACK (at-most-once).";

        var option = Array.Find(
            typeof(RedisEndpointOptions).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance),
            p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (option is null)
            return $"'{name}' is not an option of the Redis endpoint, so it would be dropped without a word.";

        var type = Nullable.GetUnderlyingType(option.PropertyType) ?? option.PropertyType;
        return $"'{name}': the value is not a {type.Name}, so the option would be dropped without a word.";
    }
}

using redb.Route.Core;

namespace redb.Route.Redis;

/// <summary>
/// Typed options for <see cref="RedisEndpoint"/>. Bound from URI query parameters.
/// </summary>
public sealed class RedisEndpointOptions : EndpointOptions
{
    // ── Connection ──

    /// <summary>Redis connection string (default: localhost:6379).</summary>
    public string ConnectionString { get; set; } = "localhost:6379";

    /// <summary>Redis database index (default: 0).</summary>
    public int Database { get; set; }

    /// <summary>Redis password (optional).</summary>
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

    /// <summary>Use pattern subscription for Pub/Sub.</summary>
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

    /// <summary>Auto-ack stream entries after processing.</summary>
    public bool StreamAutoAck { get; set; } = true;

    /// <summary>Starting position for stream reads (&gt; = new messages).</summary>
    public string StreamStartPosition { get; set; } = ">";

    // ── Transactions ──

    /// <summary>Enable transacted mode.</summary>
    public bool Transacted { get; set; }

    // ── Resilience ──

    /// <summary>Polling delay in ms when list/stream consumer finds no messages.</summary>
    public int PollDelayMs { get; set; } = 1000;

    /// <inheritdoc />
    public override void Validate()
    {
        if (StreamReadCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(StreamReadCount), "StreamReadCount must be > 0.");
    }
}

namespace redb.Route.Redis;

/// <summary>
/// Redis operation types — determines what the producer/consumer does.
/// Parsed from the first path segment of the URI: <c>redis:OPERATION:resource</c>.
/// </summary>
public enum RedisOperationType
{
    // ── Key/Value (Cache) ──
    SET,
    GET,
    DEL,
    EXISTS,
    EXPIRE,
    INCR,
    DECR,
    SETNX,

    // ── Pub/Sub ──
    PUBLISH,
    SUBSCRIBE,
    PSUBSCRIBE,

    // ── Streams ──
    XADD,
    XREAD,
    XGROUP,

    // ── Lists ──
    LPUSH,
    RPUSH,
    LPOP,
    RPOP,
    LLEN,
    LRANGE,
    BLPOP,
    BRPOP,

    // ── Hashes ──
    HSET,
    HGET,
    HMSET,
    HMGET,
    HGETALL,
    HDEL,
    HLEN,

    // ── Sets ──
    SADD,
    SREM,
    SMEMBERS,
    SCARD,
    SISMEMBER,

    // ── Sorted Sets ──
    ZADD,
    ZREM,
    ZRANGE,
    ZCARD,
    ZSCORE,
    ZRANGEBYSCORE,

    // ── Geospatial ──
    GEOADD,
    GEODIST,
    GEORADIUS,

    // ── HyperLogLog ──
    PFADD,
    PFCOUNT,
    PFMERGE,

    // ── Bitmap ──
    SETBIT,
    GETBIT,
    BITCOUNT,

    // ── Arbitrary command ──
    COMMAND
}

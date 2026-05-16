using redb.Route.Redis;

namespace redb.Route.Tests.Redis;

public sealed class RedisOperationTypeTests
{
    [Theory]
    [InlineData("SET", RedisOperationType.SET)]
    [InlineData("GET", RedisOperationType.GET)]
    [InlineData("DEL", RedisOperationType.DEL)]
    [InlineData("EXISTS", RedisOperationType.EXISTS)]
    [InlineData("EXPIRE", RedisOperationType.EXPIRE)]
    [InlineData("INCR", RedisOperationType.INCR)]
    [InlineData("DECR", RedisOperationType.DECR)]
    [InlineData("SETNX", RedisOperationType.SETNX)]
    [InlineData("PUBLISH", RedisOperationType.PUBLISH)]
    [InlineData("SUBSCRIBE", RedisOperationType.SUBSCRIBE)]
    [InlineData("PSUBSCRIBE", RedisOperationType.PSUBSCRIBE)]
    [InlineData("XADD", RedisOperationType.XADD)]
    [InlineData("XREAD", RedisOperationType.XREAD)]
    [InlineData("XGROUP", RedisOperationType.XGROUP)]
    [InlineData("LPUSH", RedisOperationType.LPUSH)]
    [InlineData("RPUSH", RedisOperationType.RPUSH)]
    [InlineData("LPOP", RedisOperationType.LPOP)]
    [InlineData("RPOP", RedisOperationType.RPOP)]
    [InlineData("LLEN", RedisOperationType.LLEN)]
    [InlineData("LRANGE", RedisOperationType.LRANGE)]
    [InlineData("BLPOP", RedisOperationType.BLPOP)]
    [InlineData("BRPOP", RedisOperationType.BRPOP)]
    [InlineData("HSET", RedisOperationType.HSET)]
    [InlineData("HGET", RedisOperationType.HGET)]
    [InlineData("HMSET", RedisOperationType.HMSET)]
    [InlineData("HMGET", RedisOperationType.HMGET)]
    [InlineData("HGETALL", RedisOperationType.HGETALL)]
    [InlineData("HDEL", RedisOperationType.HDEL)]
    [InlineData("HLEN", RedisOperationType.HLEN)]
    [InlineData("SADD", RedisOperationType.SADD)]
    [InlineData("SREM", RedisOperationType.SREM)]
    [InlineData("SMEMBERS", RedisOperationType.SMEMBERS)]
    [InlineData("SCARD", RedisOperationType.SCARD)]
    [InlineData("SISMEMBER", RedisOperationType.SISMEMBER)]
    [InlineData("ZADD", RedisOperationType.ZADD)]
    [InlineData("ZREM", RedisOperationType.ZREM)]
    [InlineData("ZRANGE", RedisOperationType.ZRANGE)]
    [InlineData("ZCARD", RedisOperationType.ZCARD)]
    [InlineData("ZSCORE", RedisOperationType.ZSCORE)]
    [InlineData("ZRANGEBYSCORE", RedisOperationType.ZRANGEBYSCORE)]
    [InlineData("GEOADD", RedisOperationType.GEOADD)]
    [InlineData("GEODIST", RedisOperationType.GEODIST)]
    [InlineData("GEORADIUS", RedisOperationType.GEORADIUS)]
    [InlineData("PFADD", RedisOperationType.PFADD)]
    [InlineData("PFCOUNT", RedisOperationType.PFCOUNT)]
    [InlineData("PFMERGE", RedisOperationType.PFMERGE)]
    [InlineData("SETBIT", RedisOperationType.SETBIT)]
    [InlineData("GETBIT", RedisOperationType.GETBIT)]
    [InlineData("BITCOUNT", RedisOperationType.BITCOUNT)]
    [InlineData("COMMAND", RedisOperationType.COMMAND)]
    public void AllOperationTypes_CanBeParsed(string name, RedisOperationType expected)
    {
        Enum.TryParse<RedisOperationType>(name, ignoreCase: true, out var result).Should().BeTrue();
        result.Should().Be(expected);
    }

    [Fact]
    public void TotalOperationCount_Is53()
    {
        var count = Enum.GetValues<RedisOperationType>().Length;
        count.Should().BeGreaterThanOrEqualTo(50);
    }
}

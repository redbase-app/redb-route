using redb.Route.Core;
using redb.Route.Expressions;
using RedisDsl = redb.Route.Redis.Redis;

namespace redb.Route.Tests.Redis;

public class RedisBuilderTests
{
    private static ConstantExpression C(string s) => new(s);
    // ── Factory methods ─────────────────────────────────────────────

    [Theory]
    [InlineData("SET")]
    [InlineData("GET")]
    [InlineData("DEL")]
    public void KeyValue_Factories_StartWithRedisScheme(string op)
    {
        var builder = op switch
        {
            "SET" => RedisDsl.Set("k"),
            "GET" => RedisDsl.Get("k"),
            "DEL" => RedisDsl.Del("k"),
            _ => throw new ArgumentException()
        };
        var uri = builder.Build();
        uri.Should().StartWith($"redis:{op}:k");
    }

    [Fact]
    public void Subscribe_StartsWithRedisScheme()
    {
        var uri = RedisDsl.Subscribe("events").Build();
        uri.Should().StartWith("redis:SUBSCRIBE:events");
    }

    [Fact]
    public void XAdd_StartsWithRedisScheme()
    {
        var uri = RedisDsl.XAdd("my-stream").Build();
        uri.Should().StartWith("redis:XADD:my-stream");
    }

    [Fact]
    public void LPush_StartsWithRedisScheme()
    {
        var uri = RedisDsl.LPush("queue").Build();
        uri.Should().StartWith("redis:LPUSH:queue");
    }

    [Fact]
    public void Command_Generic_StartsWithRedisScheme()
    {
        var uri = RedisDsl.Command("HSET", "map").Build();
        uri.Should().StartWith("redis:HSET:map");
    }

    [Fact]
    public void Exists_StartsWithRedisScheme()
    {
        var uri = RedisDsl.Exists("k").Build();
        uri.Should().StartWith("redis:EXISTS:k");
    }

    [Fact]
    public void Incr_StartsWithRedisScheme()
    {
        var uri = RedisDsl.Incr("counter").Build();
        uri.Should().StartWith("redis:INCR:counter");
    }

    // ── Connection ──────────────────────────────────────────────────

    [Fact]
    public void Connection_SetsParam()
    {
        var uri = RedisDsl.Set("k").Connection(C("redis:6379")).Build();
        uri.Should().Contain("connectionString=redis%3a6379");
    }

    [Fact]
    public void Database_SetsParam()
    {
        var uri = RedisDsl.Set("k").Database(3).Build();
        uri.Should().Contain("database=3");
    }

    [Fact]
    public void Password_SetsParam()
    {
        var uri = RedisDsl.Set("k").Password(C("secret")).Build();
        uri.Should().Contain("password=secret");
    }

    [Fact]
    public void ConnectionFactory_SetsParam()
    {
        var uri = RedisDsl.Set("k").ConnectionFactory(C("myRedis")).Build();
        uri.Should().Contain("connectionFactory=myRedis");
    }

    // ── Common params ───────────────────────────────────────────────

    [Fact]
    public void Ttl_SetsParam()
    {
        var uri = RedisDsl.Set("k").Ttl(300).Build();
        uri.Should().Contain("ttl=300");
    }

    [Fact]
    public void UsePattern_SetsParam()
    {
        var uri = RedisDsl.Subscribe("events.*").UsePattern().Build();
        uri.Should().Contain("usePattern=true");
    }

    [Fact]
    public void Transacted_SetsParam()
    {
        var uri = RedisDsl.Set("k").Transacted().Build();
        uri.Should().Contain("transacted=true");
    }

    [Fact]
    public void PollDelay_SetsParam()
    {
        var uri = RedisDsl.XRead("s").PollDelay(2000).Build();
        uri.Should().Contain("pollDelayMs=2000");
    }

    // ── Stream params ───────────────────────────────────────────────

    [Fact]
    public void ConsumerGroup_SetsParam()
    {
        var uri = RedisDsl.XRead("s").ConsumerGroup(C("workers")).Build();
        uri.Should().Contain("consumerGroup=workers");
    }

    [Fact]
    public void ConsumerName_SetsParam()
    {
        var uri = RedisDsl.XRead("s").ConsumerName(C("w1")).Build();
        uri.Should().Contain("consumerName=w1");
    }

    [Fact]
    public void StreamMaxLength_SetsParam()
    {
        var uri = RedisDsl.XAdd("s").StreamMaxLength(10000).Build();
        uri.Should().Contain("streamMaxLength=10000");
    }

    // ── Sorted set ──────────────────────────────────────────────────

    [Fact]
    public void Score_SetsParam()
    {
        var uri = RedisDsl.Command("ZADD", "leaderboard").Score(99.5).Build();
        uri.Should().Contain("score=99.5");
    }

    // ── Hash ────────────────────────────────────────────────────────

    [Fact]
    public void Field_SetsParam()
    {
        var uri = RedisDsl.Command("HSET", "hash").Field(C("name")).Build();
        uri.Should().Contain("field=name");
    }

    // ── Conversion ──────────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = RedisDsl.Set("k").Ttl(60);
        uri.Should().StartWith("redis:SET:k?");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = RedisDsl.Set("k").Ttl(60).Connection(C("c"));
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full chain ──────────────────────────────────────────────────

    [Fact]
    public void FullChain_Set_BuildsCorrectUri()
    {
        var uri = RedisDsl.Set("user:123")
            .Connection(C("redis-server:6379"))
            .Password(C("secret"))
            .Database(1)
            .Ttl(300)
            .Build();

        uri.Should().StartWith("redis:SET:user:123?");
        uri.Should().Contain("ttl=300");
        uri.Should().Contain("database=1");
    }

    [Fact]
    public void FullChain_Stream_BuildsCorrectUri()
    {
        var uri = RedisDsl.XRead("order-stream")
            .Connection(C("redis:6379"))
            .ConsumerGroup(C("workers"))
            .ConsumerName(C("w1"))
            .StreamMaxLength(10000)
            .PollDelay(500)
            .Build();

        uri.Should().StartWith("redis:XREAD:order-stream?");
        uri.Should().Contain("consumerGroup=workers");
        uri.Should().Contain("consumerName=w1");
    }

    // ── Round-trip ──────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = RedisDsl.Set("k").Ttl(60).Database(2).Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("redis");
        parsed.RawParameters["ttl"].Should().Be("60");
        parsed.RawParameters["database"].Should().Be("2");
    }
}

using redb.Route.Core;
using redb.Route.RabbitMQ;
using redb.Route.Expressions;

namespace redb.Route.Tests.RabbitMQ;

public class RabbitBuilderTests
{
    private static ConstantExpression C(string s) => new(s);
    // ── Factory ─────────────────────────────────────────────────────

    [Fact]
    public void Queue_StartsWithRabbitmqScheme()
    {
        var uri = Rabbit.Queue("orders").Build();
        uri.Should().StartWith("rabbitmq:orders");
    }

    [Fact]
    public void NullQueue_Throws()
    {
        var act = () => Rabbit.Queue(null!);
        act.Should().Throw<ArgumentException>();
    }

    // ── Connection ──────────────────────────────────────────────────

    [Fact]
    public void Host_SetsParam()
    {
        var uri = Rabbit.Queue("q").Host(C("rabbit1")).Build();
        uri.Should().Contain("host=rabbit1");
    }

    [Fact]
    public void Port_SetsParam()
    {
        var uri = Rabbit.Queue("q").Port(5673).Build();
        uri.Should().Contain("port=5673");
    }

    [Fact]
    public void Username_SetsParam()
    {
        var uri = Rabbit.Queue("q").Username(C("admin")).Build();
        uri.Should().Contain("username=admin");
    }

    [Fact]
    public void VirtualHost_SetsParam()
    {
        var uri = Rabbit.Queue("q").VirtualHost(C("/staging")).Build();
        uri.Should().Contain("virtualHost=");
    }

    // ── Exchange ────────────────────────────────────────────────────

    [Fact]
    public void Exchange_SetsNameParam()
    {
        var uri = Rabbit.Queue("q").Exchange(C("events")).Build();
        uri.Should().Contain("exchange=events");
    }

    [Fact]
    public void Exchange_WithType_SetsBothParams()
    {
        var uri = Rabbit.Queue("q").Exchange(C("events"), "topic").Build();
        uri.Should().Contain("exchange=events");
        uri.Should().Contain("exchangeType=topic");
    }

    [Fact]
    public void Declare_SetsParam()
    {
        var uri = Rabbit.Queue("q").Declare().Build();
        uri.Should().Contain("declare=true");
    }

    // ── Queue ───────────────────────────────────────────────────────

    [Fact]
    public void RoutingKey_SetsParam()
    {
        var uri = Rabbit.Queue("q").RoutingKey(C("order.*")).Build();
        uri.Should().Contain("routingKey=order.*");
    }

    [Fact]
    public void AutoDelete_SetsParam()
    {
        var uri = Rabbit.Queue("q").AutoDelete().Build();
        uri.Should().Contain("autoDelete=true");
    }

    [Fact]
    public void Exclusive_SetsParam()
    {
        var uri = Rabbit.Queue("q").Exclusive().Build();
        uri.Should().Contain("exclusive=true");
    }

    // ── Consumer ────────────────────────────────────────────────────

    [Fact]
    public void ConcurrentConsumers_SetsParam()
    {
        var uri = Rabbit.Queue("q").ConcurrentConsumers(4).Build();
        uri.Should().Contain("concurrentConsumers=4");
    }

    [Fact]
    public void PrefetchCount_SetsParam()
    {
        var uri = Rabbit.Queue("q").PrefetchCount(50).Build();
        uri.Should().Contain("prefetchCount=50");
    }

    [Fact]
    public void Transacted_SetsParam()
    {
        var uri = Rabbit.Queue("q").Transacted().Build();
        uri.Should().Contain("transacted=true");
    }

    // ── Queue limits ────────────────────────────────────────────────

    [Fact]
    public void DeadLetterExchange_SetsParam()
    {
        var uri = Rabbit.Queue("q").DeadLetterExchange(C("dlx")).Build();
        uri.Should().Contain("deadLetterExchange=dlx");
    }

    [Fact]
    public void QueueType_SetsParam()
    {
        var uri = Rabbit.Queue("q").QueueType("quorum").Build();
        uri.Should().Contain("queueType=quorum");
    }

    // ── SSL ─────────────────────────────────────────────────────────

    [Fact]
    public void Ssl_SetsParam()
    {
        var uri = Rabbit.Queue("q").Ssl().Build();
        uri.Should().Contain("ssl=true");
    }

    [Fact]
    public void SslWithCert_SetsParams()
    {
        var uri = Rabbit.Queue("q").Ssl(C("rabbit.example.com"), C("/certs/client.pfx"), C("pass")).Build();
        uri.Should().Contain("ssl=true");
        uri.Should().Contain("sslServerName=rabbit.example.com");
        uri.Should().Contain("sslCertPath=");
    }

    // ── Conversion ──────────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = Rabbit.Queue("orders").Host(C("rabbit1")).Declare();
        uri.Should().StartWith("rabbitmq:orders?");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = Rabbit.Queue("q").Host(C("rabbit1")).Exchange(C("ex"), "topic");
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full chain ──────────────────────────────────────────────────

    [Fact]
    public void FullChain_BuildsCorrectUri()
    {
        var uri = Rabbit.Queue("orders")
            .Host(C("rabbit1"))
            .Port(5672)
            .Username(C("admin"))
            .Password(C("secret"))
            .Exchange(C("events"), "topic")
            .RoutingKey(C("order.*"))
            .Declare()
            .ConcurrentConsumers(4)
            .PrefetchCount(20)
            .Build();

        uri.Should().StartWith("rabbitmq:orders?");
        uri.Should().Contain("host=rabbit1");
        uri.Should().Contain("exchange=events");
        uri.Should().Contain("exchangeType=topic");
        uri.Should().Contain("routingKey=order.*");
        uri.Should().Contain("declare=true");
        uri.Should().Contain("concurrentConsumers=4");
    }

    // ── Round-trip ──────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = Rabbit.Queue("q").Host(C("rabbit1")).Exchange(C("ex")).Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("rabbitmq");
        parsed.Path.Should().Be("q");
        parsed.RawParameters["host"].Should().Be("rabbit1");
        parsed.RawParameters["exchange"].Should().Be("ex");
    }
}

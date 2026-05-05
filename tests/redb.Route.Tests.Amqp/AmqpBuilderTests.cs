using redb.Route.Core;
using redb.Route.Amqp;
using AmqpDsl = redb.Route.Amqp.Amqp;

namespace redb.Route.Tests.Amqp;

public class AmqpBuilderTests
{
    // ── Factory ─────────────────────────────────────────────────────

    [Fact]
    public void Address_StartsWithAmqpScheme()
    {
        var uri = AmqpDsl.Address("orders").Build();
        uri.Should().StartWith("amqp:orders");
    }

    [Fact]
    public void NullAddress_Throws()
    {
        var act = () => AmqpDsl.Address(null!);
        act.Should().Throw<ArgumentException>();
    }

    // ── Connection ──────────────────────────────────────────────────

    [Fact]
    public void Host_SetsParam()
    {
        var uri = AmqpDsl.Address("q").Host("broker1").Build();
        uri.Should().Contain("host=broker1");
    }

    [Fact]
    public void Port_SetsParam()
    {
        var uri = AmqpDsl.Address("q").Port(5673).Build();
        uri.Should().Contain("port=5673");
    }

    [Fact]
    public void User_SetsParam()
    {
        var uri = AmqpDsl.Address("q").User("admin").Build();
        uri.Should().Contain("user=admin");
    }

    [Fact]
    public void Password_SetsParam()
    {
        var uri = AmqpDsl.Address("q").Password("secret").Build();
        uri.Should().Contain("password=secret");
    }

    [Fact]
    public void Ssl_SetsParam()
    {
        var uri = AmqpDsl.Address("q").Ssl().Build();
        uri.Should().Contain("ssl=true");
    }

    [Fact]
    public void VirtualHost_SetsParam()
    {
        var uri = AmqpDsl.Address("q").VirtualHost("/test").Build();
        uri.Should().Contain("virtualHost=");
    }

    // ── Consumer ────────────────────────────────────────────────────

    [Fact]
    public void Credit_SetsParam()
    {
        var uri = AmqpDsl.Address("q").Credit(200).Build();
        uri.Should().Contain("credit=200");
    }

    [Fact]
    public void ConcurrentConsumers_SetsParam()
    {
        var uri = AmqpDsl.Address("q").ConcurrentConsumers(4).Build();
        uri.Should().Contain("concurrentConsumers=4");
    }

    [Fact]
    public void AutoAccept_False_SetsParam()
    {
        var uri = AmqpDsl.Address("q").AutoAccept(false).Build();
        uri.Should().Contain("autoAccept=false");
    }

    // ── Producer ────────────────────────────────────────────────────

    [Fact]
    public void MessageDurable_SetsParam()
    {
        var uri = AmqpDsl.Address("q").MessageDurable().Build();
        uri.Should().Contain("messageDurable=true");
    }

    [Fact]
    public void ContentType_SetsParam()
    {
        var uri = AmqpDsl.Address("q").ContentType("application/json").Build();
        uri.Should().Contain("contentType=application%2fjson");
    }

    [Fact]
    public void Transacted_SetsParam()
    {
        var uri = AmqpDsl.Address("q").Transacted().Build();
        uri.Should().Contain("transacted=true");
    }

    [Fact]
    public void Declare_SetsParam()
    {
        var uri = AmqpDsl.Address("q").Declare().Build();
        uri.Should().Contain("declare=true");
    }

    [Fact]
    public void RoutingType_SetsParam()
    {
        var uri = AmqpDsl.Address("q").RoutingType("MULTICAST").Build();
        uri.Should().Contain("routingType=MULTICAST");
    }

    // ── Conversion ──────────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = AmqpDsl.Address("notifications").Host("broker1");
        uri.Should().StartWith("amqp:notifications?");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = AmqpDsl.Address("q").Host("broker1").User("admin");
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full chain ──────────────────────────────────────────────────

    [Fact]
    public void FullChain_BuildsCorrectUri()
    {
        var uri = AmqpDsl.Address("orders")
            .Host("amqp.example.com")
            .Port(5672)
            .User("admin")
            .Password("secret")
            .ConcurrentConsumers(4)
            .Transacted()
            .Declare()
            .Build();

        uri.Should().StartWith("amqp:orders?");
        uri.Should().Contain("host=amqp.example.com");
        uri.Should().Contain("port=5672");
        uri.Should().Contain("concurrentConsumers=4");
        uri.Should().Contain("transacted=true");
        uri.Should().Contain("declare=true");
    }

    // ── Round-trip ──────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = AmqpDsl.Address("q").Host("broker1").Port(5672).Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("amqp");
        parsed.Path.Should().Be("q");
        parsed.RawParameters["host"].Should().Be("broker1");
        parsed.RawParameters["port"].Should().Be("5672");
    }
}

using redb.Route.Amqp;

namespace redb.Route.Tests.Amqp;

public sealed class AmqpEndpointOptionsTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var opts = new AmqpEndpointOptions();

        opts.Host.Should().Be("localhost");
        opts.Port.Should().Be(5672);
        opts.User.Should().BeNull();
        opts.Password.Should().BeNull();
        opts.Ssl.Should().BeFalse();
        opts.Durable.Should().Be(0u);
        opts.ExpiryPolicy.Should().Be("session-end");
        opts.DistributionMode.Should().BeNull();
        opts.Dynamic.Should().BeFalse();
        opts.FilterSelector.Should().BeNull();
        opts.Capabilities.Should().BeNull();
        opts.SenderSettleMode.Should().Be(2);
        opts.ReceiverSettleMode.Should().Be(0);
        opts.Credit.Should().Be(100);
        opts.AutoAccept.Should().BeTrue();
        opts.ConcurrentConsumers.Should().Be(1);
        opts.ReceiveTimeout.Should().Be(60);
        opts.MessageDurable.Should().BeTrue();
        opts.MessagePriority.Should().Be(4);
        opts.MessageTtl.Should().Be(0u);
        opts.ContentType.Should().Be("text/plain");
        opts.Subject.Should().BeNull();
        opts.GroupId.Should().BeNull();
        opts.ReplyTo.Should().BeFalse();
        opts.Timeout.Should().Be(30);
        opts.Transacted.Should().BeFalse();
        opts.Declare.Should().BeFalse();
        opts.RoutingType.Should().Be("ANYCAST");
    }

    [Fact]
    public void Validate_InvalidPort_Throws()
    {
        var opts = new AmqpEndpointOptions { Port = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*port*");
    }

    [Fact]
    public void Validate_PortTooHigh_Throws()
    {
        var opts = new AmqpEndpointOptions { Port = 99999 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*port*");
    }

    [Fact]
    public void Validate_NegativeCredit_Throws()
    {
        var opts = new AmqpEndpointOptions { Credit = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*credit*");
    }

    [Fact]
    public void Validate_ZeroConcurrentConsumers_Throws()
    {
        var opts = new AmqpEndpointOptions { ConcurrentConsumers = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*concurrentConsumers*");
    }

    [Fact]
    public void Validate_InvalidSenderSettleMode_Throws()
    {
        var opts = new AmqpEndpointOptions { SenderSettleMode = 5 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*SenderSettleMode*");
    }

    [Fact]
    public void Validate_InvalidReceiverSettleMode_Throws()
    {
        var opts = new AmqpEndpointOptions { ReceiverSettleMode = 3 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*ReceiverSettleMode*");
    }

    [Fact]
    public void Validate_InvalidDurable_Throws()
    {
        var opts = new AmqpEndpointOptions { Durable = 5 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*Durable*");
    }

    [Fact]
    public void Validate_ValidOptions_DoesNotThrow()
    {
        var opts = new AmqpEndpointOptions
        {
            Port = 5673,
            Credit = 50,
            ConcurrentConsumers = 4,
            SenderSettleMode = 1,
            ReceiverSettleMode = 1,
            Durable = 2,
        };

        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    // ── ResolveCapabilities ──

    [Fact]
    public void ResolveCapabilities_DefaultAnycast_IncludesQueue()
    {
        var opts = new AmqpEndpointOptions { RoutingType = "ANYCAST" };
        var caps = opts.ResolveCapabilities();
        caps.Should().Contain("queue");
    }

    [Fact]
    public void ResolveCapabilities_Multicast_IncludesTopic()
    {
        var opts = new AmqpEndpointOptions { RoutingType = "MULTICAST" };
        var caps = opts.ResolveCapabilities();
        caps.Should().Contain("topic");
    }

    [Fact]
    public void ResolveCapabilities_ExplicitCapabilities_Combined()
    {
        var opts = new AmqpEndpointOptions
        {
            Capabilities = "shared,global",
            RoutingType = "MULTICAST"
        };
        var caps = opts.ResolveCapabilities();
        caps.Should().Contain("shared");
        caps.Should().Contain("global");
        caps.Should().Contain("topic");
    }

    [Fact]
    public void ResolveCapabilities_NoDuplicateWhenCapMatchesRouting()
    {
        var opts = new AmqpEndpointOptions
        {
            Capabilities = "queue",
            RoutingType = "ANYCAST"
        };
        var caps = opts.ResolveCapabilities();
        caps.Count(c => c == "queue").Should().Be(1);
    }

    // ── ResolveExpiryPolicy ──

    [Fact]
    public void ResolveExpiryPolicy_SessionEnd_Default()
    {
        var opts = new AmqpEndpointOptions();
        var policy = opts.ResolveExpiryPolicy();
        policy.ToString().Should().Be("session-end");
    }

    [Theory]
    [InlineData("link-detach", "link-detach")]
    [InlineData("session-end", "session-end")]
    [InlineData("connection-close", "connection-close")]
    [InlineData("never", "never")]
    public void ResolveExpiryPolicy_MapsCorrectly(string input, string expected)
    {
        var opts = new AmqpEndpointOptions { ExpiryPolicy = input };
        opts.ResolveExpiryPolicy().ToString().Should().Be(expected);
    }

    [Fact]
    public void ResolveExpiryPolicy_Unknown_FallsBackToSessionEnd()
    {
        var opts = new AmqpEndpointOptions { ExpiryPolicy = "garbage" };
        opts.ResolveExpiryPolicy().ToString().Should().Be("session-end");
    }
}

using redb.Route.Core;
using redb.Route.RabbitMQ;

namespace redb.Route.Tests.RabbitMQ;

public sealed class RabbitMQEndpointOptionsTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var opts = new RabbitMQEndpointOptions();

        opts.Host.Should().Be("localhost");
        opts.Port.Should().Be(5672);
        opts.Username.Should().Be("guest");
        opts.Password.Should().Be("guest");
        opts.VirtualHost.Should().Be("/");
        opts.ClientName.Should().Be("redb.Route");
        opts.Exchange.Should().BeEmpty();
        opts.ExchangeType.Should().Be("direct");
        opts.ExchangeDurable.Should().BeTrue();
        opts.ExchangeAutoDelete.Should().BeFalse();
        opts.Declare.Should().BeFalse();
        opts.Durable.Should().BeTrue();
        opts.AutoDelete.Should().BeFalse();
        opts.Exclusive.Should().BeFalse();
        opts.RoutingKey.Should().BeEmpty();
        opts.ContentType.Should().Be("application/json");
        opts.ResolvedConcurrentConsumers.Should().Be(1);
        opts.PrefetchCount.Should().Be(10);
        opts.Transacted.Should().BeNull("unset, a producer follows the enclosing .Transacted() block");
        opts.ReplyTo.Should().BeFalse();
        opts.Timeout.Should().Be(60);
        opts.MessageTtl.Should().Be(0);
        opts.Expires.Should().Be(0);
        opts.AutomaticRecovery.Should().BeTrue();
        opts.RecoveryInterval.Should().Be(5);
        opts.Heartbeat.Should().Be(60);
    }

    [Fact]
    public void Validate_ZeroPrefetchCount_Throws()
    {
        var opts = new RabbitMQEndpointOptions { PrefetchCount = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*PrefetchCount*");
    }

    [Fact]
    public void Validate_ZeroConcurrentConsumers_Throws()
    {
        var opts = new RabbitMQEndpointOptions { ConcurrentConsumers = "0" };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*concurrentConsumers*");
    }

    [Fact]
    public void Validate_ZeroTimeout_Throws()
    {
        var opts = new RabbitMQEndpointOptions { Timeout = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Timeout*");
    }

    [Fact]
    public void Validate_ValidOptions_DoesNotThrow()
    {
        var opts = new RabbitMQEndpointOptions
        {
            PrefetchCount = 20,
            ConcurrentConsumers = "4",
            Timeout = 30
        };

        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Defaults_AckMode_IsManual()
    {
        new RabbitMQEndpointOptions().AckMode.Should().Be(AckMode.Manual);
    }

    [Fact]
    public void Validate_AckModeAutoWithTransacted_Throws()
    {
        var opts = new RabbitMQEndpointOptions { AckMode = AckMode.Auto, Transacted = true };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*ackMode=auto*Transacted*");
    }

    [Fact]
    public void Validate_AckModeAutoWithoutTransacted_DoesNotThrow()
    {
        var opts = new RabbitMQEndpointOptions { AckMode = AckMode.Auto };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("topic", false)]
    [InlineData("fanout", false)]
    [InlineData("direct", true)]
    [InlineData("headers", true)]
    public void ResolveMandatory_DefaultsByExchangeType(string exchangeType, bool expected)
    {
        var opts = new RabbitMQEndpointOptions { ExchangeType = exchangeType };
        opts.ResolveMandatory().Should().Be(expected);
    }

    [Fact]
    public void ResolveMandatory_ExplicitOverridesDefault()
    {
        var opts = new RabbitMQEndpointOptions { ExchangeType = "fanout", Mandatory = true };
        opts.ResolveMandatory().Should().BeTrue();
    }

    [Fact]
    public void BuildQueueArguments_EmptyByDefault()
    {
        var opts = new RabbitMQEndpointOptions();
        opts.BuildQueueArguments().Should().BeEmpty();
    }

    [Fact]
    public void BuildQueueArguments_SetsTtl()
    {
        var opts = new RabbitMQEndpointOptions { MessageTtl = 30000 };
        var args = opts.BuildQueueArguments();
        args.Should().ContainKey("x-message-ttl");
        args["x-message-ttl"].Should().Be(30000);
    }

    [Fact]
    public void BuildQueueArguments_SetsExpires()
    {
        var opts = new RabbitMQEndpointOptions { Expires = 60000 };
        var args = opts.BuildQueueArguments();
        args.Should().ContainKey("x-expires");
        args["x-expires"].Should().Be(60000);
    }

    [Fact]
    public void BuildQueueArguments_CombinesMultiple()
    {
        var opts = new RabbitMQEndpointOptions { MessageTtl = 10000, Expires = 50000 };
        var args = opts.BuildQueueArguments();
        args.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("exchangeType=derict", "exchangeType", "direct, topic, fanout, headers")]
    [InlineData("queueType=clasic", "queueType", "classic, quorum, stream")]
    [InlineData("overflow=drophEad", "overflow", "drop-head, reject-publish, reject-publish-dlx")]
    public void Validate_unknown_broker_value_fails_naming_the_option_and_the_allowed_values(string query, string option, string allowed)
    {
        var act = () => Bind(query).Validate();

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain($"'{option}'").And.Contain(allowed);
    }

    [Theory]
    [InlineData("exchangeType=headers")]
    [InlineData("exchangeType=x-delayed-message")]
    [InlineData("exchangeType=x-consistent-hash")]
    [InlineData("queueType=stream")]
    [InlineData("queueType=quorum")]
    [InlineData("overflow=reject-publish-dlx")]
    public void Validate_accepts_every_broker_value_and_plugin_exchange_types(string query)
    {
        var act = () => Bind(query).Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_writes_a_built_in_exchange_type_the_way_the_broker_names_it()
    {
        // The broker's exchange types are case-sensitive; the built-in names are unambiguous in any case.
        var opts = Bind("exchangeType=Topic");

        opts.Validate();

        opts.ExchangeType.Should().Be("topic");
    }

    private static RabbitMQEndpointOptions Bind(string query)
    {
        var opts = new RabbitMQEndpointOptions();
        opts.BindFromUri(EndpointUriParser.Parse($"rabbitmq://q?{query}").RawParameters);
        return opts;
    }
}

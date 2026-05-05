using redb.Route.Amqp;

namespace redb.Route.Tests.Amqp;

public sealed class AmqpConnectionFactoryTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var factory = new AmqpConnectionFactory();

        factory.Host.Should().Be("localhost");
        factory.Port.Should().Be(5672);
        factory.User.Should().BeNull();
        factory.Password.Should().BeNull();
        factory.ContainerId.Should().BeNull();
        factory.VirtualHost.Should().BeNull();
        factory.IdleTimeout.Should().Be(120_000);
        factory.MaxFrameSize.Should().Be(256 * 1024);
        factory.MaxSessions.Should().Be(8);
        factory.NoDelay.Should().BeTrue();
        factory.KeepAlive.Should().BeFalse();
        factory.Ssl.Should().BeFalse();
        factory.SaslMechanism.Should().Be(SaslMechanism.Auto);
        factory.Reconnect.Should().BeFalse();
        factory.ReconnectInterval.Should().Be(5000);
        factory.MaxReconnectAttempts.Should().Be(0);
    }

    [Fact]
    public void Build_ReturnsConfiguredConnectionFactory()
    {
        var factory = new AmqpConnectionFactory
        {
            Host = "broker-host",
            Port = 5673,
            User = "admin",
            Password = "secret",
            VirtualHost = "/myVhost",
            IdleTimeout = 60_000,
            MaxFrameSize = 512 * 1024,
            MaxSessions = 16,
        };

        var result = factory.Build();

        result.Should().NotBeNull();
        result.AMQP.HostName.Should().Be("/myVhost");
        result.AMQP.IdleTimeout.Should().Be(60_000);
        result.AMQP.MaxFrameSize.Should().Be(512 * 1024);
        result.AMQP.MaxSessionsPerConnection.Should().Be(16);
    }

    [Fact]
    public void Build_WithKeepAlive_ConfiguresTcpKeepAlive()
    {
        var factory = new AmqpConnectionFactory { KeepAlive = true };
        var result = factory.Build();
        result.TCP.KeepAlive.Should().NotBeNull();
    }

    [Fact]
    public void Build_NoKeepAlive_TcpKeepAliveIsNull()
    {
        var factory = new AmqpConnectionFactory { KeepAlive = false };
        var result = factory.Build();
        result.TCP.KeepAlive.Should().BeNull();
    }

    [Fact]
    public void Build_WithNoDelay_ConfiguresTcpNoDelay()
    {
        var factory = new AmqpConnectionFactory { NoDelay = true };
        var result = factory.Build();
        result.TCP.NoDelay.Should().BeTrue();
    }

    [Fact]
    public void Build_GeneratesContainerId_WhenNotSet()
    {
        var factory = new AmqpConnectionFactory();
        var result = factory.Build();
        result.AMQP.ContainerId.Should().StartWith("redb-route-");
    }

    [Fact]
    public void Build_UsesExplicitContainerId()
    {
        var factory = new AmqpConnectionFactory { ContainerId = "my-container" };
        var result = factory.Build();
        result.AMQP.ContainerId.Should().Be("my-container");
    }

    [Fact]
    public void Build_UsesClientNameAsContainerIdFallback()
    {
        var factory = new AmqpConnectionFactory { ClientName = "my-app" };
        var result = factory.Build();
        result.AMQP.ContainerId.Should().Be("my-app");
    }

    [Fact]
    public void Build_ContainerIdTakesPriorityOverClientName()
    {
        var factory = new AmqpConnectionFactory { ContainerId = "container-1", ClientName = "app-1" };
        var result = factory.Build();
        result.AMQP.ContainerId.Should().Be("container-1");
    }

    [Fact]
    public void BuildAddress_DefaultAmqp()
    {
        var factory = new AmqpConnectionFactory();
        var addr = factory.BuildAddress();
        addr.Scheme.Should().BeOneOf("amqp", "AMQP");
        addr.Host.Should().Be("localhost");
        addr.Port.Should().Be(5672);
    }

    [Fact]
    public void BuildAddress_WithSsl_ReturnsAmqps()
    {
        var factory = new AmqpConnectionFactory { Ssl = true, Port = 5671 };
        var addr = factory.BuildAddress();
        addr.Scheme.Should().BeOneOf("amqps", "AMQPS");
        addr.Port.Should().Be(5671);
    }

    [Fact]
    public void BuildAddress_WithCredentials()
    {
        var factory = new AmqpConnectionFactory { User = "admin", Password = "pass" };
        var addr = factory.BuildAddress();
        addr.User.Should().Be("admin");
        addr.Password.Should().Be("pass");
    }

    [Fact]
    public void GetAddresses_SingleHost_ReturnsOneAddress()
    {
        var factory = new AmqpConnectionFactory { Host = "broker1", Port = 5672 };
        var addrs = factory.GetAddresses();
        addrs.Should().HaveCount(1);
        addrs[0].Host.Should().Be("broker1");
        addrs[0].Port.Should().Be(5672);
    }

    [Fact]
    public void GetAddresses_ClusterHosts_ReturnsMultiple()
    {
        var factory = new AmqpConnectionFactory { Host = "b1, b2, b3", Port = 5672 };
        var addrs = factory.GetAddresses();
        addrs.Should().HaveCount(3);
        addrs[0].Host.Should().Be("b1");
        addrs[1].Host.Should().Be("b2");
        addrs[2].Host.Should().Be("b3");
    }

    [Fact]
    public void GetAddresses_WithSsl_AllUseAmqps()
    {
        var factory = new AmqpConnectionFactory { Host = "a, b", Port = 5671, Ssl = true };
        var addrs = factory.GetAddresses();
        addrs.Should().HaveCount(2);
        foreach (var addr in addrs)
            addr.Scheme.Should().BeOneOf("amqps", "AMQPS");
    }

    [Fact]
    public void Build_WithSslEnabled_DoesNotThrow()
    {
        var factory = new AmqpConnectionFactory
        {
            Ssl = true,
            SkipServerCertValidation = true,
        };

        var act = () => factory.Build();
        act.Should().NotThrow();
    }

    [Fact]
    public void Build_WithBufferSizes_ConfiguresTcpBuffers()
    {
        var factory = new AmqpConnectionFactory
        {
            SendBufferSize = 65536,
            ReceiveBufferSize = 131072,
            SendTimeout = 5000,
            ReceiveTimeout = 10000,
        };

        var result = factory.Build();
        result.TCP.SendBufferSize.Should().Be(65536);
        result.TCP.ReceiveBufferSize.Should().Be(131072);
        result.TCP.SendTimeout.Should().Be(5000);
        result.TCP.ReceiveTimeout.Should().Be(10000);
    }
}

using redb.Route.RabbitMQ;

namespace redb.Route.Tests.RabbitMQ;

public sealed class RabbitMQConnectionFactoryTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var factory = new RabbitMQConnectionFactory();

        factory.Host.Should().Be("localhost");
        factory.Port.Should().Be(5672);
        factory.Username.Should().Be("guest");
        factory.Password.Should().Be("guest");
        factory.VirtualHost.Should().Be("/");
        factory.AutomaticRecovery.Should().BeTrue();
        factory.RecoveryInterval.Should().Be(5);
        factory.Heartbeat.Should().Be(60);
        factory.ClientName.Should().Be("redb.Route");
    }

    [Fact]
    public void Build_ReturnsConfiguredConnectionFactory()
    {
        var factory = new RabbitMQConnectionFactory
        {
            Host = "rabbit-host",
            Port = 5673,
            Username = "admin",
            Password = "secret",
            VirtualHost = "/myVhost"
        };

        var result = factory.Build();

        result.UserName.Should().Be("admin");
        result.Password.Should().Be("secret");
        result.VirtualHost.Should().Be("/myVhost");
        result.AutomaticRecoveryEnabled.Should().BeTrue();
    }

    [Fact]
    public void GetEndpoints_SingleHost_ReturnsOneEndpoint()
    {
        var factory = new RabbitMQConnectionFactory { Host = "rabbit1", Port = 5672 };
        var endpoints = factory.GetEndpoints();
        endpoints.Should().HaveCount(1);
        endpoints[0].HostName.Should().Be("rabbit1");
        endpoints[0].Port.Should().Be(5672);
    }

    [Fact]
    public void GetEndpoints_ClusterHosts_ReturnsMultiple()
    {
        var factory = new RabbitMQConnectionFactory { Host = "r1, r2, r3", Port = 5672 };
        var endpoints = factory.GetEndpoints();
        endpoints.Should().HaveCount(3);
        endpoints[0].HostName.Should().Be("r1");
        endpoints[1].HostName.Should().Be("r2");
        endpoints[2].HostName.Should().Be("r3");
    }

    [Fact]
    public void Build_SetsHeartbeatAndRecovery()
    {
        var factory = new RabbitMQConnectionFactory
        {
            Heartbeat = 30,
            RecoveryInterval = 10,
            AutomaticRecovery = false
        };

        var result = factory.Build();

        result.RequestedHeartbeat.Should().Be(TimeSpan.FromSeconds(30));
        result.NetworkRecoveryInterval.Should().Be(TimeSpan.FromSeconds(10));
        result.AutomaticRecoveryEnabled.Should().BeFalse();
    }
}

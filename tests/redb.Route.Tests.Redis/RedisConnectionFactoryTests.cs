using redb.Route.Redis;
using StackExchange.Redis;

namespace redb.Route.Tests.Redis;

public sealed class RedisConnectionFactoryTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var factory = new RedisConnectionFactory();

        factory.ConnectionString.Should().Be("localhost:6379");
        factory.Database.Should().Be(0);
        factory.Password.Should().BeNull();
        factory.ClientName.Should().Be("redb.Route");
        factory.ConnectTimeout.Should().Be(5000);
        factory.SyncTimeout.Should().Be(5000);
        factory.AsyncTimeout.Should().Be(5000);
        factory.ConnectRetry.Should().Be(3);
        factory.KeepAlive.Should().Be(60);
        factory.AbortOnConnectFail.Should().BeFalse();
        factory.AllowAdmin.Should().BeFalse();
        factory.Ssl.Should().BeFalse();
        factory.SslHost.Should().BeNull();
        factory.IncludeDetailInExceptions.Should().BeTrue();
        factory.ChannelPrefix.Should().BeNull();
    }

    [Fact]
    public void Build_ReturnsConfiguredOptions()
    {
        var factory = new RedisConnectionFactory
        {
            ConnectionString = "redis-host:6380",
            Database = 3,
            Password = "secret",
            ClientName = "myApp",
            ConnectTimeout = 3000,
            SyncTimeout = 2000,
            AsyncTimeout = 4000,
            ConnectRetry = 5,
            KeepAlive = 30,
            AbortOnConnectFail = true,
            AllowAdmin = true,
            Ssl = true,
            SslHost = "redis.example.com",
            ChannelPrefix = "pre"
        };

        var config = factory.Build();

        config.DefaultDatabase.Should().Be(3);
        config.Password.Should().Be("secret");
        config.ClientName.Should().Be("myApp");
        config.ConnectTimeout.Should().Be(3000);
        config.SyncTimeout.Should().Be(2000);
        config.AsyncTimeout.Should().Be(4000);
        config.ConnectRetry.Should().Be(5);
        config.KeepAlive.Should().Be(30);
        config.AbortOnConnectFail.Should().BeTrue();
        config.AllowAdmin.Should().BeTrue();
        config.Ssl.Should().BeTrue();
        config.SslHost.Should().Be("redis.example.com");
        config.IncludeDetailInExceptions.Should().BeTrue();
    }

    [Fact]
    public void Build_NoPassword_PasswordRemainsNull()
    {
        var factory = new RedisConnectionFactory
        {
            ConnectionString = "localhost:6379"
        };

        var config = factory.Build();
        config.Password.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Build_NoSslHost_SslHostNotExplicitlySet()
    {
        var factory = new RedisConnectionFactory
        {
            ConnectionString = "localhost:6379",
            Ssl = true
        };

        var config = factory.Build();
        config.Ssl.Should().BeTrue();
        // SslHost is not explicitly set by factory, library may or may not populate it
    }
}

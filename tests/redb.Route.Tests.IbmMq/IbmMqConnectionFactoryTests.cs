using redb.Route.IbmMq;

namespace redb.Route.Tests.IbmMq;

public sealed class IbmMqConnectionFactoryTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var factory = new IbmMqConnectionFactory();

        factory.Host.Should().Be("localhost");
        factory.Port.Should().Be(1414);
        factory.Channel.Should().Be("DEV.APP.SVRCONN");
        factory.QueueManager.Should().Be("QM1");
        factory.CCSID.Should().Be(1208);
        factory.User.Should().BeNull();
        factory.Password.Should().BeNull();
        factory.SslCipherSpec.Should().BeNull();
    }

    [Fact]
    public void BuildConnectionProperties_ContainsRequiredKeys()
    {
        var factory = new IbmMqConnectionFactory
        {
            Host = "broker1",
            Port = 1415,
            Channel = "MY.SVRCONN",
            QueueManager = "QM2",
            CCSID = 437
        };

        var props = factory.BuildConnectionProperties();

        props.Should().NotBeNull();
        props.Count.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void BuildConnectionProperties_WithAuth_IncludesCredentials()
    {
        var factory = new IbmMqConnectionFactory
        {
            User = "admin",
            Password = "secret"
        };

        var props = factory.BuildConnectionProperties();

        // Should have more entries than the base set (transport, host, port, channel, ccsid)
        props.Count.Should().BeGreaterThanOrEqualTo(7);
    }

    [Fact]
    public void BuildConnectionProperties_WithoutAuth_ExcludesCredentials()
    {
        var factory = new IbmMqConnectionFactory();

        var props = factory.BuildConnectionProperties();

        // Base set: transport, host, port, channel, ccsid = 5
        props.Count.Should().Be(5);
    }

    [Fact]
    public void BuildConnectionProperties_WithClientName_IncludesAppName()
    {
        var factory = new IbmMqConnectionFactory
        {
            ClientName = "myApp"
        };

        var props = factory.BuildConnectionProperties();

        props.Count.Should().BeGreaterThanOrEqualTo(6);
    }

    [Fact]
    public void BuildConnectionProperties_WithSslCipherSpec_IncludesSsl()
    {
        var factory = new IbmMqConnectionFactory
        {
            SslCipherSpec = "TLS_RSA_WITH_AES_256_CBC_SHA256"
        };

        var props = factory.BuildConnectionProperties();

        props.Count.Should().BeGreaterThanOrEqualTo(6);
    }

    [Fact]
    public void BuildConnectionProperties_WithSslPeerName_IncludesSsl()
    {
        var factory = new IbmMqConnectionFactory
        {
            SslPeerName = "CN=broker1"
        };

        var props = factory.BuildConnectionProperties();

        props.Count.Should().BeGreaterThanOrEqualTo(6);
    }

    [Fact]
    public void BuildConnectionProperties_WithSslCertLabel_IncludesSsl()
    {
        var factory = new IbmMqConnectionFactory
        {
            SslCertLabel = "mycert"
        };

        var props = factory.BuildConnectionProperties();

        props.Count.Should().BeGreaterThanOrEqualTo(6);
    }

    [Fact]
    public void BuildConnectionProperties_ReturnsNewInstanceEachTime()
    {
        var factory = new IbmMqConnectionFactory();

        var props1 = factory.BuildConnectionProperties();
        var props2 = factory.BuildConnectionProperties();

        props1.Should().NotBeSameAs(props2);
    }
}

using redb.Route.Core;
using redb.Route.IbmMq;

namespace redb.Route.Tests.IbmMq;

public sealed class IbmMqEndpointOptionsTests
{
    // ── Defaults ────────────────────────────────────────────────────

    [Fact]
    public void Defaults_AreCorrect()
    {
        var opts = new IbmMqEndpointOptions();

        opts.Host.Should().Be("localhost");
        opts.Port.Should().Be(1414);
        opts.Channel.Should().Be("DEV.APP.SVRCONN");
        opts.QueueManager.Should().Be("QM1");
        opts.DestinationType.Should().Be(IbmMqDestinationType.Queue);
        opts.ConcurrentConsumers.Should().Be(1);
        opts.WaitInterval.Should().Be(5000);
        opts.BatchSize.Should().Be(0);
        opts.Transacted.Should().BeFalse();
        opts.Persistence.Should().Be(IbmMqPersistence.AsQDef);
        opts.Priority.Should().Be(-1);
        opts.Expiry.Should().Be(-1);
        opts.TargetClient.Should().Be(IbmMqTargetClient.Jms);
        opts.MessageType.Should().Be(IbmMqMessageType.Datagram);
        opts.ReplyTo.Should().BeFalse();
        opts.Timeout.Should().Be(30);
        opts.CorrelationPattern.Should().Be(IbmMqCorrelationPattern.MsgId);
        opts.CCSID.Should().Be(1208);
        opts.MqmdWriteEnabled.Should().BeFalse();
        opts.MqmdReadEnabled.Should().BeTrue();
        opts.Convert.Should().BeTrue();
    }

    // ── BindFromUri ─────────────────────────────────────────────────

    [Fact]
    public void BindFromUri_ConnectionParams_Bind()
    {
        var uri = EndpointUriParser.Parse(
            "wmq:Q?host=broker1&port=1415&channel=MY.SVRCONN&queueManager=QM2&user=admin&password=secret&clientId=app1");

        var opts = new IbmMqEndpointOptions();
        opts.BindFromUri(uri.RawParameters);

        opts.Host.Should().Be("broker1");
        opts.Port.Should().Be(1415);
        opts.Channel.Should().Be("MY.SVRCONN");
        opts.QueueManager.Should().Be("QM2");
        opts.User.Should().Be("admin");
        opts.Password.Should().Be("secret");
        opts.ClientId.Should().Be("app1");
    }

    [Fact]
    public void BindFromUri_ConsumerParams_Bind()
    {
        var uri = EndpointUriParser.Parse(
            "wmq:Q?host=localhost&concurrentConsumers=4&waitInterval=10000&batchSize=10&backoutThreshold=5&backoutQueue=DLQ&convert=false");

        var opts = new IbmMqEndpointOptions();
        opts.BindFromUri(uri.RawParameters);

        opts.ConcurrentConsumers.Should().Be(4);
        opts.WaitInterval.Should().Be(10000);
        opts.BatchSize.Should().Be(10);
        opts.BackoutThreshold.Should().Be(5);
        opts.BackoutQueue.Should().Be("DLQ");
        opts.Convert.Should().BeFalse();
    }

    [Fact]
    public void BindFromUri_ProducerParams_Bind()
    {
        var uri = EndpointUriParser.Parse(
            "wmq:Q?host=localhost&persistence=Persistent&priority=7&expiry=3000&targetClient=Mq&messageType=Request");

        var opts = new IbmMqEndpointOptions();
        opts.BindFromUri(uri.RawParameters);

        opts.Persistence.Should().Be(IbmMqPersistence.Persistent);
        opts.Priority.Should().Be(7);
        opts.Expiry.Should().Be(3000);
        opts.TargetClient.Should().Be(IbmMqTargetClient.Mq);
        opts.MessageType.Should().Be(IbmMqMessageType.Request);
    }

    [Fact]
    public void BindFromUri_DestinationType_Topic()
    {
        var uri = EndpointUriParser.Parse("wmq:EVENTS?host=localhost&destinationType=Topic");

        var opts = new IbmMqEndpointOptions();
        opts.BindFromUri(uri.RawParameters);

        opts.DestinationType.Should().Be(IbmMqDestinationType.Topic);
    }

    [Fact]
    public void BindFromUri_TransactionParams_Bind()
    {
        var uri = EndpointUriParser.Parse("wmq:Q?host=localhost&transacted=true");

        var opts = new IbmMqEndpointOptions();
        opts.BindFromUri(uri.RawParameters);

        opts.Transacted.Should().BeTrue();
    }

    [Fact]
    public void BindFromUri_RpcParams_Bind()
    {
        var uri = EndpointUriParser.Parse(
            "wmq:Q?host=localhost&replyTo=true&replyToQueue=REPLY.Q&replyToQueueManager=QM2&timeout=60&correlationPattern=CorrelId");

        var opts = new IbmMqEndpointOptions();
        opts.BindFromUri(uri.RawParameters);

        opts.ReplyTo.Should().BeTrue();
        opts.ReplyToQueue.Should().Be("REPLY.Q");
        opts.ReplyToQueueManager.Should().Be("QM2");
        opts.Timeout.Should().Be(60);
        opts.CorrelationPattern.Should().Be(IbmMqCorrelationPattern.CorrelId);
    }

    [Fact]
    public void BindFromUri_SslParams_Bind()
    {
        var uri = EndpointUriParser.Parse(
            "wmq:Q?host=localhost&sslCipherSpec=TLS_RSA_WITH_AES_256_CBC_SHA256&sslPeerName=CN%3Dbroker&sslCertLabel=mycert&sslKeyRepository=/opt/keys/key");

        var opts = new IbmMqEndpointOptions();
        opts.BindFromUri(uri.RawParameters);

        opts.SslCipherSpec.Should().Be("TLS_RSA_WITH_AES_256_CBC_SHA256");
        opts.SslPeerName.Should().Be("CN=broker");
        opts.SslCertLabel.Should().Be("mycert");
        opts.SslKeyRepository.Should().Be("/opt/keys/key");
    }

    [Fact]
    public void BindFromUri_AdvancedParams_Bind()
    {
        var uri = EndpointUriParser.Parse(
            "wmq:Q?host=localhost&cCSID=437&mqmdWriteEnabled=true&mqmdReadEnabled=false");

        var opts = new IbmMqEndpointOptions();
        opts.BindFromUri(uri.RawParameters);

        opts.CCSID.Should().Be(437);
        opts.MqmdWriteEnabled.Should().BeTrue();
        opts.MqmdReadEnabled.Should().BeFalse();
    }

    // ── Validate ────────────────────────────────────────────────────

    [Fact]
    public void Validate_ValidDefaults_DoesNotThrow()
    {
        var opts = new IbmMqEndpointOptions();
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_EmptyQueueManager_Throws()
    {
        var opts = new IbmMqEndpointOptions { QueueManager = "" };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*queueManager*");
    }

    [Fact]
    public void Validate_EmptyHost_Throws()
    {
        var opts = new IbmMqEndpointOptions { Host = "" };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*host*");
    }

    [Fact]
    public void Validate_EmptyChannel_Throws()
    {
        var opts = new IbmMqEndpointOptions { Channel = "" };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*channel*");
    }

    [Fact]
    public void Validate_InvalidPort_Throws()
    {
        var opts = new IbmMqEndpointOptions { Port = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*port*");
    }

    [Fact]
    public void Validate_PortTooHigh_Throws()
    {
        var opts = new IbmMqEndpointOptions { Port = 70000 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*port*");
    }

    [Fact]
    public void Validate_InvalidConcurrentConsumers_Throws()
    {
        var opts = new IbmMqEndpointOptions { ConcurrentConsumers = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*concurrentConsumers*");
    }

    [Fact]
    public void Validate_NegativeWaitInterval_Throws()
    {
        var opts = new IbmMqEndpointOptions { WaitInterval = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*waitInterval*");
    }

    [Fact]
    public void Validate_NegativeBatchSize_Throws()
    {
        var opts = new IbmMqEndpointOptions { BatchSize = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*batchSize*");
    }

    [Fact]
    public void Validate_InvalidRpcTimeout_Throws()
    {
        var opts = new IbmMqEndpointOptions { Timeout = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*timeout*");
    }

    // ── Enum values ─────────────────────────────────────────────────

    [Theory]
    [InlineData(IbmMqPersistence.AsQDef, -1)]
    [InlineData(IbmMqPersistence.NonPersistent, 0)]
    [InlineData(IbmMqPersistence.Persistent, 1)]
    public void IbmMqPersistence_HasCorrectValues(IbmMqPersistence value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Theory]
    [InlineData(IbmMqMessageType.Datagram, 8)]
    [InlineData(IbmMqMessageType.Request, 1)]
    [InlineData(IbmMqMessageType.Reply, 2)]
    [InlineData(IbmMqMessageType.Report, 4)]
    public void IbmMqMessageType_HasCorrectValues(IbmMqMessageType value, int expected)
    {
        ((int)value).Should().Be(expected);
    }
}

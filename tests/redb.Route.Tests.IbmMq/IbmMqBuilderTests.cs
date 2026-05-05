using redb.Route.Core;
using redb.Route.IbmMq;
using IbmMqDsl = redb.Route.IbmMq.Wmq;

namespace redb.Route.Tests.IbmMq;

public sealed class IbmMqBuilderTests
{
    // ── Factory ─────────────────────────────────────────────────────

    [Fact]
    public void Queue_StartsWithIbmMqScheme()
    {
        var uri = IbmMqDsl.Queue("DEV.QUEUE.1").Build();
        uri.Should().StartWith("wmq:DEV.QUEUE.1");
    }

    [Fact]
    public void Topic_SetsDestinationType()
    {
        var uri = IbmMqDsl.Topic("EVENTS/ORDER").Build();
        uri.Should().StartWith("wmq:EVENTS/ORDER");
        uri.Should().Contain("destinationType=Topic");
    }

    [Fact]
    public void Queue_DoesNotSetDestinationType()
    {
        var uri = IbmMqDsl.Queue("Q1").Build();
        uri.Should().NotContain("destinationType");
    }

    [Fact]
    public void NullDestination_Throws()
    {
        var act = () => IbmMqDsl.Queue(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EmptyDestination_Throws()
    {
        var act = () => IbmMqDsl.Queue("");
        act.Should().Throw<ArgumentException>();
    }

    // ── Connection ──────────────────────────────────────────────────

    [Fact]
    public void Host_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").Host("broker1").Build();
        uri.Should().Contain("host=broker1");
    }

    [Fact]
    public void Port_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").Port(1415).Build();
        uri.Should().Contain("port=1415");
    }

    [Fact]
    public void Channel_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").Channel("MY.SVRCONN").Build();
        uri.Should().Contain("channel=MY.SVRCONN");
    }

    [Fact]
    public void QueueManager_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").QueueManager("QM2").Build();
        uri.Should().Contain("queueManager=QM2");
    }

    [Fact]
    public void User_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").User("admin").Build();
        uri.Should().Contain("user=admin");
    }

    [Fact]
    public void Password_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").Password("secret").Build();
        uri.Should().Contain("password=secret");
    }

    [Fact]
    public void ClientId_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").ClientId("app1").Build();
        uri.Should().Contain("clientId=app1");
    }

    [Fact]
    public void ConnectionFactory_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").ConnectionFactory("myFactory").Build();
        uri.Should().Contain("connectionFactory=myFactory");
    }

    // ── Consumer ────────────────────────────────────────────────────

    [Fact]
    public void ConcurrentConsumers_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").ConcurrentConsumers(4).Build();
        uri.Should().Contain("concurrentConsumers=4");
    }

    [Fact]
    public void WaitInterval_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").WaitInterval(10000).Build();
        uri.Should().Contain("waitInterval=10000");
    }

    [Fact]
    public void BatchSize_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").BatchSize(50).Build();
        uri.Should().Contain("batchSize=50");
    }

    [Fact]
    public void BackoutThreshold_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").BackoutThreshold(5).Build();
        uri.Should().Contain("backoutThreshold=5");
    }

    [Fact]
    public void BackoutQueue_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").BackoutQueue("DLQ").Build();
        uri.Should().Contain("backoutQueue=DLQ");
    }

    [Fact]
    public void Selector_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").Selector("priority > 5").Build();
        uri.Should().Contain("selector=");
    }

    [Fact]
    public void Convert_False_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").Convert(false).Build();
        uri.Should().Contain("convert=false");
    }

    // ── Producer ────────────────────────────────────────────────────

    [Fact]
    public void Persistent_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").Persistent().Build();
        uri.Should().Contain("persistence=Persistent");
    }

    [Fact]
    public void NonPersistent_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").NonPersistent().Build();
        uri.Should().Contain("persistence=NonPersistent");
    }

    [Fact]
    public void Priority_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").Priority(7).Build();
        uri.Should().Contain("priority=7");
    }

    [Fact]
    public void Expiry_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").Expiry(3000).Build();
        uri.Should().Contain("expiry=3000");
    }

    [Fact]
    public void TargetClient_Mq_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").TargetClient(IbmMqTargetClient.Mq).Build();
        uri.Should().Contain("targetClient=Mq");
    }

    [Fact]
    public void MessageType_Request_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").MessageType(IbmMqMessageType.Request).Build();
        uri.Should().Contain("messageType=Request");
    }

    // ── Transactions ────────────────────────────────────────────────

    [Fact]
    public void Transacted_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").Transacted().Build();
        uri.Should().Contain("transacted=true");
    }

    // ── Dead Letter ─────────────────────────────────────────────────

    [Fact]
    public void DeadLetterQueue_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").DeadLetterQueue("DLQ").Build();
        uri.Should().Contain("deadLetterQueue=DLQ");
    }

    [Fact]
    public void MaxRedeliveries_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").MaxRedeliveries(3).Build();
        uri.Should().Contain("maxRedeliveries=3");
    }

    // ── RPC ─────────────────────────────────────────────────────────

    [Fact]
    public void ReplyTo_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").ReplyTo().Build();
        uri.Should().Contain("replyTo=true");
    }

    [Fact]
    public void ReplyToQueue_SetsReplyToAndQueue()
    {
        var uri = IbmMqDsl.Queue("Q").ReplyToQueue("REPLY.Q").Build();
        uri.Should().Contain("replyTo=true");
        uri.Should().Contain("replyToQueue=REPLY.Q");
    }

    [Fact]
    public void ReplyToQueueManager_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").ReplyToQueueManager("QM2").Build();
        uri.Should().Contain("replyToQueueManager=QM2");
    }

    [Fact]
    public void Timeout_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").Timeout(60).Build();
        uri.Should().Contain("timeout=60");
    }

    [Fact]
    public void CorrelationPattern_CorrelId_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").CorrelationPattern(IbmMqCorrelationPattern.CorrelId).Build();
        uri.Should().Contain("correlationPattern=CorrelId");
    }

    // ── SSL ─────────────────────────────────────────────────────────

    [Fact]
    public void SslCipherSpec_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").SslCipherSpec("TLS_RSA_WITH_AES_256_CBC_SHA256").Build();
        uri.Should().Contain("sslCipherSpec=TLS_RSA_WITH_AES_256_CBC_SHA256");
    }

    [Fact]
    public void SslCertLabel_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").SslCertLabel("mycert").Build();
        uri.Should().Contain("sslCertLabel=mycert");
    }

    [Fact]
    public void SslPeerName_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").SslPeerName("CN=broker1").Build();
        uri.Should().Contain("sslPeerName=");
    }

    [Fact]
    public void SslKeyRepository_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").SslKeyRepository("/opt/keys/key").Build();
        uri.Should().Contain("sslKeyRepository=");
    }

    [Fact]
    public void SslKeyResetCount_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").SslKeyResetCount(1000000).Build();
        uri.Should().Contain("sslKeyResetCount=1000000");
    }

    // ── Advanced ────────────────────────────────────────────────────

    [Fact]
    public void CCSID_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").CCSID(437).Build();
        uri.Should().Contain("cCSID=437");
    }

    [Fact]
    public void MqmdWriteEnabled_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").MqmdWriteEnabled().Build();
        uri.Should().Contain("mqmdWriteEnabled=true");
    }

    [Fact]
    public void MqmdReadEnabled_False_SetsParam()
    {
        var uri = IbmMqDsl.Queue("Q").MqmdReadEnabled(false).Build();
        uri.Should().Contain("mqmdReadEnabled=false");
    }

    // ── Conversion ──────────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = IbmMqDsl.Queue("DEV.QUEUE.1").Host("broker1");
        uri.Should().StartWith("wmq:DEV.QUEUE.1?");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = IbmMqDsl.Queue("Q").Host("broker1").User("admin");
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full chain ──────────────────────────────────────────────────

    [Fact]
    public void FullChain_BuildsCorrectUri()
    {
        var uri = IbmMqDsl.Queue("ORDERS")
            .Host("mq.example.com")
            .Port(1414)
            .Channel("APP.SVRCONN")
            .QueueManager("QM1")
            .User("app")
            .Password("passw0rd")
            .ConcurrentConsumers(4)
            .Transacted()
            .Persistent()
            .Build();

        uri.Should().StartWith("wmq:ORDERS?");
        uri.Should().Contain("host=mq.example.com");
        uri.Should().Contain("port=1414");
        uri.Should().Contain("channel=APP.SVRCONN");
        uri.Should().Contain("queueManager=QM1");
        uri.Should().Contain("concurrentConsumers=4");
        uri.Should().Contain("transacted=true");
        uri.Should().Contain("persistence=Persistent");
    }

    // ── Round-trip ──────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = IbmMqDsl.Queue("Q1").Host("broker1").Port(1414).Build();
        var parsed = EndpointUriParser.Parse(original);

        parsed.Scheme.Should().Be("wmq");
        parsed.Path.Should().Be("Q1");
        parsed.RawParameters["host"].Should().Be("broker1");
        parsed.RawParameters["port"].Should().Be("1414");
    }
}

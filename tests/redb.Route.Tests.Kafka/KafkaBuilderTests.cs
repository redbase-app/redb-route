using redb.Route.Core;
using redb.Route.Expressions;
using KafkaDsl = redb.Route.Kafka.Kafka;

namespace redb.Route.Tests.Kafka;

public class KafkaBuilderTests
{
    private static ConstantExpression C(string s) => new(s);
    // ── Factory ─────────────────────────────────────────────────────

    [Fact]
    public void Topic_StartsWithKafkaScheme()
    {
        var uri = KafkaDsl.Topic("orders").Build();
        uri.Should().StartWith("kafka:orders");
    }

    [Fact]
    public void NullTopic_Throws()
    {
        var act = () => KafkaDsl.Topic(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EmptyTopic_Throws()
    {
        var act = () => KafkaDsl.Topic("");
        act.Should().Throw<ArgumentException>();
    }

    // ── Connection ──────────────────────────────────────────────────

    [Fact]
    public void Brokers_SetsParam()
    {
        var uri = KafkaDsl.Topic("t").Brokers(C("broker1:9092")).Build();
        uri.Should().Contain("brokers=broker1%3a9092");
    }

    [Fact]
    public void SecurityProtocol_SetsParam()
    {
        var uri = KafkaDsl.Topic("t").SecurityProtocol("SaslSsl").Build();
        uri.Should().Contain("securityProtocol=SaslSsl");
    }

    [Fact]
    public void Sasl_SetsParams()
    {
        var uri = KafkaDsl.Topic("t").Sasl("PLAIN", C("user"), C("pass")).Build();
        uri.Should().Contain("saslMechanism=PLAIN");
        uri.Should().Contain("saslUsername=user");
        uri.Should().Contain("saslPassword=pass");
    }

    [Fact]
    public void SslCa_SetsParam()
    {
        var uri = KafkaDsl.Topic("t").SslCa(C("/certs/ca.pem")).Build();
        uri.Should().Contain("sslCaLocation=");
    }

    [Fact]
    public void ConnectionFactory_SetsParam()
    {
        var uri = KafkaDsl.Topic("t").ConnectionFactory(C("myFactory")).Build();
        uri.Should().Contain("connectionFactory=myFactory");
    }

    // ── Consumer ────────────────────────────────────────────────────

    [Fact]
    public void GroupId_SetsParam()
    {
        var uri = KafkaDsl.Topic("t").GroupId(C("grp1")).Build();
        uri.Should().Contain("groupId=grp1");
    }

    [Fact]
    public void AutoOffsetReset_SetsParam()
    {
        var uri = KafkaDsl.Topic("t").AutoOffsetReset("Earliest").Build();
        uri.Should().Contain("autoOffsetReset=Earliest");
    }

    [Fact]
    public void MaxPollRecords_SetsParam()
    {
        var uri = KafkaDsl.Topic("t").MaxPollRecords(100).Build();
        uri.Should().Contain("maxPollRecords=100");
    }

    [Fact]
    public void BreakOnFirstError_SetsParam()
    {
        var uri = KafkaDsl.Topic("t").BreakOnFirstError().Build();
        uri.Should().Contain("breakOnFirstError=true");
    }

    [Fact]
    public void SeekTo_SetsParam()
    {
        var uri = KafkaDsl.Topic("t").SeekTo("beginning").Build();
        uri.Should().Contain("seekTo=beginning");
    }

    [Fact]
    public void TopicIsPattern_SetsParam()
    {
        var uri = KafkaDsl.Topic("order.*").TopicIsPattern().Build();
        uri.Should().Contain("topicIsPattern=true");
    }

    [Fact]
    public void IsolationLevel_SetsParam()
    {
        var uri = KafkaDsl.Topic("t").IsolationLevel("ReadCommitted").Build();
        uri.Should().Contain("isolationLevel=ReadCommitted");
    }

    // ── Producer ────────────────────────────────────────────────────

    [Fact]
    public void Acks_SetsParam()
    {
        var uri = KafkaDsl.Topic("t").Acks("All").Build();
        uri.Should().Contain("acks=All");
    }

    [Fact]
    public void Retries_SetsParam()
    {
        var uri = KafkaDsl.Topic("t").Retries(5).Build();
        uri.Should().Contain("retries=5");
    }

    [Fact]
    public void RecordMetadata_SetsParam()
    {
        var uri = KafkaDsl.Topic("t").RecordMetadata().Build();
        uri.Should().Contain("recordMetadata=true");
    }

    [Fact]
    public void Key_SetsParam()
    {
        var uri = KafkaDsl.Topic("t").Key(C("userId")).Build();
        uri.Should().Contain("key=userId");
    }

    [Fact]
    public void Compression_SetsParam()
    {
        var uri = KafkaDsl.Topic("t").Compression("snappy").Build();
        uri.Should().Contain("compressionType=snappy");
    }

    [Fact]
    public void Transacted_SetsParam()
    {
        var uri = KafkaDsl.Topic("t").Transacted().Build();
        uri.Should().Contain("transacted=true");
    }

    // ── Conversion ──────────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = KafkaDsl.Topic("orders").Brokers(C("b:9092")).GroupId(C("g"));
        uri.Should().StartWith("kafka:orders?");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = KafkaDsl.Topic("t").Brokers(C("b:9092")).Acks("All");
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full chain ──────────────────────────────────────────────────

    [Fact]
    public void FullChain_Consumer_BuildsCorrectUri()
    {
        var uri = KafkaDsl.Topic("orders")
            .Brokers(C("broker1:9092,broker2:9092"))
            .GroupId(C("order-group"))
            .AutoOffsetReset("Earliest")
            .MaxPollRecords(500)
            .BreakOnFirstError()
            .IsolationLevel("ReadCommitted")
            .Build();

        uri.Should().StartWith("kafka:orders?");
        uri.Should().Contain("groupId=order-group");
        uri.Should().Contain("autoOffsetReset=Earliest");
        uri.Should().Contain("maxPollRecords=500");
        uri.Should().Contain("breakOnFirstError=true");
    }

    [Fact]
    public void FullChain_Producer_BuildsCorrectUri()
    {
        var uri = KafkaDsl.Topic("notifications")
            .Brokers(C("broker1:9092"))
            .Acks("All")
            .Retries(3)
            .Key(C("userId"))
            .Compression("snappy")
            .Build();

        uri.Should().StartWith("kafka:notifications?");
        uri.Should().Contain("acks=All");
        uri.Should().Contain("retries=3");
        uri.Should().Contain("key=userId");
        uri.Should().Contain("compressionType=snappy");
    }

    // ── Round-trip ──────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = KafkaDsl.Topic("orders").GroupId(C("g1")).Acks("All").Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("kafka");
        parsed.Path.Should().Be("orders");
        parsed.RawParameters["groupId"].Should().Be("g1");
        parsed.RawParameters["acks"].Should().Be("All");
    }
}

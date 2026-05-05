using redb.Route.Core;
using redb.Route.Kafka;

namespace redb.Route.Tests.Kafka;

public sealed class KafkaComponentTests
{
    private readonly KafkaComponent _sut = new();

    [Fact]
    public void Scheme_ReturnsKafka()
    {
        _sut.Scheme.Should().Be("kafka");
    }

    [Fact]
    public void CreateEndpoint_ValidUri_ReturnsKafkaEndpoint()
    {
        var uri = EndpointUriParser.Parse("kafka://my-topic?brokers=localhost:9092&groupId=grp1");

        var endpoint = _sut.CreateEndpoint(uri);

        endpoint.Should().BeOfType<KafkaEndpoint>();
        var kafka = (KafkaEndpoint)endpoint;
        kafka.TopicName.Should().Be("my-topic");
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var act = () => _sut.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_MissingBrokers_Throws()
    {
        var uri = EndpointUriParser.Parse("kafka://my-topic");

        var act = () => _sut.CreateEndpoint(uri);

        act.Should().Throw<ArgumentException>().WithMessage("*brokers*");
    }

    [Fact]
    public void CreateEndpoint_WithAllConsumerOptions_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse(
            "kafka://events?brokers=b1:9092,b2:9092&groupId=g1&autoOffsetReset=Earliest&maxPollRecords=50&pollTimeoutMs=2000&breakOnFirstError=true&seekTo=beginning&topicIsPattern=true");

        var endpoint = (KafkaEndpoint)_sut.CreateEndpoint(uri);

        endpoint.TopicName.Should().Be("events");
    }

    [Fact]
    public void CreateEndpoint_WithProducerOptions_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse(
            "kafka://orders?brokers=localhost:9092&acks=All&retries=5&recordMetadata=true&key=OrderId&transacted=true&transactionIdPrefix=my-app");

        var endpoint = (KafkaEndpoint)_sut.CreateEndpoint(uri);
        endpoint.TopicName.Should().Be("orders");
    }

    [Fact]
    public void CreateEndpoint_WithSecurityOptions_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse(
            "kafka://secure-topic?brokers=kafka:9093&securityProtocol=SaslSsl&saslMechanism=Plain&saslUsername=user&saslPassword=pass");

        var endpoint = (KafkaEndpoint)_sut.CreateEndpoint(uri);
        endpoint.TopicName.Should().Be("secure-topic");
    }
}

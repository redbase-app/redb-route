using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Kafka;

namespace redb.Route.Tests.Kafka;

public sealed class KafkaConsumerTests
{
    private readonly KafkaComponent _component = new();

    private KafkaEndpoint CreateEndpoint(string uriStr)
    {
        var uri = EndpointUriParser.Parse(uriStr);
        return (KafkaEndpoint)_component.CreateEndpoint(uri);
    }

    [Fact]
    public void Ctor_NullEndpoint_Throws()
    {
        var opts = new KafkaEndpointOptions { Brokers = "x:9092", GroupId = "g" };
        var proc = Substitute.For<IProcessor>();
        var act = () => new KafkaConsumer(null!, proc, opts);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullProcessor_Throws()
    {
        var ep = CreateEndpoint("kafka://t?brokers=x:9092&groupId=g");
        var opts = new KafkaEndpointOptions { Brokers = "x:9092", GroupId = "g" };
        var act = () => new KafkaConsumer(ep, null!, opts);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        var ep = CreateEndpoint("kafka://t?brokers=x:9092&groupId=g");
        var proc = Substitute.For<IProcessor>();
        var act = () => new KafkaConsumer(ep, proc, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ProcessedCount_InitiallyZero()
    {
        var ep = CreateEndpoint("kafka://t?brokers=x:9092&groupId=g");
        var proc = Substitute.For<IProcessor>();
        var opts = new KafkaEndpointOptions { Brokers = "x:9092", GroupId = "g" };
        var consumer = new KafkaConsumer(ep, proc, opts);
        consumer.ProcessedCount.Should().Be(0);
    }

    [Fact]
    public async Task Stop_BeforeStart_DoesNotThrow()
    {
        var ep = CreateEndpoint("kafka://t?brokers=x:9092&groupId=g");
        var proc = Substitute.For<IProcessor>();
        var opts = new KafkaEndpointOptions { Brokers = "x:9092", GroupId = "g" };
        var consumer = new KafkaConsumer(ep, proc, opts);

        var act = () => consumer.Stop();
        await act.Should().NotThrowAsync();
    }
}

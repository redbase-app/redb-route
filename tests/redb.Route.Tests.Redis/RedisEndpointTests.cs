using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Redis;

namespace redb.Route.Tests.Redis;

public sealed class RedisEndpointTests
{
    private readonly RedisComponent _component = new();

    private RedisEndpoint CreateEndpoint(string uriStr)
    {
        var uri = EndpointUriParser.Parse(uriStr);
        return (RedisEndpoint)_component.CreateEndpoint(uri);
    }

    [Fact]
    public void OperationType_ExtractedFromPath()
    {
        var ep = CreateEndpoint("redis:SET:mykey?connectionString=localhost:6379");
        ep.OperationType.Should().Be(RedisOperationType.SET);
    }

    [Fact]
    public void Resource_ExtractedFromPath()
    {
        var ep = CreateEndpoint("redis:GET:user:123?connectionString=localhost:6379");
        ep.Resource.Should().Be("user:123");
    }

    [Fact]
    public void CreateProducer_ReturnsRedisProducer()
    {
        var ep = CreateEndpoint("redis:SET:key1?connectionString=localhost:6379");
        ep.CreateProducer().Should().BeOfType<RedisProducer>();
    }

    [Fact]
    public void CreateConsumer_Subscribe_ReturnsRedisConsumer()
    {
        var ep = CreateEndpoint("redis:SUBSCRIBE:channel1?connectionString=localhost:6379");
        var processor = Substitute.For<IProcessor>();
        ep.CreateConsumer(processor).Should().BeOfType<RedisConsumer>();
    }

    [Fact]
    public void CreateConsumer_XGroupWithoutConsumerGroup_Throws()
    {
        var ep = CreateEndpoint("redis:XGROUP:mystream?connectionString=localhost:6379");
        var processor = Substitute.For<IProcessor>();

        var act = () => ep.CreateConsumer(processor);
        act.Should().Throw<InvalidOperationException>().WithMessage("*ConsumerGroup*");
    }

    [Fact]
    public void CreateConsumer_XGroupWithConsumerGroup_ReturnsRedisConsumer()
    {
        var ep = CreateEndpoint("redis:XGROUP:mystream?connectionString=localhost:6379&consumerGroup=grp1");
        var processor = Substitute.For<IProcessor>();
        ep.CreateConsumer(processor).Should().BeOfType<RedisConsumer>();
    }

    [Fact]
    public void IsPubSubOperation_ReturnsTrueForPubSub()
    {
        RedisEndpoint.IsPubSubOperation(RedisOperationType.SUBSCRIBE).Should().BeTrue();
        RedisEndpoint.IsPubSubOperation(RedisOperationType.PSUBSCRIBE).Should().BeTrue();
        RedisEndpoint.IsPubSubOperation(RedisOperationType.PUBLISH).Should().BeTrue();
        RedisEndpoint.IsPubSubOperation(RedisOperationType.SET).Should().BeFalse();
    }

    [Fact]
    public void IsStreamOperation_ReturnsTrueForStreams()
    {
        RedisEndpoint.IsStreamOperation(RedisOperationType.XADD).Should().BeTrue();
        RedisEndpoint.IsStreamOperation(RedisOperationType.XREAD).Should().BeTrue();
        RedisEndpoint.IsStreamOperation(RedisOperationType.XGROUP).Should().BeTrue();
        RedisEndpoint.IsStreamOperation(RedisOperationType.SET).Should().BeFalse();
    }

    [Fact]
    public void IsListBlockingOperation_ReturnsTrueForBlocking()
    {
        RedisEndpoint.IsListBlockingOperation(RedisOperationType.BLPOP).Should().BeTrue();
        RedisEndpoint.IsListBlockingOperation(RedisOperationType.BRPOP).Should().BeTrue();
        RedisEndpoint.IsListBlockingOperation(RedisOperationType.LPOP).Should().BeFalse();
    }

    [Fact]
    public void Component_IsRedisComponent()
    {
        var ep = CreateEndpoint("redis:SET:key?connectionString=localhost:6379");
        ep.Component.Should().BeSameAs(_component);
    }
}

using redb.Route.Core;
using redb.Route.Redis;

namespace redb.Route.Tests.Redis;

public sealed class RedisComponentTests
{
    private readonly RedisComponent _sut = new();

    [Fact]
    public void Scheme_ReturnsRedis()
    {
        _sut.Scheme.Should().Be("redis");
    }

    [Fact]
    public void CreateEndpoint_ValidUri_ReturnsRedisEndpoint()
    {
        var uri = EndpointUriParser.Parse("redis:SET:mykey?connectionString=localhost:6379");

        var endpoint = _sut.CreateEndpoint(uri);

        endpoint.Should().BeOfType<RedisEndpoint>();
        var redis = (RedisEndpoint)endpoint;
        redis.OperationType.Should().Be(RedisOperationType.SET);
        redis.Resource.Should().Be("mykey");
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var act = () => _sut.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_PubSubUri_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse("redis:SUBSCRIBE:notifications?connectionString=localhost:6379");
        var endpoint = (RedisEndpoint)_sut.CreateEndpoint(uri);

        endpoint.OperationType.Should().Be(RedisOperationType.SUBSCRIBE);
        endpoint.Resource.Should().Be("notifications");
    }

    [Fact]
    public void CreateEndpoint_StreamUri_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse("redis:XADD:mystream?connectionString=localhost:6379");
        var endpoint = (RedisEndpoint)_sut.CreateEndpoint(uri);

        endpoint.OperationType.Should().Be(RedisOperationType.XADD);
        endpoint.Resource.Should().Be("mystream");
    }

    [Fact]
    public void CreateEndpoint_UnknownOperation_Throws()
    {
        var uri = EndpointUriParser.Parse("redis:FOOBAR:key1?connectionString=localhost:6379");

        var act = () => _sut.CreateEndpoint(uri);
        act.Should().Throw<ArgumentException>().WithMessage("*Unknown Redis operation*");
    }

    [Fact]
    public void CreateEndpoint_ColonPathWithNestedKey_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse("redis:GET:user:123?connectionString=localhost:6379");
        var endpoint = (RedisEndpoint)_sut.CreateEndpoint(uri);

        endpoint.OperationType.Should().Be(RedisOperationType.GET);
        endpoint.Resource.Should().Be("user:123");
    }

    [Fact]
    public void CreateEndpoint_ListOperation_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse("redis:LPUSH:mylist?connectionString=localhost:6379");
        var endpoint = (RedisEndpoint)_sut.CreateEndpoint(uri);

        endpoint.OperationType.Should().Be(RedisOperationType.LPUSH);
        endpoint.Resource.Should().Be("mylist");
    }

    [Fact]
    public void CreateEndpoint_HashOperation_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse("redis:HSET:myhash?connectionString=localhost:6379&field=name");
        var endpoint = (RedisEndpoint)_sut.CreateEndpoint(uri);

        endpoint.OperationType.Should().Be(RedisOperationType.HSET);
    }

    [Fact]
    public void CreateEndpoint_CaseInsensitive_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse("redis:set:mykey?connectionString=localhost:6379");
        var endpoint = (RedisEndpoint)_sut.CreateEndpoint(uri);
        endpoint.OperationType.Should().Be(RedisOperationType.SET);
    }
}

using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Redis;

namespace redb.Route.Tests.Redis;

public sealed class RedisConsumerTests
{
    private readonly RedisComponent _component = new();

    private RedisEndpoint CreateEndpoint(string uriStr)
    {
        var uri = EndpointUriParser.Parse(uriStr);
        return (RedisEndpoint)_component.CreateEndpoint(uri);
    }

    [Fact]
    public void Ctor_NullEndpoint_Throws()
    {
        var opts = new RedisEndpointOptions();
        var proc = Substitute.For<IProcessor>();
        var act = () => new RedisConsumer(null!, proc, opts);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullProcessor_Throws()
    {
        var ep = CreateEndpoint("redis:SUBSCRIBE:ch?connectionString=localhost:6379");
        var opts = new RedisEndpointOptions();
        var act = () => new RedisConsumer(ep, null!, opts);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        var ep = CreateEndpoint("redis:SUBSCRIBE:ch?connectionString=localhost:6379");
        var proc = Substitute.For<IProcessor>();
        var act = () => new RedisConsumer(ep, proc, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ProcessedCount_InitiallyZero()
    {
        var ep = CreateEndpoint("redis:SUBSCRIBE:ch?connectionString=localhost:6379");
        var proc = Substitute.For<IProcessor>();
        var opts = new RedisEndpointOptions();
        var consumer = new RedisConsumer(ep, proc, opts);
        consumer.ProcessedCount.Should().Be(0);
    }

    [Fact]
    public async Task Stop_BeforeStart_DoesNotThrow()
    {
        var ep = CreateEndpoint("redis:SUBSCRIBE:ch?connectionString=localhost:6379");
        var proc = Substitute.For<IProcessor>();
        var opts = new RedisEndpointOptions();
        var consumer = new RedisConsumer(ep, proc, opts);

        var act = () => consumer.Stop();
        await act.Should().NotThrowAsync();
    }
}

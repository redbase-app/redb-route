using redb.Route.Core;
using redb.Route.Redis;

namespace redb.Route.Tests.Redis;

public sealed class RedisProducerTests
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
        var act = () => new RedisProducer(null!, opts);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        var ep = CreateEndpoint("redis:SET:k?connectionString=localhost:6379");
        var act = () => new RedisProducer(ep, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Process_BeforeStart_Throws()
    {
        var ep = CreateEndpoint("redis:SET:k?connectionString=localhost:6379");
        var producer = new RedisProducer(ep, new RedisEndpointOptions());
        var exchange = new Exchange(new Message("test"));

        var act = () => producer.Process(exchange);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not been started*");
    }

    [Fact]
    public async Task Stop_BeforeStart_DoesNotThrow()
    {
        var ep = CreateEndpoint("redis:SET:k?connectionString=localhost:6379");
        var producer = new RedisProducer(ep, new RedisEndpointOptions());

        var act = () => producer.Stop();
        await act.Should().NotThrowAsync();
    }
}

using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Amqp;

namespace redb.Route.Tests.Amqp;

public sealed class AmqpProducerTests
{
    private readonly AmqpComponent _component = new();

    private AmqpEndpoint CreateEndpoint(string uriStr)
    {
        var uri = EndpointUriParser.Parse(uriStr);
        return (AmqpEndpoint)_component.CreateEndpoint(uri);
    }

    [Fact]
    public void Ctor_NullEndpoint_Throws()
    {
        var opts = new AmqpEndpointOptions();
        var act = () => new AmqpProducer(null!, opts);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        var ep = CreateEndpoint("amqp://q?host=localhost");
        var act = () => new AmqpProducer(ep, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Process_BeforeStart_Throws()
    {
        var ep = CreateEndpoint("amqp://q?host=localhost");
        var producer = new AmqpProducer(ep, new AmqpEndpointOptions());
        var exchange = new Exchange(new Message("test"));

        var act = () => producer.Process(exchange);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not been started*");
    }

    [Fact]
    public async Task Stop_BeforeStart_DoesNotThrow()
    {
        var ep = CreateEndpoint("amqp://q?host=localhost");
        var producer = new AmqpProducer(ep, new AmqpEndpointOptions());

        var act = () => producer.Stop();
        await act.Should().NotThrowAsync();
    }
}

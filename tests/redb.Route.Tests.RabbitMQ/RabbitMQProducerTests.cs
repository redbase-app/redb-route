using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RabbitMQ;

namespace redb.Route.Tests.RabbitMQ;

public sealed class RabbitMQProducerTests
{
    private readonly RabbitMQComponent _component = new();

    private RabbitMQEndpoint CreateEndpoint(string uriStr)
    {
        var uri = EndpointUriParser.Parse(uriStr);
        return (RabbitMQEndpoint)_component.CreateEndpoint(uri);
    }

    [Fact]
    public void Ctor_NullEndpoint_Throws()
    {
        var opts = new RabbitMQEndpointOptions();
        var act = () => new RabbitMQProducer(null!, opts);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        var ep = CreateEndpoint("rabbitmq://q?host=localhost");
        var act = () => new RabbitMQProducer(ep, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Process_BeforeStart_Throws()
    {
        var ep = CreateEndpoint("rabbitmq://q?host=localhost");
        var producer = new RabbitMQProducer(ep, new RabbitMQEndpointOptions());
        var exchange = new Exchange(new Message("test"));

        var act = () => producer.Process(exchange);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not been started*");
    }

    [Fact]
    public async Task Stop_BeforeStart_DoesNotThrow()
    {
        var ep = CreateEndpoint("rabbitmq://q?host=localhost");
        var producer = new RabbitMQProducer(ep, new RabbitMQEndpointOptions());

        var act = () => producer.Stop();
        await act.Should().NotThrowAsync();
    }
}

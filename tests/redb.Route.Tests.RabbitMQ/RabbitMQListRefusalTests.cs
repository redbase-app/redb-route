using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RabbitMQ;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// A list body at the RabbitMQ producer is refused at the send, never sent as its type name. On this line a list has no wire
/// form here; 4.8.0 sends a list of IMessage element by element. Expects the broker of the test stand.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RabbitMQListRefusalTests
{
    public static TheoryData<object> Lists => new()
    {
        new List<IMessage> { new Message("a"), new Message("b") },
        new List<int> { 1, 2 },
    };

    [Theory]
    [MemberData(nameof(Lists))]
    public async Task A_list_body_is_refused_not_sent_as_its_type_name(object list)
    {
        var endpoint = new RabbitMQComponent().CreateEndpoint(EndpointUriParser.Parse($"rabbitmq://list-refusal-{Guid.NewGuid():N}?host=localhost&port=5672&username=admin&password=admin&declare=true"));
        var producer = endpoint.CreateProducer();
        await producer.Start();
        try
        {
            var act = () => producer.Process(new Exchange(new Message(list)));

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*a list has no wire form here*");
        }
        finally
        {
            await producer.Stop();
        }
    }
}

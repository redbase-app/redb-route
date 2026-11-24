using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Kafka;

namespace redb.Route.Tests.Kafka;

/// <summary>
/// A list body at the Kafka producer is refused at the send, never sent as its type name. On this line a list has no wire
/// form here; 4.8.0 sends a list of IMessage element by element. Expects the broker of the test stand.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KafkaListRefusalTests
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
        var endpoint = new KafkaComponent().CreateEndpoint(EndpointUriParser.Parse($"kafka://list-refusal-{Guid.NewGuid():N}?brokers=localhost:29092,localhost:29094,localhost:29096"));
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

using System.Collections.Concurrent;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RabbitMQ;
using Xunit.Abstractions;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// A consumer with an empty queue name and an exchange gets a temporary queue of its own: the broker names it, the
/// consumer binds it and reads it. Every such consumer of a fanout exchange gets its copy of each message.
/// Expects RabbitMQ at localhost:5672 (admin/admin).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RabbitMQTemporaryQueueTests
{
    private readonly ITestOutputHelper _output;

    public RabbitMQTemporaryQueueTests(ITestOutputHelper output) => _output = output;

    private static RabbitMQEndpoint Endpoint(string queue, string extraParams)
        => (RabbitMQEndpoint)new RabbitMQComponent().CreateEndpoint(EndpointUriParser.Parse(
            $"rabbitmq://{queue}?host=localhost&port=5672&username=admin&password=admin&declare=true&{extraParams}"));

    private static (IProcessor Processor, ConcurrentQueue<string> Received) Recorder()
    {
        var received = new ConcurrentQueue<string>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Enqueue(Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!));
                return Task.CompletedTask;
            });
        return (processor, received);
    }

    [Fact]
    public async Task Every_consumer_with_an_empty_queue_name_gets_its_copy_of_a_fanout_message()
    {
        var exchangeName = $"fanout-{Guid.NewGuid():N}";
        var topology = $"exchange={exchangeName}&exchangeType=fanout&exchangeAutoDelete=true";

        var first = Recorder();
        var second = Recorder();
        var firstEndpoint = Endpoint("", topology);
        var secondEndpoint = Endpoint("", topology);
        var firstConsumer = (RabbitMQConsumer)firstEndpoint.CreateConsumer(first.Processor);
        var secondConsumer = (RabbitMQConsumer)secondEndpoint.CreateConsumer(second.Processor);
        await firstConsumer.Start();
        await secondConsumer.Start();

        var producerEndpoint = Endpoint("", topology);
        var producer = (RabbitMQProducer)producerEndpoint.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("to everyone")));

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((first.Received.Count < 1 || second.Received.Count < 1) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        await Task.Delay(300);

        await producer.Stop();
        await producerEndpoint.Stop();
        await firstConsumer.Stop();
        await secondConsumer.Stop();
        await firstEndpoint.Stop();
        await secondEndpoint.Stop();

        _output.WriteLine($"first: {first.Received.Count}, second: {second.Received.Count}");
        first.Received.Should().Equal("to everyone");
        second.Received.Should().Equal("to everyone");
    }

    [Fact]
    public async Task A_producer_with_an_empty_queue_name_binds_no_queue_of_its_own_to_the_exchange()
    {
        // No consumer is bound, so a mandatory publish has nowhere to go. A queue the producer bound for itself
        // would take the message, and nobody would ever read it. A fanout publish is not mandatory by default.
        var exchangeName = $"fanout-{Guid.NewGuid():N}";
        var endpoint = Endpoint("", $"exchange={exchangeName}&exchangeType=fanout&exchangeAutoDelete=true&mandatory=true");
        var producer = (RabbitMQProducer)endpoint.CreateProducer();
        await producer.Start();

        var act = () => producer.Process(new Exchange(new Message("anyone?")));

        await act.Should().ThrowAsync<RabbitMQUnroutableException>();
        await producer.Stop();
        await endpoint.Stop();
    }

    private static RabbitMQEndpoint Fluent(RabbitBuilder builder)
        => (RabbitMQEndpoint)new RabbitMQComponent().CreateEndpoint(EndpointUriParser.Parse(
            builder.Build() + "&host=localhost&port=5672&username=admin&password=admin"));

    [Fact]
    public async Task A_consumer_built_with_Rabbit_Exchange_gets_its_copy_of_a_fanout_message()
    {
        var exchangeName = $"fanout-{Guid.NewGuid():N}";
        var recorder = Recorder();
        var consumerEndpoint = Fluent(Rabbit.Exchange(exchangeName, "fanout").ExchangeAutoDelete().Declare());
        var consumer = (RabbitMQConsumer)consumerEndpoint.CreateConsumer(recorder.Processor);
        await consumer.Start();

        var producerEndpoint = Fluent(Rabbit.Exchange(exchangeName, "fanout").ExchangeAutoDelete().Declare());
        var producer = (RabbitMQProducer)producerEndpoint.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("fluent")));

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (recorder.Received.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        await producer.Stop();
        await producerEndpoint.Stop();
        await consumer.Stop();
        await consumerEndpoint.Stop();

        recorder.Received.Should().Equal("fluent");
    }

    [Fact]
    public async Task A_consumer_built_with_Rabbit_Exchange_without_Declare_fails_to_start()
    {
        var endpoint = Fluent(Rabbit.Exchange($"fanout-{Guid.NewGuid():N}", "fanout"));
        var consumer = (RabbitMQConsumer)endpoint.CreateConsumer(Recorder().Processor);

        var act = () => consumer.Start();

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("declare=false");
        await endpoint.Stop();
    }

    [Fact]
    public async Task A_consumer_with_an_empty_queue_name_and_declare_off_fails_to_start()
    {
        var endpoint = (RabbitMQEndpoint)new RabbitMQComponent().CreateEndpoint(EndpointUriParser.Parse(
            $"rabbitmq://?host=localhost&port=5672&username=admin&password=admin&declare=false&exchange=fanout-{Guid.NewGuid():N}"));
        var consumer = (RabbitMQConsumer)endpoint.CreateConsumer(Recorder().Processor);

        var act = () => consumer.Start();

        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Contain("declare=false");
        await endpoint.Stop();
    }
}

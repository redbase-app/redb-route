using System.Collections.Concurrent;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Amqp;
using redb.Route.Core;
using Xunit.Abstractions;

namespace redb.Route.Tests.Amqp;

/// <summary>
/// A MULTICAST address and a dynamic node give every receiver link its own copy or its own node, so a consumer
/// runs one link whatever <c>concurrentConsumers</c> says; <c>dynamic=true</c> reads the node the broker creates.
/// Expects ActiveMQ Artemis at localhost:5673 (admin/admin).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AmqpMulticastAndDynamicTests
{
    private const string Credentials = "host=localhost&port=5673&user=admin&password=admin";
    private readonly ITestOutputHelper _output;

    public AmqpMulticastAndDynamicTests(ITestOutputHelper output) => _output = output;

    private static AmqpEndpoint Endpoint(string address, string extraParams = "")
        => (AmqpEndpoint)new AmqpComponent().CreateEndpoint(
            EndpointUriParser.Parse($"amqp://{address}?{Credentials}{extraParams}"));

    private static (IProcessor Processor, ConcurrentQueue<string> Received) Recorder()
    {
        var received = new ConcurrentQueue<string>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var body = callInfo.Arg<IExchange>().In.Body;
                received.Enqueue(body is byte[] bytes ? Encoding.UTF8.GetString(bytes) : body?.ToString() ?? "");
                return Task.CompletedTask;
            });
        return (processor, received);
    }

    private static async Task SendAsync(string address, string extraParams, string body)
    {
        var endpoint = Endpoint(address, extraParams);
        var producer = (AmqpProducer)endpoint.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message(body)));
        await producer.Stop();
        await endpoint.Stop();
    }

    [Fact]
    public async Task A_multicast_consumer_with_concurrent_consumers_processes_each_message_once()
    {
        var address = $"multicast.{Guid.NewGuid():N}";
        var recorder = Recorder();
        var endpoint = Endpoint(address, "&routingType=MULTICAST&concurrentConsumers=3");
        var consumer = (AmqpConsumer)endpoint.CreateConsumer(recorder.Processor);
        await consumer.Start();

        await SendAsync(address, "&routingType=MULTICAST", "once");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (recorder.Received.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        // Room for the copies a second or third link would get.
        await Task.Delay(1_000);
        await consumer.Stop();
        await endpoint.Stop();

        _output.WriteLine($"processed {recorder.Received.Count} time(s)");
        recorder.Received.Should().Equal("once");
    }

    [Fact]
    public async Task A_dynamic_consumer_reads_the_node_the_broker_created_for_it()
    {
        var recorder = Recorder();
        var endpoint = Endpoint("", "&dynamic=true&concurrentConsumers=2");
        var consumer = (AmqpConsumer)endpoint.CreateConsumer(recorder.Processor);
        await consumer.Start();
        _output.WriteLine($"dynamic node: {consumer.Address}");
        consumer.Address.Should().NotBeNullOrEmpty("the broker names the node it creates");

        await SendAsync(consumer.Address, "", "to the dynamic node");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (recorder.Received.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        await consumer.Stop();
        await endpoint.Stop();

        recorder.Received.Should().Equal("to the dynamic node");
    }

    [Fact]
    public void A_dynamic_endpoint_with_an_address_is_refused()
    {
        var act = () => Endpoint("orders", "&dynamic=true");

        act.Should().Throw<ArgumentException>().WithMessage("*dynamic*");
    }
}

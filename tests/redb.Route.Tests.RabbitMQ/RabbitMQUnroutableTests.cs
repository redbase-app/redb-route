using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.RabbitMQ;
using redb.Route.Transactions;
using Xunit.Abstractions;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// A mandatory publish that no queue takes comes back as <c>basic.return</c>. The route must see that as a failed
/// send (so <c>OnException</c>, retry and dead-letter handling apply), not as a delivered message.
/// Expects RabbitMQ at localhost:5672 (admin/admin).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RabbitMQUnroutableTests
{
    private readonly ITestOutputHelper _output;

    public RabbitMQUnroutableTests(ITestOutputHelper output) => _output = output;

    /// <summary>The default exchange routes by queue name; nothing is declared, so nothing takes the message.</summary>
    private static RabbitMQEndpoint Unroutable(string extraParams = "")
        => (RabbitMQEndpoint)new RabbitMQComponent().CreateEndpoint(EndpointUriParser.Parse(
            $"rabbitmq://no-such-queue-{Guid.NewGuid():N}?host=localhost&port=5672&username=admin&password=admin{extraParams}"));

    [Fact]
    public async Task Immediate_send_that_no_queue_takes_fails_the_step()
    {
        var endpoint = Unroutable();
        var producer = (RabbitMQProducer)endpoint.CreateProducer();
        await producer.Start();

        var act = () => producer.Process(new Exchange(new Message("lost?")));

        var failure = await act.Should().ThrowAsync<RabbitMQUnroutableException>();
        _output.WriteLine(failure.Which.ToString());
        await producer.Stop();
        await endpoint.Stop();
    }

    [Fact]
    public async Task Rpc_request_that_no_queue_takes_fails_at_once_not_after_the_timeout()
    {
        var endpoint = Unroutable("&replyTo=true&timeout=20");
        var producer = (RabbitMQProducer)endpoint.CreateProducer();
        await producer.Start();

        var clock = Stopwatch.StartNew();
        var act = () => producer.Process(new Exchange(new Message("anyone?")));

        var failure = await act.Should().ThrowAsync<RabbitMQUnroutableException>();
        clock.Stop();
        _output.WriteLine($"{clock.Elapsed}: {failure.Which}");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "a returned request has no reply to wait for");
        await producer.Stop();
        await endpoint.Stop();
    }

    [Fact]
    public async Task Transacted_block_send_that_no_queue_takes_fails_the_block()
    {
        var endpoint = Unroutable();
        var producer = (RabbitMQProducer)endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("lost in a block?"));
        var act = () => new TransactedProcessor(new DelegateProcessor(async (ex, ct) =>
            {
                for (var i = 0; i < 3; i++)
                    await producer.Process(ex, ct);
            }), new TransactionPolicy())
            .Process(exchange);

        // The same exception as an immediate send of it, so one OnException<RabbitMQUnroutableException> covers both.
        var failure = await act.Should().ThrowAsync<RabbitMQUnroutableException>();
        failure.Which.Message.Should().Contain("3 of the 3 messages");
        _output.WriteLine(failure.Which.Message);
        await producer.Stop();
        await endpoint.Stop();
    }
}

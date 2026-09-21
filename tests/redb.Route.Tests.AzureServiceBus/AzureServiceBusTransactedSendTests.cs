// Integration tests run on a single TFM to avoid cross-TFM interference
// (all TFMs share the same emulator queue and can steal each other's messages).
#if NET9_0

using System.Collections.Concurrent;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.AzureServiceBus;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Transactions;

namespace redb.Route.Tests.AzureServiceBus;

/// <summary>
/// Service Bus sends inside <c>.Transacted()</c>: unset, they join the block and leave only after the database commits;
/// <c>transacted=false</c> sends at once, outside the block's transaction (the Service Bus client would otherwise enlist
/// in it on its own); <c>transacted=true</c> outside a block is refused.
/// </summary>
[Trait("Category", "Integration")]
[Collection(AsbEmulatorQueue.Name)]
public sealed class AzureServiceBusTransactedSendTests
{
    private const string ConnectionString =
        "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;" +
        "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private const string Queue = "queue.1";

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static AzureServiceBusEndpoint CreateEndpoint(string? extraParams = null)
    {
        var qs = $"connectionString={ConnectionString}";
        if (extraParams is not null) qs += $"&{extraParams}";
        return (AzureServiceBusEndpoint)new AzureServiceBusComponent().CreateEndpoint(EndpointUriParser.Parse($"asb://{Queue}?{qs}"));
    }

    private static Task InTransaction(IExchange exchange, Func<IExchange, CancellationToken, Task> body) =>
        new TransactedProcessor(new DelegateProcessor(body), new TransactionPolicy()).Process(exchange);

    private static async Task SendFromAFailedBlock(string body, string? producerParams)
    {
        var endpoint = CreateEndpoint(producerParams);
        var producer = endpoint.CreateProducer();
        await producer.Start();
        var failed = () => InTransaction(new Exchange(new Message(body)), async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            throw new InvalidOperationException("the unit of work failed");
        });
        await failed.Should().ThrowAsync<InvalidOperationException>();
        await producer.Stop();
        await endpoint.Stop();
    }

    /// <summary>Everything that reaches the queue during the window, taken off it for good.</summary>
    private static async Task<IReadOnlyCollection<string>> ReceiveFor(TimeSpan window)
    {
        var endpoint = CreateEndpoint("receiveMode=ReceiveAndDelete&maxConcurrentCalls=10");
        var received = new ConcurrentBag<string>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!));
                return Task.CompletedTask;
            });
        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(window);
        await consumer.Stop();
        await endpoint.Stop();
        return received;
    }

    [Fact]
    public async Task Send_without_transacted_joins_the_block_and_leaves_nothing_when_it_fails()
    {
        var body = $"join-{Tag()}";

        await SendFromAFailedBlock(body, producerParams: null);

        (await ReceiveFor(TimeSpan.FromSeconds(4))).Should().NotContain(body,
            "inside .Transacted() a send joins the transaction unless transacted=false opts out");
    }

    [Fact]
    public async Task Send_with_transacted_false_goes_out_at_once_even_from_a_failed_block()
    {
        var body = $"optout-{Tag()}";

        await SendFromAFailedBlock(body, "transacted=false");

        (await ReceiveFor(TimeSpan.FromSeconds(4))).Should().Contain(body);
    }

    [Fact]
    public async Task Consumer_abandons_a_rolled_back_unit_of_work_so_it_is_delivered_again()
    {
        await ReceiveFor(TimeSpan.FromSeconds(2));   // start from an empty queue
        var body = $"rbonly-{Tag()}";
        var endpoint = CreateEndpoint();
        var producer = endpoint.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message(body)));
        await producer.Stop();
        await endpoint.Stop();

        // What .RollbackAll() does inside .Transacted(): mark the unit of work rollback-only and stop the route.
        var deliveries = 0;
        var deliveredAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var route = new TransactedProcessor(new DelegateProcessor(ex =>
        {
            if (Encoding.UTF8.GetString((byte[])ex.In.Body!) != body) return;
            if (Interlocked.Increment(ref deliveries) >= 2) deliveredAgain.TrySetResult();
            ex.MarkRollbackOnly();
            ex.Stop();
        }), new TransactionPolicy());

        var consumerEndpoint = CreateEndpoint();
        var consumer = consumerEndpoint.CreateConsumer(route);
        await consumer.Start();
        await Task.WhenAny(deliveredAgain.Task, Task.Delay(15_000));
        await consumer.Stop();
        await consumerEndpoint.Stop();

        deliveries.Should().BeGreaterThanOrEqualTo(2,
            "a rolled-back unit of work is abandoned, not completed, so Service Bus delivers the message again");
    }

    [Fact]
    public async Task Send_with_transacted_true_outside_a_block_is_refused()
    {
        var endpoint = CreateEndpoint("transacted=true");
        var producer = endpoint.CreateProducer();
        await producer.Start();

        var act = () => producer.Process(new Exchange(new Message($"none-{Tag()}")));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Transacted()*");
        await producer.Stop();
        await endpoint.Stop();
    }
}

#endif

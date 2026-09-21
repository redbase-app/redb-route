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
/// <c>batchCommit=true</c>: the sends a producer defers in a <c>.Transacted()</c> block leave as one Service Bus batch —
/// the entity takes it whole or not at all — and a block whose messages do not fit in one batch sends nothing. The batch
/// body mode (<c>enableBatch</c>) no longer drops the items over its limits without a word.
/// </summary>
[Trait("Category", "Integration")]
[Collection(AsbEmulatorQueue.Name)]
public sealed class AzureServiceBusBatchCommitTests
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
    public async Task The_sends_of_a_block_leave_as_one_batch()
    {
        var tag = Tag();
        var endpoint = CreateEndpoint("batchCommit=true");
        var producer = endpoint.CreateProducer();
        await producer.Start();

        await InTransaction(new Exchange(new Message($"{tag}-one")), async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            ex.In.Body = $"{tag}-two";
            await producer.Process(ex, ct);
        });
        await producer.Stop();
        await endpoint.Stop();

        (await ReceiveFor(TimeSpan.FromSeconds(3))).Where(b => b.StartsWith(tag)).Order()
            .Should().Equal($"{tag}-one", $"{tag}-two");
    }

    [Fact]
    public async Task A_block_whose_messages_do_not_fit_in_one_batch_sends_nothing()
    {
        var tag = Tag();
        var endpoint = CreateEndpoint("batchCommit=true&batchMaxSizeBytes=2048");
        var producer = endpoint.CreateProducer();
        await producer.Start();

        var block = () => InTransaction(new Exchange(new Message($"{tag}-small")), async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            ex.In.Body = tag + new string('x', 3_000);
            await producer.Process(ex, ct);
        });
        await block.Should().ThrowAsync<InvalidOperationException>().WithMessage("*do not fit in one Service Bus batch*");
        await producer.Stop();
        await endpoint.Stop();

        (await ReceiveFor(TimeSpan.FromSeconds(3))).Where(b => b.StartsWith(tag))
            .Should().BeEmpty("the small message is not sent without the one that did not fit");
    }

    [Fact]
    public async Task A_batch_body_over_batchMaxMessages_fails_instead_of_dropping_the_rest()
    {
        var tag = Tag();
        var endpoint = CreateEndpoint("enableBatch=true&batchMaxMessages=2");
        var producer = endpoint.CreateProducer();
        await producer.Start();

        var send = () => producer.Process(new Exchange(new Message(new[] { $"{tag}-1", $"{tag}-2", $"{tag}-3" })));
        await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("*batchMaxMessages=2*");
        await producer.Stop();
        await endpoint.Stop();

        (await ReceiveFor(TimeSpan.FromSeconds(3))).Where(b => b.Contains(tag)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_batch_body_over_batchMaxSizeBytes_fails_instead_of_dropping_the_rest()
    {
        var tag = Tag();
        var endpoint = CreateEndpoint("enableBatch=true&batchMaxSizeBytes=2048");
        var producer = endpoint.CreateProducer();
        await producer.Start();

        var big = new string('x', 1_500);
        var send = () => producer.Process(new Exchange(new Message(new[] { $"{tag}-{big}", $"{tag}-{big}" })));
        await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("*do not fit in one Service Bus batch*");
        await producer.Stop();
        await endpoint.Stop();

        (await ReceiveFor(TimeSpan.FromSeconds(3))).Where(b => b.Contains(tag)).Should().BeEmpty();
    }

    [Fact]
    public void BatchCommit_contradicts_transacted_false()
    {
        var act = () => CreateEndpoint("batchCommit=true&transacted=false");

        act.Should().Throw<ArgumentException>().WithMessage("*batchCommit*transacted=false*");
    }
}
#endif

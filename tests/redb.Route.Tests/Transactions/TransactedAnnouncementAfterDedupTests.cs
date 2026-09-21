using System.Collections.Concurrent;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Transactions;
using Xunit;

namespace redb.Route.Tests.Transactions;

/// <summary>
/// Where the announcement goes relative to the dedup key. The database commits first, so the one failure the
/// order leaves open is "the work is stored, the message did not go out". The broker then redelivers — and the dedup
/// key, committed with the work, recognises the redelivery. If the send sits inside the dedup block it is skipped
/// with the work and the announcement is lost for good; after the block it runs again and goes out.
/// </summary>
public class TransactedAnnouncementAfterDedupTests
{
    private sealed class FlakySend : ITransactedAction
    {
        private readonly Func<bool> _fail;
        public FlakySend(Func<bool> fail) => _fail = fail;
        public static int Sent;
        public Task Commit(CancellationToken ct = default)
        {
            if (_fail())
                throw new InvalidOperationException("broker down after the database committed");
            Interlocked.Increment(ref Sent);
            return Task.CompletedTask;
        }
        public Task Rollback(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static void Register(IExchange ex, ITransactedAction action) =>
        ((ConcurrentDictionary<string, ITransactedAction>)ex.Properties[TransactedProcessor.TransactActionPropertyKey]!)
            [$"send-{Guid.NewGuid():N}"] = action;

    private static Exchange Delivery() => new(new Message("order-1") { Headers = { ["messageId"] = "m-1" } });

    [Fact]
    public async Task Send_after_the_dedup_block_goes_out_on_the_redelivery()
    {
        FlakySend.Sent = 0;
        var work = 0;
        var firstSend = true;
        await using var context = new RouteContext();

        context.AddRoutes(r => r
            .From("direct://announce-after")
            .Transacted()
                .IdempotentConsumer(new InMemoryIdempotentRepository(), ex => ex.In.GetHeader<string>("messageId")!)
                    .Process(_ => work++)
                .EndIdempotentConsumer()
                .Process(ex => Register(ex, new FlakySend(() => { var f = firstSend; firstSend = false; return f; })))
            .End());

        await context.Start();
        var producer = context.GetEndpoint("direct://announce-after").CreateProducer();
        await producer.Start();

        // First delivery: the work commits, the send fails after the commit, the failure propagates (no ack).
        var first = () => producer.Process(Delivery());
        await first.Should().ThrowAsync<InvalidOperationException>();

        // The broker redelivers: the dedup key skips the work, the send runs again and goes out.
        await producer.Process(Delivery());

        work.Should().Be(1, "the dedup key keeps the work from being done twice");
        FlakySend.Sent.Should().Be(1, "the announcement is not lost: it goes out on the redelivery");
    }

    [Fact]
    public async Task Send_inside_the_dedup_block_is_lost_when_it_failed_after_the_commit()
    {
        FlakySend.Sent = 0;
        var work = 0;
        var firstSend = true;
        await using var context = new RouteContext();

        context.AddRoutes(r => r
            .From("direct://announce-inside")
            .Transacted()
                .IdempotentConsumer(new InMemoryIdempotentRepository(), ex => ex.In.GetHeader<string>("messageId")!)
                    .Process(_ => work++)
                    .Process(ex => Register(ex, new FlakySend(() => { var f = firstSend; firstSend = false; return f; })))
                .EndIdempotentConsumer()
            .End());

        await context.Start();
        var producer = context.GetEndpoint("direct://announce-inside").CreateProducer();
        await producer.Start();

        var first = () => producer.Process(Delivery());
        await first.Should().ThrowAsync<InvalidOperationException>();
        await producer.Process(Delivery());

        work.Should().Be(1);
        FlakySend.Sent.Should().Be(0,
            "inside the dedup block the redelivery is skipped whole, the send with it: this is the shape to avoid");
    }
}

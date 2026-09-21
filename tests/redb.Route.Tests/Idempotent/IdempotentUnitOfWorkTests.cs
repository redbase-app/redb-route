using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Idempotent;

/// <summary>
/// When the idempotent consumer confirms or removes its key (Apache Camel's defaults, <c>completionEager=false</c> and
/// <c>removeOnFailure=true</c>): at the end of the exchange's unit of work, not at the end of the block. A failure anywhere
/// in the exchange takes the key back, so the redelivery is processed again; a key claimed inside a transaction follows
/// the transaction instead.
/// </summary>
public class IdempotentUnitOfWorkTests
{
    private static Exchange Delivery(string id) => new(new Message("order") { Headers = { ["messageId"] = id } });

    private static string MessageId(IExchange ex) => ex.In.GetHeader<string>("messageId")!;

    private static async Task<IProducer> Start(RouteContext context, string uri)
    {
        await context.Start();
        var producer = context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        return producer;
    }

    [Fact]
    public async Task A_failure_after_the_block_takes_the_key_back()
    {
        var repo = new InMemoryIdempotentRepository();
        var work = 0;
        var failNext = true;
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://uow-after")
            .IdempotentConsumer(repo, MessageId)
                .Process(_ => work++)
            .EndIdempotentConsumer()
            .Process(_ =>
            {
                if (!failNext) return;
                failNext = false;
                throw new InvalidOperationException("a step after the block");
            }));
        var producer = await Start(context, "direct://uow-after");

        var first = () => producer.Process(Delivery("m-1"));
        await first.Should().ThrowAsync<InvalidOperationException>();
        (await repo.Contains("m-1")).Should().BeFalse("the exchange failed, so its message must be processed again");

        await producer.Process(Delivery("m-1"));
        work.Should().Be(2, "the redelivery is not a duplicate: the first attempt did not complete");
        (await repo.Contains("m-1")).Should().BeTrue();
    }

    [Fact]
    public async Task RollbackAll_in_the_block_takes_the_key_back()
    {
        var repo = new InMemoryIdempotentRepository();
        var work = 0;
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://uow-rollback")
            .IdempotentConsumer(repo, MessageId)
                .Process(_ => work++)
                .RollbackAll()
            .EndIdempotentConsumer());
        var producer = await Start(context, "direct://uow-rollback");

        await producer.Process(Delivery("m-2"));

        work.Should().Be(1);
        (await repo.Contains("m-2")).Should().BeFalse(
            "a rollback-only exchange is not acknowledged; a key left behind would skip its redelivery as a duplicate");
    }

    [Fact]
    public async Task A_route_called_with_the_exchange_shares_the_callers_unit_of_work()
    {
        var repo = new InMemoryIdempotentRepository();
        await using var context = new RouteContext();
        context.AddRoutes(r =>
        {
            r.From("direct://uow-caller")
                .To("direct://uow-callee")
                .Process(_ => throw new InvalidOperationException("the caller fails after the callee returned"));
            r.From("direct://uow-callee")
                .IdempotentConsumer(repo, MessageId)
                    .Process(_ => { })
                .EndIdempotentConsumer();
        });
        var producer = await Start(context, "direct://uow-caller");

        var send = () => producer.Process(Delivery("m-3"));
        await send.Should().ThrowAsync<InvalidOperationException>();

        (await repo.Contains("m-3")).Should().BeFalse("the unit of work is the caller's, and the caller failed");
    }

    [Fact]
    public async Task The_key_is_back_before_the_exchange_is_reported_failed()
    {
        var repo = new InMemoryIdempotentRepository();
        var listener = new KeyAtFailure(repo, "m-4");
        await using var context = new RouteContext();
        context.AddLifecycleListener(listener);
        context.AddRoutes(r => r
            .From("direct://uow-order")
            .IdempotentConsumer(repo, MessageId)
                .Process(_ => { })
            .EndIdempotentConsumer()
            .Process(_ => throw new InvalidOperationException("after the block")));
        var producer = await Start(context, "direct://uow-order");

        var send = () => producer.Process(Delivery("m-4"));
        await send.Should().ThrowAsync<InvalidOperationException>();

        // The consumer settles the message after this point; a key still present here could skip a redelivery that
        // another consumer picks up at once.
        listener.KeyPresentAtFailure.Should().BeFalse();
    }

    [Fact]
    public async Task A_redelivery_of_the_route_runs_a_block_that_failed_again()
    {
        var repo = new InMemoryIdempotentRepository();
        var attempts = 0;
        IExchange? seen = null;
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://uow-redeliver-block")
            .OnException<InvalidOperationException>()
                .MaximumRedeliveries(1)
                .RedeliveryDelay(TimeSpan.FromMilliseconds(1))
            .End()
            .IdempotentConsumer(repo, MessageId)
                .Process(ex =>
                {
                    seen = ex;
                    if (++attempts == 1)
                        throw new InvalidOperationException("the block fails once");
                })
            .EndIdempotentConsumer());
        var producer = await Start(context, "direct://uow-redeliver-block");

        await producer.Process(Delivery("m-5"));

        attempts.Should().Be(2, "the exchange holding the key is not a duplicate of itself");
        seen!.Properties.Should().NotContainKey(IdempotentConsumerProcessor.DuplicatePropertyKey);
        (await repo.Contains("m-5")).Should().BeTrue();
    }

    [Fact]
    public async Task A_redelivery_of_the_route_does_not_redo_a_block_that_succeeded()
    {
        var repo = new InMemoryIdempotentRepository();
        var work = 0;
        var tail = 0;
        IExchange? seen = null;
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://uow-redeliver-tail")
            .OnException<InvalidOperationException>()
                .MaximumRedeliveries(1)
                .RedeliveryDelay(TimeSpan.FromMilliseconds(1))
            .End()
            .IdempotentConsumer(repo, MessageId)
                .Process(_ => work++)
            .EndIdempotentConsumer()
            .Process(ex =>
            {
                seen = ex;
                if (++tail == 1)
                    throw new InvalidOperationException("a step after the block fails once");
            }));
        var producer = await Start(context, "direct://uow-redeliver-tail");

        await producer.Process(Delivery("m-6"));

        work.Should().Be(1, "Camel redelivers the failed step, not the block before it");
        tail.Should().Be(2);
        seen!.Properties.Should().NotContainKey(IdempotentConsumerProcessor.DuplicatePropertyKey,
            "the redelivery is the same exchange, not a duplicate message");
        (await repo.Contains("m-6")).Should().BeTrue();
    }

    [Fact]
    public async Task A_split_branch_has_its_own_unit_of_work()
    {
        var repo = new InMemoryIdempotentRepository();
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://uow-split")
            .Split(e => (IEnumerable<object?>)e.In.Body!, b => b
                .IdempotentConsumer(e => (string)e.In.Body!, repo, ib => ib
                    .Process(_ => { }))
                .Process(e =>
                {
                    if ((string)e.In.Body! == "b")
                        throw new InvalidOperationException("branch b fails after its block");
                })));
        var producer = await Start(context, "direct://uow-split");

        // Split stops on the first failed branch and fails the exchange (stopOnException defaults to true).
        var send = () => producer.Process(new Exchange(new Message(new object?[] { "a", "b", "c" })));
        await send.Should().ThrowAsync<InvalidOperationException>();

        (await repo.Contains("a")).Should().BeTrue("branch a completed; its redelivery is skipped");
        (await repo.Contains("b")).Should().BeFalse("its branch failed after the block");
        (await repo.Contains("c")).Should().BeFalse("the split stopped before branch c");
    }

    [Fact]
    public async Task A_rollback_takes_back_a_key_the_repository_keeps_outside_the_transaction()
    {
        var repo = new InMemoryIdempotentRepository();
        var work = 0;
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://uow-tx-rollback")
            .Transacted()
                .IdempotentConsumer(repo, MessageId)
                    .Process(_ =>
                    {
                        if (++work == 1)
                            throw new InvalidOperationException("the work fails, the transaction rolls back");
                    })
                .EndIdempotentConsumer()
            .End());
        var producer = await Start(context, "direct://uow-tx-rollback");

        var first = () => producer.Process(Delivery("m-7"));
        await first.Should().ThrowAsync<InvalidOperationException>();
        (await repo.Contains("m-7")).Should().BeFalse(
            "the work rolled back; an in-memory key is not part of the transaction and has to be removed");

        await producer.Process(Delivery("m-7"));
        work.Should().Be(2);
    }

    private sealed class KeyAtFailure(IIdempotentRepository repo, string key) : IRouteLifecycleListener
    {
        public bool? KeyPresentAtFailure { get; private set; }

        public async Task OnExchangeFailed(string routeId, IExchange exchange, Exception exception, CancellationToken ct) =>
            KeyPresentAtFailure = await repo.Contains(key, ct);
    }
}

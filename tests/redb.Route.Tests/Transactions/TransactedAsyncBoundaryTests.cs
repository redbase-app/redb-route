using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Transactions;

namespace redb.Route.Tests.Transactions;

/// <summary>
/// A transaction belongs to the flow that opened it. An exchange handed over asynchronously (<c>seda:</c>, <c>vm:</c>,
/// <c>.Threads()</c> for InOnly) is a unit of work of its own, and a copy that outlives its block must not see the block
/// as still running: a send registered there would be committed by nobody and lost.
/// </summary>
public class TransactedAsyncBoundaryTests : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly RouteContext _context;

    public TransactedAsyncBoundaryTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SharedVmRegistry());   // vm: endpoints need it
        _services = services.BuildServiceProvider();
        _context = new RouteContext(_services, "tx-boundary");
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        await _services.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private sealed class NoopAction : ITransactedAction
    {
        public Task Commit(CancellationToken ct = default) => Task.CompletedTask;
        public Task Rollback(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_copy_that_outlives_its_block_does_not_see_the_block_as_running()
    {
        IExchange? copy = null;

        await new TransactedProcessor(new DelegateProcessor(ex => copy = ex.Clone()), TransactionPolicy.Default)
            .Process(new Exchange(new Message("m")));

        TransactedActions.IsActive(copy!).Should().BeFalse("the block the copy came from has ended");
        var register = () => TransactedActions.Register(copy!, "late", new NoopAction(), "broker:late");
        register.Should().Throw<InvalidOperationException>("a send nobody would commit must fail, not vanish");
    }

    /// <summary>
    /// The sending block stays open until the other side has looked at the exchange, so what is checked is the hand-off
    /// itself, not a block that has already ended.
    /// </summary>
    [Theory]
    [InlineData("seda")]
    [InlineData("vm")]
    public async Task Exchange_handed_over_asynchronously_starts_outside_the_sending_block(string scheme)
    {
        var target = $"{scheme}://tx-boundary-{Guid.NewGuid():N}";
        bool? activeOnTheOtherSide = null;
        var looked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _context.AddRoutes(r =>
        {
            r.From($"direct://tx-boundary-{scheme}")
                .Transacted()
                    .To(target)
                    .Process((_, ct) => looked.Task.WaitAsync(TimeSpan.FromSeconds(10), ct))
                .End();
            r.From(target)
                .Process(ex =>
                {
                    activeOnTheOtherSide = TransactedActions.IsActive(ex);
                    looked.TrySetResult();
                });
        });
        await _context.Start();

        var producer = _context.GetEndpoint($"direct://tx-boundary-{scheme}").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("m")));

        activeOnTheOtherSide.Should().BeFalse($"{scheme}: is an asynchronous hand-off, a unit of work of its own");
    }

    [Fact]
    public async Task InOnly_threads_worker_starts_outside_the_sending_block()
    {
        bool? heldTheBlockSet = null;
        var looked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _context.AddRoutes(r =>
        {
            r.From("direct://tx-boundary-threads")
                .Transacted()
                    .Threads(2)
                        .Process(ex =>
                        {
                            heldTheBlockSet = ex.Properties.ContainsKey(TransactedProcessor.TransactActionPropertyKey);
                            looked.TrySetResult();
                        })
                .End();
        });
        await _context.Start();

        var producer = _context.GetEndpoint("direct://tx-boundary-threads").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("m")) { Pattern = ExchangePattern.InOnly });
        await looked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        heldTheBlockSet.Should().BeFalse(".Threads() for InOnly hands the exchange to a worker: a unit of work of its own");
    }
}

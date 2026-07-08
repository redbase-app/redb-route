using System.Collections.Concurrent;
using System.Transactions;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Verifies that parallel Splitter / Multicast branches run under a private
/// <see cref="DependentTransaction"/> clone of the ambient transaction instead of sharing one
/// <see cref="Transaction.Current"/> across threads (which System.Transactions forbids).
/// </summary>
public class ParallelTransactionIsolationTests
{
    [Fact]
    public async Task Multicast_Parallel_InsideTransaction_EachBranchUnderDependentClone()
    {
        var kinds = new ConcurrentBag<string>();
        var multicast = new MulticastProcessor(parallelProcessing: true)
            .AddTarget(new DelegateProcessor(_ => kinds.Add(TxKind())))
            .AddTarget(new DelegateProcessor(_ => kinds.Add(TxKind())))
            .AddTarget(new DelegateProcessor(_ => kinds.Add(TxKind())));

        using (var scope = new TransactionScope(
            TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            await multicast.Process(new Exchange(new Message("data")));
            scope.Complete(); // must not throw / block — all dependents already signalled Complete()
        }

        kinds.Should().HaveCount(3);
        kinds.Should().OnlyContain(k => k == nameof(DependentTransaction));
    }

    [Fact]
    public async Task Splitter_Parallel_InsideTransaction_EachBranchUnderDependentClone()
    {
        var kinds = new ConcurrentBag<string>();
        var splitter = new SplitterProcessor(
            ex => (IEnumerable<object?>)ex.In.Body!,
            new DelegateProcessor(_ => kinds.Add(TxKind())),
            parallelProcessing: true,
            stopOnException: false);

        using (var scope = new TransactionScope(
            TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            await splitter.Process(new Exchange(new Message(new object[] { "a", "b", "c", "d" })));
            scope.Complete();
        }

        kinds.Should().HaveCount(4);
        kinds.Should().OnlyContain(k => k == nameof(DependentTransaction));
    }

    [Fact]
    public async Task Multicast_Parallel_NoAmbientTransaction_IsPassthrough()
    {
        var sawTransaction = new ConcurrentBag<bool>();
        var multicast = new MulticastProcessor(parallelProcessing: true)
            .AddTarget(new DelegateProcessor(_ => sawTransaction.Add(Transaction.Current != null)))
            .AddTarget(new DelegateProcessor(_ => sawTransaction.Add(Transaction.Current != null)));

        await multicast.Process(new Exchange(new Message("data")));

        sawTransaction.Should().HaveCount(2);
        sawTransaction.Should().OnlyContain(seen => seen == false); // no scope created when no ambient TX
    }

    private static string TxKind() => Transaction.Current?.GetType().Name ?? "(none)";
}

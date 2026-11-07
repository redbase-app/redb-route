using System.Transactions;

namespace redb.Route.Transactions;

/// <summary>
/// Runs one branch of a parallel fan-out (Splitter / Multicast in parallel mode) under its own
/// <see cref="DependentTransaction"/> clone of the ambient transaction.
/// <para>
/// Parallel branches are dispatched with <c>Task.Run</c>, which flows the caller's
/// <see cref="System.Threading.ExecutionContext"/>. Because the route's <see cref="TransactionScope"/>
/// is created with <see cref="TransactionScopeAsyncFlowOption.Enabled"/>, every branch would
/// otherwise observe — and concurrently enlist resources in — the <em>same</em>
/// <see cref="Transaction.Current"/>. <see cref="System.Transactions"/> does not allow concurrent
/// use of a single transaction across threads: a second concurrent enlistment promotes to MSDTC or
/// throws <c>"transaction context in use by another thread"</c>.
/// </para>
/// <para>
/// Each branch instead gets a private <see cref="DependentTransaction"/> via
/// <see cref="Transaction.DependentClone"/>. <see cref="DependentCloneOption.BlockCommitUntilComplete"/>
/// makes the parent transaction's commit wait until every branch has signalled completion, so the
/// parent never commits work a branch is still writing. Broker transports that defer via
/// <c>TRANSACT_ACTION</c> (Kafka/Rabbit/Redis/ASB/AMQP) do not enlist and are unaffected either way;
/// this matters for resources that DO enlist (SQL/ADO.NET, redb DB work).
/// </para>
/// <para>No-op when there is no ambient transaction — the overwhelmingly common, non-transacted case.</para>
/// </summary>
internal static class DependentTransactionBranch
{
    /// <summary>
    /// Executes <paramref name="branch"/> isolated under a dependent clone of the ambient transaction.
    /// When no ambient transaction is present the branch runs directly with zero overhead.
    /// </summary>
    internal static async Task RunAsync(Func<Task> branch)
    {
        var ambient = Transaction.Current;
        if (ambient is null)
        {
            await branch().ConfigureAwait(false);
            return;
        }

        // Fork a dependent clone so this branch's resource enlistment is private to this thread.
        var dependent = ambient.DependentClone(DependentCloneOption.BlockCommitUntilComplete);

        // New ambient for this branch is the dependent; resources opened inside branch() enlist in it.
        using (var scope = new TransactionScope(dependent, TransactionScopeAsyncFlowOption.Enabled))
        {
            await branch().ConfigureAwait(false);
            scope.Complete();
        }

        // Signal the dependent is finished so the parent's BlockCommitUntilComplete commit can proceed.
        // Reached only on the success path: if branch() throws, the scope above disposes without
        // Complete(), which aborts the (whole) transaction — the parent is then released by the abort,
        // not by this signal, so there is no deadlock.
        dependent.Complete();
    }
}

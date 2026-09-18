using System.Transactions;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// The batch inside a route-level transaction (<c>.Transacted()</c> — an ambient <see cref="TransactionScope"/>). There is no
/// local transaction to take savepoints on, so continuing past an error cannot be honest and is refused before any write.
/// Runs on providers that enlist in System.Transactions.
/// </summary>
public abstract class AmbientTransactionBatchTestsBase : IAsyncLifetime
{
    private SqlE2EDatabase? _db;

    /// <summary>The provider under test.</summary>
    protected abstract SqlE2EProvider Provider { get; }

    private SqlE2EDatabase Db => _db ?? throw new InvalidOperationException("The table is created in InitializeAsync.");

    /// <inheritdoc />
    public async Task InitializeAsync() => _db = await SqlE2EDatabase.CreateAsync(Provider);

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        try
        {
            if (_db is not null)
                await _db.DisposeAsync();
        }
        finally
        {
            (Provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task BatchContinue_UnderTransactionScope_FailsBeforeFirstWrite()
    {
        Exception? thrown;
        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            (_, thrown) = await SqlBatchRun.RunAsync(Db, Provider.InsertSql(Db.Table), Provider.DuplicateKeyItems(), breakOnError: false);

            // A route completes its transaction when the pipeline did not fail.
            if (thrown is null)
                scope.Complete();
        }

        thrown.Should().BeOfType<InvalidOperationException>(
            $"continue mode needs a local transaction with savepoints, but the batch threw {Outcome.Describe(thrown)}");
        (await Db.ReadIdsAsync()).Should().BeEmpty("the refusal comes before anything is written");
    }

    [Fact]
    public async Task BatchBreak_UnderTransactionScopeNotCompleted_LeavesNoRows()
    {
        var items = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1, ["val"] = "a" },
            new() { ["id"] = 2, ["val"] = "b" },
            new() { ["id"] = 3, ["val"] = "c" },
        };

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            var (_, thrown) = await SqlBatchRun.RunAsync(Db, Provider.InsertSql(Db.Table), items, breakOnError: true);
            thrown.Should().BeNull($"the batch itself succeeds ({Outcome.Describe(thrown)})");

            // A step after the SQL endpoint fails: the route does not complete its transaction.
        }

        (await Db.ReadIdsAsync()).Should().BeEmpty("the batch wrote inside the route's transaction, which rolled back");
    }
}

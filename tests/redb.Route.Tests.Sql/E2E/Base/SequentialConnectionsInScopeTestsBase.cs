using System.Transactions;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// Two <c>sql:</c> steps in one route transaction, as the connector runs them: each step opens a connection, runs its
/// statement and closes the connection before the next step opens one. Measured per driver: whether the second
/// connection reuses the first one's enlistment and the scope commits both statements, or the second open needs a
/// distributed transaction.
/// </summary>
public abstract class SequentialConnectionsInScopeTestsBase : IAsyncLifetime
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
    public async Task Two_steps_one_after_the_other_inside_one_scope_commit_both_statements()
    {
        var failure = await Outcome.Of(async () =>
        {
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            await using (var first = await Db.OpenAsync())
                await SqlE2EDatabase.ExecuteAsync(first, null, Provider.InsertLiteral(Db.Table, 1, "a"));
            await using (var second = await Db.OpenAsync())
                await SqlE2EDatabase.ExecuteAsync(second, null, Provider.InsertLiteral(Db.Table, 2, "b"));
            scope.Complete();
        });

        failure.Should().BeNull($"the second step reuses the first one's enlistment ({Outcome.Describe(failure)})");
        (await Db.ReadIdsAsync()).Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public async Task Two_steps_one_after_the_other_inside_one_scope_roll_back_together()
    {
        var failure = await Outcome.Of(async () =>
        {
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            await using (var first = await Db.OpenAsync())
                await SqlE2EDatabase.ExecuteAsync(first, null, Provider.InsertLiteral(Db.Table, 1, "a"));
            await using (var second = await Db.OpenAsync())
                await SqlE2EDatabase.ExecuteAsync(second, null, Provider.InsertLiteral(Db.Table, 2, "b"));
            // Not completed: the route failed after both steps.
        });

        failure.Should().BeNull(Outcome.Describe(failure));
        (await Db.ReadIdsAsync()).Should().BeEmpty();
    }
}

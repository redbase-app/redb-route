using System.Transactions;
using redb.Route.Sql.Connection;
using redb.Route.Sql.Repositories;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// The raw-ADO repositories create their table on first use. That first use may happen inside the transaction of whatever
/// message came first (a <c>.Transacted()</c> route); if that transaction rolls back, the table must still be there for the
/// next call. On PostgreSQL and SQL Server DDL is transactional, so a table created inside the message's transaction goes
/// with its rollback.
/// </summary>
public abstract class SqlRepositoryBootstrapAmbientTestsBase : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private readonly List<string> _tables = [];

    /// <summary>The provider under test.</summary>
    protected abstract SqlE2EProvider Provider { get; }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try
        {
            await using var connection = await Factory().CreateConnectionAsync();
            foreach (var table in _tables)
                await Outcome.Of(() => SqlE2EDatabase.ExecuteAsync(connection, null, $"DROP TABLE {table}"));
        }
        finally
        {
            (Provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task Idempotent_FirstCallInARolledBackTransaction_TheTableStays()
    {
        var table = Track("rsql_idem_" + _suffix);
        var repository = new SqlIdempotentRepository(Factory(), new SqlIdempotentOptions { ProcessorName = "route-1", TableName = table });

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            (await repository.Add("k1")).Should().BeTrue();
            // The route failed: the scope ends without Complete().
        }

        var next = await Outcome.Of(() => repository.Add("k2"));

        next.Should().BeNull("the repository's table does not belong to the message that happened to come first: " +
                             Outcome.Describe(next));
    }

    [Fact]
    public async Task ClaimCheck_FirstCallInARolledBackTransaction_TheTableStays()
    {
        var table = Track("rsql_claim_" + _suffix);
        var repository = new SqlClaimCheckRepository(Factory(), new SqlClaimCheckOptions { TableName = table, CleanupInterval = 0 });

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await repository.Store("claim-1", new byte[] { 1 });
        }

        var next = await Outcome.Of(() => repository.Store("claim-2", new byte[] { 2 }));

        next.Should().BeNull("the repository's table does not belong to the message that happened to come first: " +
                             Outcome.Describe(next));
    }

    private string Track(string table)
    {
        _tables.Add(table);
        return table;
    }

    private ISqlConnectionFactory Factory() =>
        new SqlConnectionFactory(new SqlConnectionOptions { ConnectionString = Provider.ConnectionString, ProviderFactory = Provider.Factory });
}

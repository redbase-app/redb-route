using System.Transactions;
using redb.Route.Abstractions;
using redb.Route.Sql.Connection;
using redb.Route.Sql.Repositories;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// <see cref="SqlIdempotentRepository.JoinsAmbientTransaction"/> tells the idempotent consumer whether a rollback of the
/// route transaction takes the key back by itself. It must say what the driver does: a false "no" makes the consumer delete
/// the key after the rollback, and in a cluster that delete can hit the key another node has claimed meanwhile; a false
/// "yes" leaves a key behind whose work never committed, and the redelivery is skipped as a duplicate.
/// </summary>
public abstract class SqlIdempotentAmbientTestsBase : IAsyncLifetime
{
    private readonly string _table = "rsql_idem_" + Guid.NewGuid().ToString("N")[..12];
    private readonly List<string> _connectionStrings = [];

    /// <summary>The provider under test.</summary>
    protected abstract SqlE2EProvider Provider { get; }

    /// <summary>Whether the provider's connections enlist in an ambient transaction with the default connection string.</summary>
    protected abstract bool EnlistsByDefault { get; }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try
        {
            if (_connectionStrings.Count > 0)
            {
                await using var connection = await Factory(_connectionStrings[0]).CreateConnectionAsync();
                await SqlE2EDatabase.ExecuteAsync(connection, null, $"DROP TABLE {_table}");
            }
        }
        finally
        {
            (Provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task JoinsAmbientTransaction_SaysWhatTheRollbackDoesWithTheKey()
    {
        var repository = Repository(Provider.ConnectionString);
        await repository.Add("warm-up");   // creates the table outside any transaction

        var keptAfterRollback = await AddInRolledBackScopeAsync(repository, "k1");

        ((IIdempotentRepository)repository).JoinsAmbientTransaction.Should().Be(EnlistsByDefault, $"{Provider.Name} with its default connection string");
        ((IIdempotentRepository)repository).JoinsAmbientTransaction.Should().Be(!keptAfterRollback,
            "the flag must match what the rollback did with the key: taken back when the connection enlisted, kept otherwise");
    }

    /// <summary><c>Enlist=false</c> keeps the connection out of the ambient transaction; the flag follows it.</summary>
    protected async Task AssertEnlistOffDoesNotJoin()
    {
        var repository = Repository(Provider.ConnectionString + ";Enlist=false");
        await repository.Add("warm-up");

        var keptAfterRollback = await AddInRolledBackScopeAsync(repository, "k1");

        keptAfterRollback.Should().BeTrue("with Enlist=false the insert commits on its own, outside the route transaction");
        ((IIdempotentRepository)repository).JoinsAmbientTransaction.Should().BeFalse("the consumer has to remove the key itself after the rollback");
    }

    private static async Task<bool> AddInRolledBackScopeAsync(SqlIdempotentRepository repository, string key)
    {
        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            (await repository.Add(key)).Should().BeTrue();
            // The route failed: the scope ends without Complete().
        }

        return await repository.Contains(key);
    }

    [Fact]
    public async Task Add_ThatLosesTheKeyToAConcurrentInsert_IsADuplicate_NotAnError()
    {
        // Another session inserts the key and commits while node A's insert of it is under way. PostgreSQL then fails
        // node A's insert on the primary key (its existence check could not see the uncommitted row); SQL Server and SQLite
        // wait on the row and find it. Either way the key is held: a duplicate, not an exception for the route.
        var nodeA = Repository(Provider.ConnectionString);
        await nodeA.Add("warm-up");   // creates the table
        await using var other = await Factory(Provider.ConnectionString).CreateConnectionAsync();
        await using var otherTx = await other.BeginTransactionAsync();
        await SqlE2EDatabase.ExecuteAsync(other, otherTx,
            $"INSERT INTO {_table} (processor_name, message_key, created_at, confirmed) VALUES ('route-1', 'race', '2026-01-01T00:00:00.0000000+00:00', 0)");

        var addA = Task.Run(() => nodeA.Add("race"));
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await otherTx.CommitAsync();

        (await addA).Should().BeFalse("the other session's row holds the key");
    }

    private SqlIdempotentRepository Repository(string connectionString)
    {
        _connectionStrings.Add(connectionString);
        return new SqlIdempotentRepository(Factory(connectionString),
            new SqlIdempotentOptions { ProcessorName = "route-1", TableName = _table });
    }

    private ISqlConnectionFactory Factory(string connectionString) =>
        new SqlConnectionFactory(new SqlConnectionOptions { ConnectionString = connectionString, ProviderFactory = Provider.Factory });
}

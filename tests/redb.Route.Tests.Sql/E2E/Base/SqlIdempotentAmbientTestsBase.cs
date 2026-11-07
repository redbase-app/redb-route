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

    private SqlIdempotentRepository Repository(string connectionString)
    {
        _connectionStrings.Add(connectionString);
        return new SqlIdempotentRepository(Factory(connectionString),
            new SqlIdempotentOptions { ProcessorName = "route-1", TableName = _table });
    }

    private ISqlConnectionFactory Factory(string connectionString) =>
        new SqlConnectionFactory(new SqlConnectionOptions { ConnectionString = connectionString, ProviderFactory = Provider.Factory });
}

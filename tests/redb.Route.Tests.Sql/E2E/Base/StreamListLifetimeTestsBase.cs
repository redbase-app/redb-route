using System.Data.Common;
using System.Transactions;
using redb.Route.Core;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// The lifetime of a streamed result (<c>outputType=StreamList</c>) against real servers. The connector must release a
/// stream the route never read when the exchange ends. The provider facts below — measured, not wished — are why a stream is
/// refused inside a route transaction: a transaction does not commit while a reader is open, a volatile enlistment cannot
/// close the reader in time on every driver, and a second connection in the same scope needs a distributed transaction.
/// </summary>
public abstract class StreamListLifetimeTestsBase : IAsyncLifetime
{
    private SqlE2EDatabase? _db;
    private SqlE2EDatabase? _target;

    /// <summary>The provider under test.</summary>
    protected abstract SqlE2EProvider Provider { get; }

    private SqlE2EDatabase Db => _db ?? throw new InvalidOperationException("The table is created in InitializeAsync.");
    private SqlE2EDatabase Target => _target ?? throw new InvalidOperationException("The table is created in InitializeAsync.");
    private string Select => $"SELECT id, val FROM {Db.Table} ORDER BY id";

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _db = await SqlE2EDatabase.CreateAsync(Provider);
        _target = await SqlE2EDatabase.CreateAsync(Provider);
        for (var id = 1; id <= 3; id++)
            await Db.ExecuteAsync(Provider.InsertLiteral(Db.Table, id, "v" + id));
    }

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
            try
            {
                if (_target is not null)
                    await _target.DisposeAsync();
            }
            finally
            {
                (Provider as IDisposable)?.Dispose();
            }
        }
    }

    [Fact]
    public async Task StreamNotConsumed_ExchangeDisposed_ConnectionReleased()
    {
        var factory = Db.CreateConnectionFactory();
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, factory, Select, new() { ["outputType"] = "StreamList" });
        var exchange = new Exchange(new Message("go"));

        await endpoint.CreateProducer().Process(exchange, CancellationToken.None);
        await exchange.DisposeAsync();

        factory.OpenConnections.Should().Be(0,
            "a streamed result the route never read releases its reader, command, transaction and connection when the exchange ends");
    }

    [Fact]
    public async Task ReaderOpen_ScopeCompletes_TransactionInDoubt()
    {
        // The connection outlives the scope, as a streamed result's connection would: the scope commits with the reader open.
        DbConnection? connection = null;
        DbCommand? command = null;
        var thrown = await Outcome.Of(async () =>
        {
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            connection = await Db.OpenAsync();
            command = connection.CreateCommand();
            command.CommandText = Select;
            var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            scope.Complete();
        });

        try
        {
            thrown.Should().BeOfType<TransactionInDoubtException>(
                $"the transaction cannot commit while the reader is still open on its connection ({Outcome.Describe(thrown)})");
        }
        finally
        {
            command?.Dispose();
            if (connection is not null)
                await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReaderClosedByVolatileEnlistmentOnPrepare_CommitsOnlyWhereDriverPreparesItFirst()
    {
        DbConnection? connection = null;
        var thrown = await Outcome.Of(async () =>
        {
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            connection = await Db.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = Select;
            var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            Transaction.Current!.EnlistVolatile(new CloseOnPrepare(reader, command), EnlistmentOptions.None);
            scope.Complete();
        });

        try
        {
            if (Provider.ReaderClosedOnPrepareLetsScopeCommit)
                thrown.Should().BeNull(Outcome.Describe(thrown));
            else
                thrown.Should().BeAssignableTo<TransactionException>(
                    $"the driver commits before the volatile enlistment closes the reader ({Outcome.Describe(thrown)})");
        }
        finally
        {
            if (connection is not null)
                await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReaderOnOneConnection_WriteOnSecond_InsideOneScope_CannotCommit()
    {
        Exception? writeError = null;
        var scopeError = await Outcome.Of(async () =>
        {
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            await using var readConnection = await Db.OpenAsync();
            await using var readCommand = readConnection.CreateCommand();
            readCommand.CommandText = Select;
            await using var reader = await readCommand.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();

            // What a batch fed by a streamed result would do: a second connection writes while the first still reads.
            writeError = await Outcome.Of(async () =>
            {
                await using var writeConnection = await Target.OpenAsync();
                await SqlE2EDatabase.ExecuteAsync(writeConnection, null, Provider.InsertLiteral(Target.Table, 1, "w"));
            });

            while (await reader.ReadAsync()) { }
            if (writeError is null)
                scope.Complete();
        });

        (writeError ?? scopeError).Should().BeAssignableTo<TransactionException>(
            $"two connections at once in one scope need a distributed transaction (write: {Outcome.Describe(writeError)}, scope: {Outcome.Describe(scopeError)})");
        (await Target.ReadIdsAsync()).Should().BeEmpty();
    }

    /// <summary>A volatile resource manager that closes a reader and its command when the transaction prepares.</summary>
    private sealed class CloseOnPrepare(DbDataReader reader, DbCommand command) : IEnlistmentNotification
    {
        public void Prepare(PreparingEnlistment preparingEnlistment)
        {
            Close();
            preparingEnlistment.Prepared();
        }

        public void Commit(Enlistment enlistment) => enlistment.Done();

        public void Rollback(Enlistment enlistment)
        {
            Close();
            enlistment.Done();
        }

        public void InDoubt(Enlistment enlistment) => enlistment.Done();

        private void Close()
        {
            reader.Dispose();
            command.Dispose();
        }
    }
}

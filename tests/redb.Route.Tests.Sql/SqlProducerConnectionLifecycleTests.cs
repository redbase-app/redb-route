using System.Data;
using System.Data.Common;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql.Connection;
using Xunit;

namespace redb.Route.Tests.Sql;

/// <summary>
/// A connection the producer opened must be back in the pool whatever happens next. This is the
/// hot path for audit and usage writes, so a leak here is not a slow drip — it is the pool
/// draining under the exact conditions (dropped sockets, cancellations) that come in bursts.
/// <para>
/// Two holes, both found by review: <c>BeginTransactionAsync</c> ran before the <c>try</c>, so a
/// throw there lost the connection with no <c>finally</c> to catch it; and the StreamList branch
/// skipped disposal by <em>option value</em> rather than by whether ownership actually reached the
/// stream, so any error before the handoff — the reader failing to open, say — leaked connection
/// and transaction both.
/// </para>
/// </summary>
public sealed class SqlProducerConnectionLifecycleTests
{
    // ── Fakes: a connection that fails at a chosen point and records its disposal ──

    private sealed class RecordingFactory(DbConnection connection) : ISqlConnectionFactory
    {
        public Task<DbConnection> CreateConnectionAsync(bool readOnly = false, CancellationToken ct = default)
            => Task.FromResult(connection);
    }

    private sealed class FakeConnection : DbConnection
    {
        public bool Disposed;
        public bool FailOnBeginTransaction;
        public bool FailOnExecuteReader;
        public FakeTransaction? Transaction;

        public override string ConnectionString { get; set; } = "";
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "0";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            if (FailOnBeginTransaction)
                throw new InvalidOperationException("socket died while starting the transaction");
            return Transaction = new FakeTransaction(this, isolationLevel);
        }

        protected override DbCommand CreateDbCommand() => new FakeCommand(this);

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return base.DisposeAsync();
        }
    }

    private sealed class FakeTransaction(FakeConnection connection, IsolationLevel level) : DbTransaction
    {
        public bool Disposed;
        public override IsolationLevel IsolationLevel => level;
        protected override DbConnection DbConnection => connection;
        public override void Commit() { }
        public override void Rollback() { }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return base.DisposeAsync();
        }
    }

    private sealed class FakeCommand(FakeConnection connection) : DbCommand
    {
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection
            => throw new NotSupportedException("the test SQL carries no :parameters");
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() { }
        public override int ExecuteNonQuery() => 1;
        public override object? ExecuteScalar() => 1;
        protected override DbParameter CreateDbParameter()
            => throw new NotSupportedException("the test SQL carries no :parameters");

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            if (connection.FailOnExecuteReader)
                throw new InvalidOperationException("reader refused to open");
            throw new NotSupportedException("these tests only exercise the failure path");
        }
    }

    // ── Harness ──

    private static async Task<IProducer> Producer(RouteContext context, FakeConnection connection, string uri, string from)
    {
        context.AddComponent(new redb.Route.Sql.SqlComponent());
        context.AddToRegistry("lifecycle", (ISqlConnectionFactory)new RecordingFactory(connection));
        context.AddRoutes(r => r.From(from).To(uri));
        await context.Start();
        var producer = context.GetEndpoint(from).CreateProducer();
        await producer.Start();
        return producer;
    }

    // ── The holes ──

    [Fact]
    public async Task A_connection_whose_transaction_never_started_is_still_returned()
    {
        var connection = new FakeConnection { FailOnBeginTransaction = true };
        await using var context = new RouteContext();
        var producer = await Producer(context, connection,
            "sql:INSERT INTO audit (x) VALUES (1)?dataSource=lifecycle&outputType=None", "direct:sql-txfail");

        var act = () => producer.Process(new Exchange(new Message("m")));
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*socket died*");

        connection.Disposed.Should().BeTrue(
            "a throw from BeginTransactionAsync (dropped socket, cancellation) must not strand the connection");
    }

    [Fact]
    public async Task A_stream_that_never_received_ownership_does_not_keep_the_connection()
    {
        // StreamList hands connection, command and transaction to the stream — but only once the
        // reader opened. An error before that handoff must be cleaned up by the producer itself:
        // skipping disposal because the OPTION says StreamList leaks all three on any such error.
        var connection = new FakeConnection { FailOnExecuteReader = true };
        await using var context = new RouteContext();
        var producer = await Producer(context, connection,
            "sql:SELECT * FROM orders?dataSource=lifecycle&outputType=StreamList", "direct:sql-streamfail");

        var act = () => producer.Process(new Exchange(new Message("m")));
        await act.Should().ThrowAsync<InvalidOperationException>();

        connection.Disposed.Should().BeTrue("ownership never reached the stream, so the producer still owns the cleanup");
        connection.Transaction!.Disposed.Should().BeTrue();
    }
}

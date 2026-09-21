using System.Data;
using System.Data.Common;
using System.Transactions;
using Microsoft.Data.Sqlite;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Sql.Connection;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql;

/// <summary>
/// A streamed result (<c>outputType=StreamList</c>) holds a reader, its command, a transaction and a connection past the
/// endpoint. As in Apache Camel, they are released when the exchange ends, whether or not the route read the stream and
/// whatever became of the body; the stream reads once; inside a route transaction it is refused, because the transaction
/// cannot commit while the reader is open.
/// </summary>
public sealed class SqlProducerStreamListLifetimeTests : IDisposable
{
    private const string Select = "SELECT id, val FROM stream_items ORDER BY id";

    private readonly SqliteTestHelper _db = new();

    public SqlProducerStreamListLifetimeTests()
    {
        _db.Execute("CREATE TABLE stream_items (id INTEGER NOT NULL, val TEXT NOT NULL)");
        _db.Execute("INSERT INTO stream_items (id, val) VALUES (1, 'a'), (2, 'b'), (3, 'c')");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task StreamList_NotConsumed_ExchangeDisposed_ConnectionClosed()
    {
        var factory = new TrackingFactory(_db.ConnectionString);
        var exchange = await ProduceAsync(factory);
        factory.Open.Should().Be(1, "the stream keeps its connection while the route runs");

        await exchange.DisposeAsync();

        factory.Open.Should().Be(0, "the exchange ended: the stream nobody read releases its connection");
    }

    [Fact]
    public async Task StreamList_CopyOfTheExchangeDisposed_TheStreamStaysWithTheOriginal()
    {
        var factory = new TrackingFactory(_db.ConnectionString);
        var exchange = await ProduceAsync(factory);

        // WireTap, RecipientList, Enrich, Threads: a copy shares the body and is disposed when its own work is done.
        var copy = exchange.CloneLinked();
        copy.In.Body.Should().BeSameAs(exchange.In.Body);
        await copy.DisposeAsync();

        factory.Open.Should().Be(1, "the stream belongs to the exchange that registered it, not to a copy that shares the body");
        var ids = new List<object?>();
        await foreach (var row in (IAsyncEnumerable<Dictionary<string, object?>>)exchange.In.Body!)
            ids.Add(row["id"]);
        ids.Should().Equal(1L, 2L, 3L);
        await exchange.DisposeAsync();
        factory.Open.Should().Be(0);
    }

    [Fact]
    public async Task StreamList_BodyReplaced_ExchangeDisposed_ConnectionClosed()
    {
        var factory = new TrackingFactory(_db.ConnectionString);
        var exchange = await ProduceAsync(factory);
        exchange.In.Body = "replaced by a later step";

        await exchange.DisposeAsync();

        factory.Open.Should().Be(0, "release is tied to the exchange, not to whatever is the body when it ends");
    }

    [Fact]
    public async Task StreamList_InOutputHeader_ExchangeDisposed_ConnectionClosed()
    {
        var factory = new TrackingFactory(_db.ConnectionString);
        var exchange = await ProduceAsync(factory, new() { ["outputHeader"] = "rows" });
        exchange.In.Headers["rows"].Should().NotBeNull();

        await exchange.DisposeAsync();

        factory.Open.Should().Be(0);
    }

    [Fact]
    public async Task StreamList_FullyConsumed_ConnectionClosedAtEndOfStream()
    {
        var factory = new TrackingFactory(_db.ConnectionString);
        var exchange = await ProduceAsync(factory);

        var ids = new List<object?>();
        await foreach (var row in (IAsyncEnumerable<Dictionary<string, object?>>)exchange.In.Body!)
            ids.Add(row["id"]);

        ids.Should().Equal(1L, 2L, 3L);
        factory.Open.Should().Be(0, "reading to the end releases the connection at once");
        await exchange.DisposeAsync();
        factory.Open.Should().Be(0);
    }

    [Fact]
    public async Task StreamList_EarlyExit_ConnectionClosed()
    {
        var factory = new TrackingFactory(_db.ConnectionString);
        var exchange = await ProduceAsync(factory);

        await foreach (var _ in (IAsyncEnumerable<Dictionary<string, object?>>)exchange.In.Body!)
            break;

        factory.Open.Should().Be(0, "leaving the loop disposes the enumerator, which releases the connection");
        await exchange.DisposeAsync();
    }

    [Fact]
    public async Task StreamList_ReadTwice_SecondReadFails()
    {
        var factory = new TrackingFactory(_db.ConnectionString);
        var exchange = await ProduceAsync(factory);
        var stream = (IAsyncEnumerable<Dictionary<string, object?>>)exchange.In.Body!;
        await foreach (var _ in stream) { }

        var thrown = await Outcome.Of(async () =>
        {
            await foreach (var _ in stream) { }
        });

        thrown.Should().BeOfType<InvalidOperationException>(Outcome.Describe(thrown))
            .Which.Message.Should().Contain("once");
        await exchange.DisposeAsync();
    }

    [Fact]
    public async Task StreamList_InsideTransactionScope_RefusedBeforeOpeningConnection()
    {
        var factory = new TrackingFactory(_db.ConnectionString);
        Exception? thrown;
        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            thrown = await Outcome.Of(() => ProduceAsync(factory));
        }

        thrown.Should().BeOfType<InvalidOperationException>(Outcome.Describe(thrown))
            .Which.Message.Should().Contain("StreamList");
        factory.Requests.Should().Be(0, "the refusal comes before any connection is opened");
    }

    // ── ProducerTemplate: the request ends the exchange that owns the stream ──

    [Fact]
    public async Task StreamList_RequestBody_IsRefusedAtTheCall_AndTheConnectionIsReturned()
    {
        var factory = new TrackingFactory(_db.ConnectionString);
        await using var context = await StartStreamRouteAsync(factory, "direct:stream-body");
        var template = new ProducerTemplate(context);
        template.Start();
        try
        {
            var thrown = await Outcome.Of(() => template.RequestBody("direct:stream-body", "go"));

            thrown.Should().BeOfType<InvalidOperationException>(
                    "RequestBody ends the exchange before it returns, and the stream reads from that exchange's connection: " +
                    Outcome.Describe(thrown))
                .Which.Message.Should().Contain("RequestAsync", "the message points to the call that keeps the exchange open");
            factory.Open.Should().Be(0, "the refused reply's exchange was still released, with the connection behind the stream");
        }
        finally
        {
            template.Stop();
        }
    }

    [Fact]
    public async Task StreamList_RequestAsync_ReadThenDispose_ReadsEveryRow()
    {
        var factory = new TrackingFactory(_db.ConnectionString);
        await using var context = await StartStreamRouteAsync(factory, "direct:stream-async");
        var template = new ProducerTemplate(context);
        template.Start();
        try
        {
            var ids = new List<object?>();
            await using (var exchange = await template.RequestAsync("direct:stream-async", new Exchange(new Message("go"))))
            {
                var body = exchange.Out?.Body ?? exchange.In.Body;
                await foreach (var row in (IAsyncEnumerable<Dictionary<string, object?>>)body!)
                    ids.Add(row["id"]);
            }

            ids.Should().Equal(1L, 2L, 3L);
            factory.Open.Should().Be(0);
        }
        finally
        {
            template.Stop();
        }
    }

    private static async Task<RouteContext> StartStreamRouteAsync(TrackingFactory factory, string from)
    {
        var context = new RouteContext();
        context.AddComponent(new DirectComponent());
        context.AddComponent(new SqlComponent());
        context.AddToRegistry("stream-db", (ISqlConnectionFactory)factory);
        context.AddRoutes(r => r.From(from).To($"sql:{Select}?dataSource=stream-db&outputType=StreamList"));
        await context.Start();
        return context;
    }

    private async Task<Exchange> ProduceAsync(TrackingFactory factory, Dictionary<string, string>? extra = null)
    {
        var parameters = new Dictionary<string, string> { ["outputType"] = "StreamList" };
        if (extra is not null)
            foreach (var (key, value) in extra)
                parameters[key] = value;

        var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, factory, Select, parameters);
        var exchange = new Exchange(new Message("go"));
        await endpoint.CreateProducer().Process(exchange, CancellationToken.None);
        return exchange;
    }

    /// <summary>SQLite connections that count requests and the connections still open.</summary>
    private sealed class TrackingFactory(string connectionString) : ISqlConnectionFactory
    {
        private int _open;

        public int Requests { get; private set; }
        public int Open => Volatile.Read(ref _open);

        public async Task<DbConnection> CreateConnectionAsync(bool readOnly = false, CancellationToken ct = default)
        {
            Requests++;
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            Interlocked.Increment(ref _open);
            connection.StateChange += (_, change) =>
            {
                if (change.OriginalState == ConnectionState.Open && change.CurrentState == ConnectionState.Closed)
                    Interlocked.Decrement(ref _open);
            };
            return connection;
        }
    }
}

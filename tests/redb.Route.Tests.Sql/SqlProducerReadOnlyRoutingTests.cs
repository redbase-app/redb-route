using System.Data.Common;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Sql.Connection;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql;

/// <summary>
/// The read-only hint decides whether a data source with a replica hands out the replica connection. Only the endpoint's
/// author knows that a statement does not write — a SELECT can call a function that writes, take a lock or advance a
/// sequence — so the hint is the explicit <c>readOnly=true</c> option, never a guess from the SQL text or the output type.
/// </summary>
public sealed class SqlProducerReadOnlyRoutingTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    public SqlProducerReadOnlyRoutingTests()
    {
        _db.Execute("CREATE TABLE ro_items (id INTEGER PRIMARY KEY AUTOINCREMENT, val TEXT)");
        _db.Execute("INSERT INTO ro_items (val) VALUES ('a')");
    }

    public void Dispose() => _db.Dispose();

    // ── Execute ─────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_InsertReturningScalar_UsesPrimaryConnection()
    {
        var factory = new ReadOnlyRecordingFactory(_db);
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, factory,
            "INSERT INTO ro_items (val) VALUES (:#val) RETURNING id", new() { ["outputType"] = "Scalar" });
        var exchange = new Exchange(new Message());
        exchange.In.Headers["val"] = "x";

        await endpoint.CreateProducer().Process(exchange, CancellationToken.None);

        Convert.ToInt64(_db.ExecuteScalar("SELECT COUNT(*) FROM ro_items")).Should().Be(2);
        factory.ReadOnlyRequests.Should().Equal(new[] { false }, "an INSERT must not be routed to a read replica");
    }

    [Fact]
    public async Task Execute_SelectWithoutReadOnly_UsesPrimary()
    {
        var factory = await ProduceAsync("SELECT id, val FROM ro_items", new() { ["outputType"] = "SelectList" });

        factory.ReadOnlyRequests.Should().Equal(new[] { false }, "a replica is used only when the endpoint declares readOnly=true");
    }

    [Fact]
    public async Task Execute_ReadOnlyTrue_UsesReplica()
    {
        var factory = await ProduceAsync("SELECT COUNT(*) FROM ro_items", new() { ["outputType"] = "Scalar", ["readOnly"] = "true" });

        factory.ReadOnlyRequests.Should().Equal(true);
    }

    [Fact]
    public async Task Execute_ReadOnlyTrue_DecidesWhateverTheOutputType()
    {
        var factory = await ProduceAsync("SELECT COUNT(*) FROM ro_items", new() { ["outputType"] = "None", ["readOnly"] = "true" });

        factory.ReadOnlyRequests.Should().Equal(new[] { true }, "the option, not the output type, picks the replica");
    }

    // ── Procedure ───────────────────────────────────────────────────

    [Fact]
    public async Task Procedure_ReadOnlyTrue_UsesReplica()
    {
        var factory = await ProduceAsync("ro_fn", new() { ["mode"] = "Procedure", ["asFunction"] = "true", ["readOnly"] = "true" });

        factory.ReadOnlyRequests.Should().Equal(new[] { true }, "a procedure the author declares read-only may run on the replica");
    }

    [Fact]
    public async Task Procedure_Default_UsesPrimary()
    {
        var factory = await ProduceAsync("ro_fn", new() { ["mode"] = "Procedure", ["asFunction"] = "true" });

        factory.ReadOnlyRequests.Should().Equal(false);
    }

    // ── Poll ────────────────────────────────────────────────────────

    [Fact]
    public async Task Poll_Default_UsesPrimary()
    {
        var factory = await PollAsync(new());

        factory.ReadOnlyRequests.Should().Equal(new[] { false }, "a poll reads from the replica only with readOnly=true");
    }

    [Fact]
    public async Task Poll_ReadOnlyTrue_UsesReplica()
    {
        var factory = await PollAsync(new() { ["readOnly"] = "true" });

        factory.ReadOnlyRequests.Should().Equal(true);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private async Task<ReadOnlyRecordingFactory> ProduceAsync(string sql, Dictionary<string, string> parameters)
    {
        var factory = new ReadOnlyRecordingFactory(_db);
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, factory, sql, parameters);

        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(new Exchange(new Message()), CancellationToken.None));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        return factory;
    }

    private async Task<ReadOnlyRecordingFactory> PollAsync(Dictionary<string, string> extra)
    {
        var factory = new ReadOnlyRecordingFactory(_db);
        await using var context = new RouteContext();
        var parameters = new Dictionary<string, string> { ["mode"] = "Poll", ["repeatCount"] = "1" };
        foreach (var (key, value) in extra)
            parameters[key] = value;
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, factory, "SELECT id, val FROM ro_items", parameters);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var thrown = await Outcome.Of(() => ((SqlConsumer)endpoint.CreateConsumer(processor)).Poll(CancellationToken.None));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        return factory;
    }

    /// <summary>Records the read-only hint of every request; connections have the function <c>ro_fn()</c>.</summary>
    private sealed class ReadOnlyRecordingFactory(SqliteTestHelper db) : ISqlConnectionFactory
    {
        public List<bool> ReadOnlyRequests { get; } = [];

        public Task<DbConnection> CreateConnectionAsync(bool readOnly = false, CancellationToken ct = default)
        {
            ReadOnlyRequests.Add(readOnly);
            var connection = db.CreateConnection();
            connection.CreateFunction("ro_fn", () => 1L);
            return Task.FromResult<DbConnection>(connection);
        }
    }
}

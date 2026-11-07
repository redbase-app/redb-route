using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql;

/// <summary>
/// <c>outputType=Auto</c> is decided by the statement that runs — the <c>query</c> option or a named query — not by the URI
/// path, which is then only a label.
/// </summary>
public sealed class SqlProducerAutoOutputTypeTests : IDisposable
{
    private const string Select = "SELECT id, val FROM auto_items ORDER BY id";
    private readonly SqliteTestHelper _db = new();

    public SqlProducerAutoOutputTypeTests()
    {
        _db.Execute("CREATE TABLE auto_items (id INTEGER PRIMARY KEY, val TEXT)");
        _db.Execute("INSERT INTO auto_items (id, val) VALUES (1, 'a'), (2, 'b')");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Auto_QueryOptionSelect_ReturnsRows()
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), "items", new() { ["query"] = Select });

        var exchange = await ProcessAsync(endpoint);

        Values(exchange).Should().Equal(new object?[] { "a", "b" }, "a SELECT in the query option returns its rows");
    }

    [Fact]
    public async Task Auto_QueryOptionExpressionSelect_ReturnsRows()
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), "items", new() { ["query"] = "${header.sql}" });

        var exchange = await ProcessAsync(endpoint, e => e.In.Headers["sql"] = Select);

        Values(exchange).Should().Equal(new object?[] { "a", "b" }, "the statement the expression produced decides the output type");
    }

    [Fact]
    public async Task Auto_NamedQuerySelect_ReturnsRows()
    {
        var registry = new SqlNamedQueryRegistry();
        registry.Register("allItems", Select);
        await using var context = new RouteContext();
        context.AddService(typeof(ISqlNamedQueryRegistry), registry);
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), "ref:allItems");

        var exchange = await ProcessAsync(endpoint);

        Values(exchange).Should().Equal(new object?[] { "a", "b" }, "a named SELECT returns its rows");
    }

    [Fact]
    public async Task Auto_SelectPathButQueryOptionInsert_ReportsUpdateCount()
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), "SELECT 1",
            new() { ["query"] = "INSERT INTO auto_items (id, val) VALUES (3, 'c')" });

        var exchange = await ProcessAsync(endpoint);

        exchange.In.Body.Should().Be("untouched", "an INSERT leaves the body alone");
        exchange.In.Headers[SqlHeaders.UpdateCount].Should().Be(1, "the statement that ran is an INSERT, whatever the path says");
    }

    // ── Decided by the driver, not by the first word of the text (Apache Camel: execute() says whether there is a result set) ──

    [Theory]
    [InlineData("-- the newest first\nSELECT id, val FROM auto_items ORDER BY id")]
    [InlineData("/* hint */ SELECT id, val FROM auto_items ORDER BY id")]
    [InlineData("  \n\t SELECT id, val FROM auto_items ORDER BY id")]
    public async Task Auto_SelectBehindACommentOrWhitespace_ReturnsRows(string sql)
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), "items", new() { ["query"] = sql });

        var exchange = await ProcessAsync(endpoint);

        Values(exchange).Should().Equal(new object?[] { "a", "b" }, "what the statement returns decides, not how its text starts");
        exchange.In.Headers[SqlHeaders.OutputType].Should().Be("SelectList", "the header reports what Auto resolved to");
    }

    [Fact]
    public async Task Auto_CteThatWrites_ReportsUpdateCount()
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), "items",
            new() { ["query"] = "WITH x AS (SELECT 3 AS id) INSERT INTO auto_items (id, val) SELECT id, 'c' FROM x" });

        var exchange = await ProcessAsync(endpoint);

        exchange.In.Body.Should().Be("untouched", "a WITH that writes returns no rows");
        exchange.In.Headers[SqlHeaders.UpdateCount].Should().Be(1);
        exchange.In.Headers[SqlHeaders.OutputType].Should().Be("None");
        _db.ExecuteScalar("SELECT COUNT(*) FROM auto_items").Should().Be(3L);
    }

    [Fact]
    public async Task Auto_InsertReturning_ReturnsTheRowsItReturns()
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), "items",
            new() { ["query"] = "INSERT INTO auto_items (id, val) VALUES (4, 'd') RETURNING id, val" });

        var exchange = await ProcessAsync(endpoint);

        Values(exchange).Should().Equal(new object?[] { "d" }, "the statement returned a row, so Auto returns it, as Camel does");
        _db.ExecuteScalar("SELECT COUNT(*) FROM auto_items").Should().Be(3L, "the write happened and was committed");
    }

    private static async Task<Exchange> ProcessAsync(SqlEndpoint endpoint, Action<Exchange>? prepare = null)
    {
        var exchange = new Exchange(new Message("untouched"));
        prepare?.Invoke(exchange);
        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, CancellationToken.None));
        thrown.Should().BeNull(Outcome.Describe(thrown));
        return exchange;
    }

    private static List<object?> Values(Exchange exchange) =>
        exchange.In.Body.Should().BeAssignableTo<IList<Dictionary<string, object?>>>(
                $"outputType=Auto returns the rows of a SELECT (body: {exchange.In.Body ?? "null"}, " +
                $"updateCount: {(exchange.In.Headers.TryGetValue(SqlHeaders.UpdateCount, out var count) ? count : "absent")})")
            .Subject.Select(r => r["val"]).ToList();
}

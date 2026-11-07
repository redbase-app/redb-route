using System.Text.Json;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.Batch;

/// <summary>
/// Batch items in the shapes routes actually produce: CSV maps with string values, POCO lists, JSON arrays. Each
/// item's values must reach the statement's parameters.
/// </summary>
public sealed class BatchItemShapeTests : IDisposable
{
    private const string Json = """[{"id":1,"val":"a"},{"id":2,"val":"b"}]""";

    private readonly SqliteTestHelper _db = new();

    public BatchItemShapeTests() => _db.Execute("CREATE TABLE items (id INTEGER NOT NULL, val TEXT NOT NULL)");

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Batch_StringDictionaryItems_BindByKey()
    {
        var items = new List<Dictionary<string, string>>
        {
            new() { ["id"] = "1", ["val"] = "a" },
            new() { ["id"] = "2", ["val"] = "b" },
        };

        var exchange = await RunBatchAsync(items);

        AssertRows(exchange, "1|a", "2|b");
    }

    [Fact]
    public async Task Batch_PocoItems_BindByProperty()
    {
        var items = new List<ShapeItem>
        {
            new() { Id = 1, Val = "a" },
            new() { Id = 2, Val = "b" },
        };

        var exchange = await RunBatchAsync(items);

        AssertRows(exchange, "1|a", "2|b");
    }

    [Fact]
    public async Task Batch_JsonElementItems_BindScalars()
    {
        var items = JsonSerializer.Deserialize<List<JsonElement>>(Json)!;

        var exchange = await RunBatchAsync(items);

        AssertRows(exchange, "1|a", "2|b");
    }

    [Fact]
    public async Task Batch_JsonElementValuesInDictionaryItems_BindScalars()
    {
        // What a JSON unmarshal to object-valued maps produces: every value is a JsonElement.
        var items = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(Json)!;

        var exchange = await RunBatchAsync(items);

        AssertRows(exchange, "1|a", "2|b");
    }

    [Fact]
    public async Task Batch_UnknownItemShape_WithoutParams_FailsWithIndex()
    {
        // Scalar items carry no named values; Camel fails an unresolved named parameter instead of binding NULL.
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(),
            "INSERT INTO items (id, val) VALUES (:#id, :#val)", new()
            {
                ["outputType"] = "None",
                ["batchSize"] = "10",
                ["breakBatchOnError"] = "true",
            });
        var exchange = new Exchange(new Message(new List<string> { "a", "b" }));

        var act = () => endpoint.CreateProducer().Process(exchange, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*item 0*");
    }

    public sealed class ShapeItem
    {
        public int Id { get; set; }
        public string Val { get; set; } = "";
    }

    private async Task<Exchange> RunBatchAsync(object body)
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(),
            "INSERT INTO items (id, val) VALUES (:#id, :#val)", new()
            {
                ["outputType"] = "None",
                ["batchSize"] = "10",
                ["breakBatchOnError"] = "false",
            });
        var exchange = new Exchange(new Message(body));
        await endpoint.CreateProducer().Process(exchange, CancellationToken.None);
        return exchange;
    }

    private void AssertRows(Exchange exchange, params string[] expected)
    {
        var rows = _db.Query("SELECT id, val FROM items ORDER BY id")
            .Select(r => $"{r["id"]}|{r["val"]}")
            .ToList();
        var error = exchange.In.Headers.TryGetValue(SqlHeaders.Error, out var e) ? e : "none";

        rows.Should().Equal(expected, $"every item's values must be bound (error header: {error})");
    }
}

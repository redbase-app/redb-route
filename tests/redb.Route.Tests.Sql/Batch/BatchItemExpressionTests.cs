using redb.Route.Core;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.Batch;

/// <summary>Explicit <c>param.*</c> expressions evaluated per batch item, and the order of value sources.</summary>
public sealed class BatchItemExpressionTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    public BatchItemExpressionTests() =>
        _db.Execute("CREATE TABLE tenant_items (id INTEGER NOT NULL, val TEXT NOT NULL, tenant TEXT NULL)");

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Batch_ItemExpression_SeesExchangeProperties()
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(),
            "INSERT INTO tenant_items (id, val, tenant) VALUES (:#id, :#val, :#tenant)", new()
            {
                ["outputType"] = "None",
                ["batchSize"] = "10",
                ["param.tenant"] = "${property.tenant}",
            });
        var exchange = new Exchange(new Message(Items()));
        exchange.Properties["tenant"] = "t1";

        await endpoint.CreateProducer().Process(exchange, CancellationToken.None);

        _db.Query("SELECT tenant FROM tenant_items ORDER BY id").Select(r => r["tenant"])
            .Should().Equal(new object?[] { "t1", "t1" }, "an item's expression runs in the context of the exchange that carries the batch");
    }

    [Fact]
    public async Task Batch_ItemsDoNotInheritParentHeaderValues_WhenItemHasKey()
    {
        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(),
            "INSERT INTO tenant_items (id, val) VALUES (:#id, :#val)", new()
            {
                ["outputType"] = "None",
                ["batchSize"] = "10",
            });
        var exchange = new Exchange(new Message(Items()));
        exchange.In.Headers["val"] = "parent";

        await endpoint.CreateProducer().Process(exchange, CancellationToken.None);

        _db.Query("SELECT val FROM tenant_items ORDER BY id").Select(r => r["val"])
            .Should().Equal(new object?[] { "a", "b" }, "an item's own value wins over the carrying exchange's header");
    }

    private static List<Dictionary<string, object?>> Items() =>
    [
        new() { ["id"] = 1, ["val"] = "a" },
        new() { ["id"] = 2, ["val"] = "b" },
    ];
}

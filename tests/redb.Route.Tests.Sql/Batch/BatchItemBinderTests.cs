using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Nodes;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Tests.Sql.Batch.Fakes;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.Batch;

/// <summary>
/// Where a batch item's parameter values come from: the order of the sources, name matching for each item shape, JSON
/// values, items that are exchanges, and when an expression context is created.
/// </summary>
public sealed class BatchItemBinderTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    public BatchItemBinderTests() => _db.Execute(
        "CREATE TABLE bound (id INTEGER NOT NULL, val TEXT NULL, tenant TEXT NULL, i INTEGER NULL, d REAL NULL, b INTEGER NULL, g REAL NULL)");

    public void Dispose() => _db.Dispose();

    // ── Order of sources ────────────────────────────────────────────────────

    [Fact]
    public async Task Binder_ExplicitConstant_WinsOverItemValue()
    {
        var thrown = await RunAsync("INSERT INTO bound (id, val) VALUES (:#id, :#val)", Carrier(DictionaryItems()),
            new() { ["param.val"] = "constant" });

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Column("val").Should().Equal(new object?[] { "constant", "constant" }, "an explicit param.* is the first source");
    }

    [Fact]
    public async Task Binder_ParentHeader_UsedWhenItemHasNoKey()
    {
        var carrier = Carrier(new List<Dictionary<string, object?>> { new() { ["id"] = 1 }, new() { ["id"] = 2 } });
        carrier.In.Headers["val"] = "from-header";

        var thrown = await RunAsync("INSERT INTO bound (id, val) VALUES (:#id, :#val)", carrier);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Column("val").Should().Equal(new object?[] { "from-header", "from-header" },
            "the carrying exchange's header is the source after the item's own keys");
    }

    // ── Dictionary items ────────────────────────────────────────────────────

    [Fact]
    public async Task Binder_DictionaryKey_CaseInsensitiveFallback()
    {
        var items = new List<Dictionary<string, object?>> { new() { ["ID"] = 1, ["Val"] = "a" } };

        var thrown = await RunAsync("INSERT INTO bound (id, val) VALUES (:#id, :#val)", Carrier(items));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Rows("id", "val").Should().Equal("1|a");
    }

    [Fact]
    public async Task Binder_DictionaryKeysDifferingOnlyByCase_ExactKeyWins()
    {
        var items = new List<Dictionary<string, object?>> { new() { ["id"] = 1, ["val"] = "exact", ["VAL"] = "other" } };

        var thrown = await RunAsync("INSERT INTO bound (id, val) VALUES (:#id, :#val)", Carrier(items));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Column("val").Should().Equal(new object?[] { "exact" }, "a key that matches the placeholder exactly is taken as is");
    }

    [Fact]
    public async Task Binder_DictionaryKeysDifferingOnlyByCase_NoExactKey_IsNotGuessed()
    {
        var items = new List<Dictionary<string, object?>> { new() { ["id"] = 1, ["Val"] = "x", ["VAL"] = "y" } };

        var thrown = await RunAsync("INSERT INTO bound (id, val) VALUES (:#id, :#val)", Carrier(items));

        thrown.Should().BeOfType<InvalidOperationException>(Outcome.Describe(thrown))
            .Which.Message.Should().Contain("item 0").And.Contain(":#val");
        Column("val").Should().BeEmpty("two keys differing only by case do not name one value");
    }

    // ── POCO items ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Binder_Poco_ColumnAttributeAndSnakeCase()
    {
        var items = new List<PocoItem>
        {
            new() { Id = 1, Value = "a", TenantCode = "t1" },
            new() { Id = 2, Value = "b", TenantCode = null },
        };

        var thrown = await RunAsync("INSERT INTO bound (id, val, tenant) VALUES (:#id, :#item_value, :#tenant_code)", Carrier(items));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Rows("id", "val", "tenant").Should().Equal("1|a|t1", "2|b|");
    }

    // ── JSON items ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Binder_JsonElement_ScalarKinds()
    {
        const string json = """[{"id":1,"val":"text","i":42,"d":1.5,"b":true,"g":1e300,"tenant":null}]""";
        var items = JsonSerializer.Deserialize<List<JsonElement>>(json)!;

        var thrown = await RunAsync(
            "INSERT INTO bound (id, val, i, d, b, g, tenant) VALUES (:#id, :#val, :#i, :#d, :#b, :#g, :#tenant)", Carrier(items));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        var row = _db.Query("SELECT id, val, i, d, b, g, tenant FROM bound").Should().ContainSingle().Subject;
        row["id"].Should().Be(1L);
        row["val"].Should().Be("text");
        row["i"].Should().Be(42L);
        row["d"].Should().Be(1.5);
        row["b"].Should().Be(1L);
        row["g"].Should().Be(1e300);
        row["tenant"].Should().BeNull();
    }

    [Fact]
    public async Task Binder_JsonElement_EmptyString_IsDbNull()
    {
        var items = JsonSerializer.Deserialize<List<JsonElement>>("""[{"id":1,"val":""}]""")!;

        var thrown = await RunAsync("INSERT INTO bound (id, val) VALUES (:#id, :#val)", Carrier(items));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Column("val").Should().Equal(new object?[] { null }, "an empty JSON string is NULL, like an empty string anywhere else");
    }

    [Fact]
    public async Task Binder_JsonElement_ObjectAndArrayValues_AreRawText()
    {
        const string json = """[{"id":1,"val":{"a":1,"b":[true,null]},"tenant":[1,"x"]}]""";
        var items = JsonSerializer.Deserialize<List<JsonElement>>(json)!;

        var thrown = await RunAsync("INSERT INTO bound (id, val, tenant) VALUES (:#id, :#val, :#tenant)", Carrier(items));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Rows("val", "tenant").Should().Equal("""{"a":1,"b":[true,null]}|[1,"x"]""");
    }

    [Fact]
    public async Task Binder_JsonObjectItems_BindScalars()
    {
        var items = JsonNode.Parse("""[{"id":1,"val":"a","i":7},{"id":2,"val":null,"i":8}]""")!
            .AsArray().Select(node => node!.AsObject()).ToList();

        var thrown = await RunAsync("INSERT INTO bound (id, val, i) VALUES (:#id, :#val, :#i)", Carrier(items));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Rows("id", "val", "i").Should().Equal("1|a|7", "2||8");
    }

    // ── Exchange items ──────────────────────────────────────────────────────

    [Fact]
    public async Task Binder_ExchangeItem_UsesOwnHeadersNotParent()
    {
        var carrier = Carrier(new List<IExchange> { ItemExchange(1, "a"), ItemExchange(2, "b") });
        carrier.In.Headers["id"] = 99;
        carrier.In.Headers["val"] = "parent";

        var thrown = await RunAsync("INSERT INTO bound (id, val) VALUES (:#id, :#val)", carrier);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Rows("id", "val").Should().Equal("1|a", "2|b");
    }

    [Fact]
    public async Task Binder_ExchangeItem_ExpressionEvaluatedOnItem()
    {
        var carrier = Carrier(new List<IExchange> { ItemExchange(1, "a", label: "L1"), ItemExchange(2, "b", label: "L2") });
        carrier.In.Headers["label"] = "parent";

        var thrown = await RunAsync("INSERT INTO bound (id, val, tenant) VALUES (:#id, :#val, :#tenant)", carrier,
            new() { ["param.tenant"] = "${header.label}" });

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Column("tenant").Should().Equal(new object?[] { "L1", "L2" }, "an item exchange evaluates param.* on itself");
    }

    // ── Normalisation and expression context ───────────────────────────────

    [Fact]
    public async Task Binder_EmptyString_IsDbNull_SameAsSinglePath()
    {
        var items = new List<Dictionary<string, object?>> { new() { ["id"] = 1, ["val"] = "" } };

        var thrown = await RunAsync("INSERT INTO bound (id, val) VALUES (:#id, :#val)", Carrier(items));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Column("val").Should().Equal(new object?[] { null });
    }

    [Fact]
    public async Task Binder_NoTemplates_CreatesNoChildExchange()
    {
        var carrier = new CountingExchange(Carrier(DictionaryItems()));

        var thrown = await RunAsync("INSERT INTO bound (id, val) VALUES (:#id, :#val)", carrier);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        carrier.LinkedChildren.Should().Be(0, "without ${...} in param.* nothing needs an expression context");
        Column("val").Should().Equal(new object?[] { "a", "b" });
    }

    [Fact]
    public async Task Binder_Templates_EvaluatedInLinkedChildOfCarryingExchange()
    {
        var carrier = new CountingExchange(Carrier(DictionaryItems()));
        carrier.Properties["tenant"] = "t1";

        var thrown = await RunAsync("INSERT INTO bound (id, val, tenant) VALUES (:#id, :#val, :#tenant)", carrier,
            new() { ["param.tenant"] = "${property.tenant}" });

        thrown.Should().BeNull(Outcome.Describe(thrown));
        carrier.LinkedChildren.Should().Be(2, "each item is evaluated in a linked child of the carrying exchange");
        Column("tenant").Should().Equal(new object?[] { "t1", "t1" });
    }

    public sealed class PocoItem
    {
        public int Id { get; set; }

        [Column("item_value")]
        public string? Value { get; set; }

        public string? TenantCode { get; set; }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Exchange Carrier(object items) => new(new Message(items));

    private static List<Dictionary<string, object?>> DictionaryItems() =>
    [
        new() { ["id"] = 1, ["val"] = "a" },
        new() { ["id"] = 2, ["val"] = "b" },
    ];

    private static IExchange ItemExchange(int id, string val, string? label = null)
    {
        var exchange = new Exchange(new Message($"event-{id}"));
        exchange.In.Headers["id"] = id;
        exchange.In.Headers["val"] = val;
        if (label is not null)
            exchange.In.Headers["label"] = label;
        return exchange;
    }

    private async Task<Exception?> RunAsync(string sql, IExchange carrier, Dictionary<string, string>? options = null)
    {
        var parameters = new Dictionary<string, string> { ["outputType"] = "None", ["batchSize"] = "10" };
        if (options is not null)
            foreach (var (key, value) in options)
                parameters[key] = value;

        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), sql, parameters);
        return await Outcome.Of(() => endpoint.CreateProducer().Process(carrier, CancellationToken.None));
    }

    private List<object?> Column(string name) =>
        _db.Query($"SELECT {name} FROM bound ORDER BY id").Select(r => r[name]).ToList();

    private List<string> Rows(params string[] columns) =>
        _db.Query($"SELECT {string.Join(", ", columns)} FROM bound ORDER BY id")
            .Select(r => string.Join("|", columns.Select(c => r[c])))
            .ToList();
}

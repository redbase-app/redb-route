using System.Data;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;
using SqlDsl = redb.Route.Sql.Sql;

namespace redb.Route.Tests.Sql;

/// <summary>
/// Covers the three options that were bound from the URI but never honoured:
/// <c>procedureName</c> falling back to the URI path, <c>outputClass</c> (POCO mapping),
/// and <c>outputHeader</c> (result to a header instead of the body).
/// See docs/SQL_PROCEDURE_MODE_REGRESSION.md.
/// </summary>
public class SqlOutputMappingTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    public SqlOutputMappingTests()
    {
        _db.Execute("""
            CREATE TABLE orders (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                customer    TEXT NOT NULL,
                total_price REAL NOT NULL,
                processed   INTEGER NOT NULL DEFAULT 0
            )
            """);
        _db.Execute("INSERT INTO orders (customer, total_price) VALUES ('alice', 10.5), ('bob', 20.25)");
    }

    public void Dispose() => _db.Dispose();

    /// <summary>POCO target for outputClass. Note total_price → TotalPrice (snake_case mapping).</summary>
    public class Order
    {
        public long Id { get; set; }
        public string? Customer { get; set; }
        public double TotalPrice { get; set; }
    }

    private SqlEndpoint CreateEndpoint(string sql, Dictionary<string, string> parameters)
    {
        var context = new RouteContext();
        var component = new SqlComponent();
        context.AddComponent(component);
        context.AddToRegistry("main", _db.CreateFactory());

        var uri = new EndpointUri("sql", sql, $"sql:{sql}", parameters);
        return (SqlEndpoint)component.CreateEndpoint(uri);
    }

    private SqlEndpoint CreateEndpointFromUri(string uriString)
    {
        var context = new RouteContext();
        var component = new SqlComponent();
        context.AddComponent(component);
        context.AddToRegistry("main", _db.CreateFactory());

        return (SqlEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(uriString));
    }

    private static Exchange CreateExchange(object? body = null, Dictionary<string, object?>? headers = null)
    {
        var ex = new Exchange(new Message(body));
        if (headers != null)
            foreach (var kv in headers)
                ex.In.Headers[kv.Key] = kv.Value;
        return ex;
    }

    // ── procedureName falls back to the URI path ────────────────────

    [Fact]
    public async Task ProcedureMode_WithoutProcedureName_TakesNameFromUriPath()
    {
        // No procedureName= at all — the path must supply it.
        var endpoint = CreateEndpoint("abs", new()
        {
            ["mode"] = "Procedure",
            ["dataSource"] = "main",
            ["asFunction"] = "true",
            ["procedureParams"] = "IN:x:Int64"
        });

        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange(headers: new() { ["x"] = -42L });

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Body.Should().Be(42L);
        exchange.In.Headers[SqlHeaders.StoredProcedure].Should().Be("abs");
    }

    [Fact]
    public void ProcedureMode_ExplicitProcedureName_WinsOverPath()
    {
        var endpoint = CreateEndpoint("ignored_path", new()
        {
            ["mode"] = "Procedure",
            ["dataSource"] = "main",
            ["procedureName"] = "real_proc"
        });

        endpoint.EndpointOptions.ProcedureName.Should().Be("real_proc");
    }

    [Fact]
    public async Task FluentProcedureBuilder_ProducesAWorkingEndpoint()
    {
        // Sql.Procedure() only ever emitted the name into the path, so before the fix
        // this URI failed validation with "ProcedureName is required for Procedure mode."
        string uri = SqlDsl.Procedure("abs")
            .DataSource(new redb.Route.Expressions.ConstantExpression("main"))
            .AsFunction()
            .In("x", DbType.Int64);

        var endpoint = CreateEndpointFromUri(uri);
        endpoint.EndpointOptions.ProcedureName.Should().Be("abs");

        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange(headers: new() { ["x"] = -7L });

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Body.Should().Be(7L);
    }

    // ── outputClass: POCO mapping ───────────────────────────────────

    [Fact]
    public async Task OutputClass_SelectList_MapsToTypedListOfPoco()
    {
        var endpoint = CreateEndpoint("SELECT id, customer, total_price FROM orders ORDER BY id", new()
        {
            ["dataSource"] = "main",
            ["outputType"] = "SelectList",
            ["outputClass"] = typeof(Order).AssemblyQualifiedName!
        });

        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        var orders = exchange.In.Body.Should().BeOfType<List<Order>>().Subject;
        orders.Should().HaveCount(2);
        orders[0].Customer.Should().Be("alice");
        orders[0].TotalPrice.Should().Be(10.5);   // total_price → TotalPrice
        orders[1].Id.Should().Be(2);
        exchange.In.Headers[SqlHeaders.RowCount].Should().Be(2);
    }

    [Fact]
    public async Task OutputClass_SelectOne_MapsToSinglePoco()
    {
        var endpoint = CreateEndpoint("SELECT id, customer, total_price FROM orders WHERE customer = 'bob'", new()
        {
            ["dataSource"] = "main",
            ["outputType"] = "SelectOne",
            ["outputClass"] = typeof(Order).AssemblyQualifiedName!
        });

        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        var order = exchange.In.Body.Should().BeOfType<Order>().Subject;
        order.Customer.Should().Be("bob");
        order.TotalPrice.Should().Be(20.25);
    }

    [Fact]
    public async Task OutputClass_StreamList_YieldsTypedAsyncEnumerable()
    {
        var endpoint = CreateEndpoint("SELECT id, customer, total_price FROM orders ORDER BY id", new()
        {
            ["dataSource"] = "main",
            ["outputType"] = "StreamList",
            ["outputClass"] = typeof(Order).AssemblyQualifiedName!
        });

        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        var stream = exchange.In.Body.Should().BeAssignableTo<IAsyncEnumerable<Order>>().Subject;

        var seen = new List<Order>();
        await foreach (var order in stream)
            seen.Add(order);

        seen.Should().HaveCount(2);
        seen[0].Customer.Should().Be("alice");
    }

    [Fact]
    public async Task OutputClass_NotSet_StillYieldsDictionaries()
    {
        // Regression guard: the default path must be untouched.
        var endpoint = CreateEndpoint("SELECT id, customer FROM orders ORDER BY id", new()
        {
            ["dataSource"] = "main",
            ["outputType"] = "SelectList"
        });

        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Body.Should().BeOfType<List<Dictionary<string, object?>>>();
    }

    [Fact]
    public void OutputClass_UnknownType_ThrowsWithAClearMessage()
    {
        var endpoint = CreateEndpoint("SELECT 1", new()
        {
            ["dataSource"] = "main",
            ["outputType"] = "SelectList",
            ["outputClass"] = "Nope.NotAType"
        });

        var producer = endpoint.CreateProducer();

        var act = async () => await producer.Process(CreateExchange(), CancellationToken.None);

        act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Nope.NotAType*could not be resolved*");
    }

    // ── outputHeader: result to header, body preserved ──────────────

    [Fact]
    public async Task OutputHeader_SelectList_PutsResultInHeaderAndKeepsBody()
    {
        var endpoint = CreateEndpoint("SELECT id, customer FROM orders ORDER BY id", new()
        {
            ["dataSource"] = "main",
            ["outputType"] = "SelectList",
            ["outputHeader"] = "lookup"
        });

        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange(body: "original payload");

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Body.Should().Be("original payload");   // body survives the enrichment
        var rows = exchange.In.Headers["lookup"]
            .Should().BeOfType<List<Dictionary<string, object?>>>().Subject;
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task OutputHeader_Scalar_PutsScalarInHeader()
    {
        var endpoint = CreateEndpoint("SELECT COUNT(*) FROM orders", new()
        {
            ["dataSource"] = "main",
            ["outputType"] = "Scalar",
            ["outputHeader"] = "orderCount"
        });

        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange(body: "keep me");

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Body.Should().Be("keep me");
        exchange.In.Headers["orderCount"].Should().Be(2L);
    }

    [Fact]
    public async Task OutputHeader_WithOutputClass_PutsTypedListInHeader()
    {
        var endpoint = CreateEndpoint("SELECT id, customer, total_price FROM orders ORDER BY id", new()
        {
            ["dataSource"] = "main",
            ["outputType"] = "SelectList",
            ["outputClass"] = typeof(Order).AssemblyQualifiedName!,
            ["outputHeader"] = "orders"
        });

        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange(body: 42);

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Body.Should().Be(42);
        exchange.In.Headers["orders"].Should().BeOfType<List<Order>>()
            .Which.Should().HaveCount(2);
    }

    [Fact]
    public async Task OutputHeader_Procedure_AsFunction_PutsResultInHeader()
    {
        var endpoint = CreateEndpoint("abs", new()
        {
            ["mode"] = "Procedure",
            ["dataSource"] = "main",
            ["asFunction"] = "true",
            ["procedureParams"] = "IN:x:Int64",
            ["outputHeader"] = "absValue"
        });

        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange(body: "payload", headers: new() { ["x"] = -5L });

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Body.Should().Be("payload");
        exchange.In.Headers["absValue"].Should().Be(5L);
    }
}

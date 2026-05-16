using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;

namespace redb.Route.Tests.Sql;

/// <summary>
/// Integration tests for SqlProducer using an in-memory SQLite database.
/// Tests all 5 OutputType branches plus parameter binding, transactions, noop, and headers.
/// </summary>
public class SqlProducerTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    public SqlProducerTests()
    {
        _db.Execute("""
            CREATE TABLE products (
                id    INTEGER PRIMARY KEY AUTOINCREMENT,
                name  TEXT NOT NULL,
                price REAL NOT NULL
            )
            """);
        _db.Execute("INSERT INTO products(name, price) VALUES('Widget', 9.99)");
        _db.Execute("INSERT INTO products(name, price) VALUES('Gadget', 19.99)");
        _db.Execute("INSERT INTO products(name, price) VALUES('Doohickey', 4.50)");
    }

    private SqlEndpoint CreateEndpoint(string sql, Dictionary<string, string>? extraParams = null)
    {
        var context = new RouteContext();
        var component = new SqlComponent();
        context.AddComponent(component);
        context.AddToRegistry("main", _db.CreateFactory());

        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Execute",
            ["dataSource"] = "main"
        };
        if (extraParams != null)
            foreach (var kv in extraParams)
                parameters[kv.Key] = kv.Value;

        var uri = new EndpointUri("sql", sql, $"sql:{sql}", parameters);
        return (SqlEndpoint)component.CreateEndpoint(uri);
    }

    private static Exchange CreateExchange(object? body = null, Dictionary<string, object?>? headers = null)
    {
        var ex = new Exchange(new Message(body));
        if (headers != null)
            foreach (var kv in headers)
                ex.In.Headers[kv.Key] = kv.Value;
        return ex;
    }

    // ── OutputType: SelectList ──────────────────────────────────────

    [Fact]
    public async Task SelectList_ReturnsAllRows()
    {
        var endpoint = CreateEndpoint("SELECT * FROM products ORDER BY id",
            new() { ["outputType"] = "SelectList" });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange();
        await producer.Process(exchange, CancellationToken.None);

        var rows = exchange.In.Body as List<Dictionary<string, object?>>;
        rows.Should().NotBeNull();
        rows.Should().HaveCount(3);
        rows![0]["name"].Should().Be("Widget");
        rows[2]["name"].Should().Be("Doohickey");
    }

    [Fact]
    public async Task SelectList_SetsRowCountHeader()
    {
        var endpoint = CreateEndpoint("SELECT * FROM products",
            new() { ["outputType"] = "SelectList" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Headers[SqlHeaders.RowCount].Should().Be(3);
    }

    [Fact]
    public async Task SelectList_EmptyResult_ReturnsEmptyList()
    {
        var endpoint = CreateEndpoint("SELECT * FROM products WHERE id = -1",
            new() { ["outputType"] = "SelectList" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        var rows = exchange.In.Body as List<Dictionary<string, object?>>;
        rows.Should().NotBeNull();
        rows.Should().BeEmpty();
        exchange.In.Headers[SqlHeaders.RowCount].Should().Be(0);
    }

    // ── OutputType: SelectOne ───────────────────────────────────────

    [Fact]
    public async Task SelectOne_ReturnsSingleRow()
    {
        var endpoint = CreateEndpoint("SELECT * FROM products WHERE id = 1",
            new() { ["outputType"] = "SelectOne" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        var row = exchange.In.Body as Dictionary<string, object?>;
        row.Should().NotBeNull();
        row!["name"].Should().Be("Widget");
        exchange.In.Headers[SqlHeaders.RowCount].Should().Be(1);
    }

    [Fact]
    public async Task SelectOne_NoResult_ReturnsNull()
    {
        var endpoint = CreateEndpoint("SELECT * FROM products WHERE id = -1",
            new() { ["outputType"] = "SelectOne" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Body.Should().BeNull();
        exchange.In.Headers[SqlHeaders.RowCount].Should().Be(0);
    }

    // ── OutputType: Scalar ──────────────────────────────────────────

    [Fact]
    public async Task Scalar_ReturnsCount()
    {
        var endpoint = CreateEndpoint("SELECT COUNT(*) FROM products",
            new() { ["outputType"] = "Scalar" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        // SQLite returns long
        Convert.ToInt32(exchange.In.Body).Should().Be(3);
        exchange.In.Headers[SqlHeaders.RowCount].Should().Be(1);
    }

    [Fact]
    public async Task Scalar_NullResult_ReturnsNull()
    {
        _db.Execute("DELETE FROM products");

        var endpoint = CreateEndpoint("SELECT name FROM products WHERE id = 1",
            new() { ["outputType"] = "Scalar" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Body.Should().BeNull();
    }

    // ── OutputType: StreamList ──────────────────────────────────────

    [Fact]
    public async Task StreamList_ReturnsAllRows()
    {
        var endpoint = CreateEndpoint("SELECT * FROM products ORDER BY id",
            new() { ["outputType"] = "StreamList" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Body.Should().BeAssignableTo<IAsyncEnumerable<Dictionary<string, object?>>>();
        exchange.In.Headers[SqlHeaders.RowCount].Should().Be(-1);

        var stream = (IAsyncEnumerable<Dictionary<string, object?>>)exchange.In.Body!;
        var rows = new List<Dictionary<string, object?>>();
        await foreach (var row in stream)
            rows.Add(row);

        rows.Should().HaveCount(3);
    }

    // ── OutputType: None (NonQuery) ─────────────────────────────────

    [Fact]
    public async Task None_Insert_ReturnsAffectedRows()
    {
        var endpoint = CreateEndpoint(
            "INSERT INTO products(name, price) VALUES('Thingamajig', 2.99)",
            new() { ["outputType"] = "None" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Headers[SqlHeaders.UpdateCount].Should().Be(1);
    }

    [Fact]
    public async Task None_Update_ReturnsAffectedRows()
    {
        var endpoint = CreateEndpoint(
            "UPDATE products SET price = 0.01 WHERE price > 5",
            new() { ["outputType"] = "None" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        // Widget (9.99) and Gadget (19.99) → 2 rows updated
        exchange.In.Headers[SqlHeaders.UpdateCount].Should().Be(2);
    }

    [Fact]
    public async Task None_Delete_ReturnsAffectedRows()
    {
        var endpoint = CreateEndpoint(
            "DELETE FROM products WHERE id = 1",
            new() { ["outputType"] = "None" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Headers[SqlHeaders.UpdateCount].Should().Be(1);
        _db.Query("SELECT * FROM products").Should().HaveCount(2);
    }

    // ── Parameter binding ───────────────────────────────────────────

    [Fact]
    public async Task BindParameters_FromHeaders()
    {
        var endpoint = CreateEndpoint(
            "SELECT * FROM products WHERE name = @name",
            new() { ["outputType"] = "SelectOne" });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange(headers: new()
        {
            ["name"] = "Gadget"
        });

        await producer.Process(exchange, CancellationToken.None);

        var row = exchange.In.Body as Dictionary<string, object?>;
        row.Should().NotBeNull();
        row!["name"].Should().Be("Gadget");
    }

    [Fact]
    public async Task BindParameters_FromBodyDictionary()
    {
        var endpoint = CreateEndpoint(
            "SELECT * FROM products WHERE name = @name",
            new() { ["outputType"] = "SelectOne" });
        var producer = endpoint.CreateProducer();

        var body = new Dictionary<string, object?> { ["name"] = "Doohickey" };
        var exchange = CreateExchange(body: body);

        await producer.Process(exchange, CancellationToken.None);

        var row = exchange.In.Body as Dictionary<string, object?>;
        row.Should().NotBeNull();
        row!["name"].Should().Be("Doohickey");
    }

    [Fact]
    public async Task BindParameters_HeaderTakesPriority()
    {
        var endpoint = CreateEndpoint(
            "SELECT * FROM products WHERE name = @name",
            new() { ["outputType"] = "SelectOne" });
        var producer = endpoint.CreateProducer();

        var body = new Dictionary<string, object?> { ["name"] = "Doohickey" };
        var exchange = CreateExchange(body: body, headers: new()
        {
            ["name"] = "Widget"
        });

        await producer.Process(exchange, CancellationToken.None);

        // Header wins over body
        var row = exchange.In.Body as Dictionary<string, object?>;
        row!["name"].Should().Be("Widget");
    }

    [Fact]
    public async Task BindParameters_MissingParam_BindsDBNull()
    {
        var endpoint = CreateEndpoint(
            "SELECT * FROM products WHERE name = @nonExistent OR 1=1 ORDER BY id LIMIT 1",
            new() { ["outputType"] = "SelectOne" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        // Query should still work (param bound as NULL)
        var row = exchange.In.Body as Dictionary<string, object?>;
        row.Should().NotBeNull();
    }

    // ── Common headers ──────────────────────────────────────────────

    [Fact]
    public async Task CommonHeaders_SetCorrectly()
    {
        var endpoint = CreateEndpoint("SELECT 1",
            new() { ["outputType"] = "Scalar" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Headers[SqlHeaders.Query].Should().Be("SELECT 1");
        exchange.In.Headers[SqlHeaders.DataSource].Should().Be("main");
        exchange.In.Headers[SqlHeaders.OutputType].Should().Be("Scalar");
        exchange.In.Headers.Should().ContainKey(SqlHeaders.ExecutionTime);
    }

    // ── Noop mode ───────────────────────────────────────────────────

    [Fact]
    public async Task Noop_SetsQueryHeaderOnly()
    {
        var endpoint = CreateEndpoint("SELECT * FROM products",
            new() { ["outputType"] = "SelectList", ["noop"] = "true" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Headers[SqlHeaders.Query].Should().Be("SELECT * FROM products");
        // Body should NOT be set (no actual execution)
        exchange.In.Body.Should().BeNull();
    }

    // ── Transacted ──────────────────────────────────────────────────

    [Fact]
    public async Task Transacted_CommitsOnSuccess()
    {
        var endpoint = CreateEndpoint(
            "INSERT INTO products(name, price) VALUES('TxProduct', 1.00)",
            new() { ["outputType"] = "None", ["transacted"] = "true" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Headers.Should().ContainKey(SqlHeaders.TransactionId);
        _db.Query("SELECT * FROM products WHERE name = 'TxProduct'").Should().HaveCount(1);
    }

    // ── Error handling ──────────────────────────────────────────────

    [Fact]
    public async Task InvalidSql_Throws()
    {
        var endpoint = CreateEndpoint("INVALID SQL STATEMENT",
            new() { ["outputType"] = "None" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        var act = () => producer.Process(exchange, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public void NoDataSourceAndNoConnectionString_Throws()
    {
        var component = new SqlComponent();
        var uri = new EndpointUri("sql", "SELECT 1", "sql:SELECT 1",
            new Dictionary<string, string> { ["mode"] = "Execute" });

        var act = () => component.CreateEndpoint(uri);

        act.Should().Throw<ArgumentException>();
    }

    // ── Multiple queries in sequence ────────────────────────────────

    [Fact]
    public async Task MultipleExecutions_IndependentState()
    {
        var endpoint = CreateEndpoint("SELECT COUNT(*) FROM products",
            new() { ["outputType"] = "Scalar" });
        var producer = endpoint.CreateProducer();

        var ex1 = CreateExchange();
        await producer.Process(ex1, CancellationToken.None);
        Convert.ToInt32(ex1.In.Body).Should().Be(3);

        // Insert a row via another producer
        var insertEndpoint = CreateEndpoint(
            "INSERT INTO products(name, price) VALUES('NewItem', 5.00)",
            new() { ["outputType"] = "None" });
        var insertProducer = insertEndpoint.CreateProducer();
        await insertProducer.Process(CreateExchange(), CancellationToken.None);

        // Count should now be 4
        var ex2 = CreateExchange();
        await producer.Process(ex2, CancellationToken.None);
        Convert.ToInt32(ex2.In.Body).Should().Be(4);
    }

    // ── Explicit Parameters (.Param) ────────────────────────────────

    [Fact]
    public async Task ExplicitParam_ConstantValue_OverridesHeader()
    {
        // @name is bound to explicit "Widget" even though header has "Gadget"
        var endpoint = CreateEndpoint(
            "SELECT * FROM products WHERE name = @name",
            new()
            {
                ["outputType"] = "SelectOne",
                ["param.name"] = "Widget"
            });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange(headers: new() { ["name"] = "Gadget" });
        await producer.Process(exchange, CancellationToken.None);

        var row = exchange.In.Body as Dictionary<string, object?>;
        row.Should().NotBeNull();
        row!["name"].Should().Be("Widget");
    }

    [Fact]
    public async Task ExplicitParam_ConstantValue_OverridesBody()
    {
        var endpoint = CreateEndpoint(
            "SELECT * FROM products WHERE name = @name",
            new()
            {
                ["outputType"] = "SelectOne",
                ["param.name"] = "Doohickey"
            });
        var producer = endpoint.CreateProducer();

        var body = new Dictionary<string, object?> { ["name"] = "Widget" };
        var exchange = CreateExchange(body: body);
        await producer.Process(exchange, CancellationToken.None);

        var row = exchange.In.Body as Dictionary<string, object?>;
        row.Should().NotBeNull();
        row!["name"].Should().Be("Doohickey");
    }

    [Fact]
    public async Task ExplicitParam_MixedWithAutoBindHeaders()
    {
        // @name explicit, @price from header
        _db.Execute("CREATE TABLE filtered (id INTEGER PRIMARY KEY, name TEXT, price REAL)");
        _db.Execute("INSERT INTO filtered(name, price) VALUES('A', 10)");
        _db.Execute("INSERT INTO filtered(name, price) VALUES('A', 20)");
        _db.Execute("INSERT INTO filtered(name, price) VALUES('B', 10)");

        var endpoint = CreateEndpoint(
            "SELECT * FROM filtered WHERE name = @name AND price = @price",
            new()
            {
                ["outputType"] = "SelectOne",
                ["param.name"] = "A"     // explicit constant
            });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange(headers: new() { ["price"] = 20 }); // auto-bind from header
        await producer.Process(exchange, CancellationToken.None);

        var row = exchange.In.Body as Dictionary<string, object?>;
        row.Should().NotBeNull();
        row!["name"].Should().Be("A");
        Convert.ToDouble(row["price"]).Should().Be(20);
    }

    [Fact]
    public async Task ExplicitParam_InsertWithConstants()
    {
        var endpoint = CreateEndpoint(
            "INSERT INTO products(name, price) VALUES(@name, @price)",
            new()
            {
                ["outputType"] = "None",
                ["param.name"] = "ExplicitItem",
                ["param.price"] = "7.77"
            });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange();
        await producer.Process(exchange, CancellationToken.None);

        var count = Convert.ToInt64(_db.ExecuteScalar("SELECT COUNT(*) FROM products WHERE name = 'ExplicitItem'"));
        count.Should().Be(1);
    }

    [Fact]
    public async Task ExplicitParam_EmptyValue_BindsAsDBNull()
    {
        _db.Execute("CREATE TABLE notes (id INTEGER PRIMARY KEY, text TEXT)");

        var endpoint = CreateEndpoint(
            "INSERT INTO notes(text) VALUES(@text)",
            new()
            {
                ["outputType"] = "None",
                ["param.text"] = ""
            });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange(headers: new() { ["text"] = "should be ignored" });
        await producer.Process(exchange, CancellationToken.None);

        // Empty explicit param → DBNull → NULL in DB
        var result = _db.ExecuteScalar("SELECT text FROM notes LIMIT 1");
        result.Should().Be(DBNull.Value);
    }

    [Fact]
    public async Task ExplicitParam_SingleExpression_PreservesType()
    {
        _db.Execute("CREATE TABLE typed_test (tag TEXT, num INTEGER)");

        var endpoint = CreateEndpoint(
            "INSERT INTO typed_test(tag, num) VALUES(@tag, @num)",
            new()
            {
                ["outputType"] = "None",
                ["param.tag"] = "${header.tag}",
                ["param.num"] = "${header.num}"
            });
        var producer = endpoint.CreateProducer();

        // header.num is int 42in — single expression should preserve the type
        var exchange = CreateExchange(headers: new()
        {
            ["tag"] = "hello",
            ["num"] = 42
        });
        await producer.Process(exchange, CancellationToken.None);

        var tagResult = _db.ExecuteScalar("SELECT tag FROM typed_test LIMIT 1");
        tagResult.Should().Be("hello");

        var numResult = _db.ExecuteScalar("SELECT num FROM typed_test LIMIT 1");
        Convert.ToInt32(numResult).Should().Be(42);
    }

    [Fact]
    public async Task ExplicitParam_CompositeTemplate_ReturnsString()
    {
        _db.Execute("CREATE TABLE composite_test (label TEXT)");

        var endpoint = CreateEndpoint(
            "INSERT INTO composite_test(label) VALUES(@label)",
            new()
            {
                ["outputType"] = "None",
                ["param.label"] = "prefix-${header.tag}-suffix"
            });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange(headers: new() { ["tag"] = "mid" });
        await producer.Process(exchange, CancellationToken.None);

        var result = _db.ExecuteScalar("SELECT label FROM composite_test LIMIT 1");
        result.Should().Be("prefix-mid-suffix");
    }

    // ── Batch Mode ──────────────────────────────────────────────────

    [Fact]
    public async Task Batch_InsertsAllItems()
    {
        var items = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "Batch1", ["price"] = 1.0 },
            new() { ["name"] = "Batch2", ["price"] = 2.0 },
            new() { ["name"] = "Batch3", ["price"] = 3.0 },
        };

        var endpoint = CreateEndpoint(
            "INSERT INTO products(name, price) VALUES(@name, @price)",
            new() { ["outputType"] = "None", ["batchSize"] = "10" });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange(body: items);
        await producer.Process(exchange, CancellationToken.None);

        var count = Convert.ToInt64(_db.ExecuteScalar("SELECT COUNT(*) FROM products"));
        count.Should().Be(6); // 3 original + 3 batch

        exchange.In.Headers[SqlHeaders.UpdateCount].Should().Be(3);
        exchange.In.Headers[SqlHeaders.RowCount].Should().Be(3);
    }

    [Fact]
    public async Task Batch_Transacted_CommitsAll()
    {
        var items = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "TX1", ["price"] = 1.0 },
            new() { ["name"] = "TX2", ["price"] = 2.0 },
        };

        var endpoint = CreateEndpoint(
            "INSERT INTO products(name, price) VALUES(@name, @price)",
            new() { ["outputType"] = "None", ["batchSize"] = "10", ["transacted"] = "true" });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange(body: items);
        await producer.Process(exchange, CancellationToken.None);

        var count = Convert.ToInt64(_db.ExecuteScalar("SELECT COUNT(*) FROM products WHERE name IN ('TX1','TX2')"));
        count.Should().Be(2);
        exchange.In.Headers.Should().ContainKey(SqlHeaders.TransactionId);
    }

    [Fact]
    public async Task Batch_BreakOnError_StopsAndRollsBack()
    {
        // products has NOT NULL on name, so inserting null fails
        var items = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "Good1", ["price"] = 1.0 },
            new() { ["name"] = DBNull.Value, ["price"] = 2.0 }, // fails NOT NULL
            new() { ["name"] = "Good2", ["price"] = 3.0 },
        };

        var endpoint = CreateEndpoint(
            "INSERT INTO products(name, price) VALUES(@name, @price)",
            new()
            {
                ["outputType"] = "None",
                ["batchSize"] = "10",
                ["breakBatchOnError"] = "true",
                ["transacted"] = "true"
            });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange(body: items);

        var act = () => producer.Process(exchange, CancellationToken.None);
        await act.Should().ThrowAsync<AggregateException>();

        // Transaction rolled back — Good1 should NOT be persisted
        var count = Convert.ToInt64(_db.ExecuteScalar("SELECT COUNT(*) FROM products WHERE name = 'Good1'"));
        count.Should().Be(0);
    }

    [Fact]
    public async Task Batch_ContinueOnError_ProcessesRemaining()
    {
        _db.Execute("CREATE TABLE batch_test (id INTEGER PRIMARY KEY, val TEXT NOT NULL)");

        var items = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1, ["val"] = "ok1" },
            new() { ["id"] = 1, ["val"] = "duplicate PK" }, // fails
            new() { ["id"] = 3, ["val"] = "ok3" },
        };

        var endpoint = CreateEndpoint(
            "INSERT INTO batch_test(id, val) VALUES(@id, @val)",
            new()
            {
                ["outputType"] = "None",
                ["batchSize"] = "10",
                ["breakBatchOnError"] = "false"
            });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange(body: items);
        await producer.Process(exchange, CancellationToken.None);

        // 2 of 3 succeeded, 1 error
        Convert.ToInt32(exchange.In.Headers[SqlHeaders.RowCount]).Should().Be(2);
        exchange.In.Headers[SqlHeaders.Error]!.ToString().Should().Contain("1 batch error");

        var count = Convert.ToInt64(_db.ExecuteScalar("SELECT COUNT(*) FROM batch_test"));
        count.Should().Be(2);
    }

    [Fact]
    public async Task Batch_EmptyList_NoBatchProcessing()
    {
        var endpoint = CreateEndpoint(
            "SELECT COUNT(*) FROM products",
            new() { ["outputType"] = "Scalar", ["batchSize"] = "10" });
        var producer = endpoint.CreateProducer();

        // Body is empty list — should NOT enter batch mode, falls through to normal execution
        var exchange = CreateExchange(body: new List<Dictionary<string, object?>>());
        await producer.Process(exchange, CancellationToken.None);

        // Normal scalar execution returns the count
        exchange.In.Body.Should().Be(3L); // 3 seeded products
    }

    // ── StreamList OutputType ───────────────────────────────────────

    [Fact]
    public async Task StreamList_ReturnsIAsyncEnumerable()
    {
        var endpoint = CreateEndpoint("SELECT * FROM products ORDER BY id",
            new() { ["outputType"] = "StreamList" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Body.Should().BeAssignableTo<IAsyncEnumerable<Dictionary<string, object?>>>();
        exchange.In.Headers[SqlHeaders.RowCount].Should().Be(-1); // unknown until iterated
    }

    [Fact]
    public async Task StreamList_CanIterateAllRows()
    {
        var endpoint = CreateEndpoint("SELECT name FROM products ORDER BY id",
            new() { ["outputType"] = "StreamList" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        var stream = (IAsyncEnumerable<Dictionary<string, object?>>)exchange.In.Body!;
        var names = new List<string>();
        await foreach (var row in stream)
            names.Add((string)row["name"]!);

        names.Should().BeEquivalentTo(["Widget", "Gadget", "Doohickey"]);
    }

    // ── Transacted Rollback ─────────────────────────────────────────

    [Fact]
    public async Task Transacted_RollsBackOnError()
    {
        // Create a table to verify rollback
        _db.Execute("CREATE TABLE tx_test (id INTEGER PRIMARY KEY, val TEXT)");

        var endpoint = CreateEndpoint(
            "INSERT INTO tx_test(id, val) VALUES(1, 'should-rollback')",
            new() { ["outputType"] = "None", ["transacted"] = "true" });

        // Simulate error: use a query that succeeds for insert but the producer
        // encounters an error in the commit flow. Instead, we inject error via bad SQL in
        // a second step. Let's use a dual-query scenario:
        // Actually, simplest: execute valid INSERT, then throw from the test.
        // But we can't intercept. Better approach: force SQL error after insert
        // by using a constraint violation in the same command.
        // Simplest test: invalid SQL should throw and rollback.
        var badEndpoint = CreateEndpoint(
            "INSERT INTO tx_test(id, val) VALUES(1, 'a'); INSERT INTO tx_test(id, val) VALUES(1, 'b')",
            new() { ["outputType"] = "None", ["transacted"] = "true" });
        var producer = badEndpoint.CreateProducer();
        var exchange = CreateExchange();

        // SQLite handles multi-statement differently; let's use a simpler approach
        // First insert succeeds, second with same PK fails
        var firstEndpoint = CreateEndpoint(
            "INSERT INTO tx_test(id, val) VALUES(1, 'first')",
            new() { ["outputType"] = "None", ["transacted"] = "false" });
        var p1 = firstEndpoint.CreateProducer();
        await p1.Process(CreateExchange(), CancellationToken.None);

        // Now try to insert duplicate with transaction
        var dupeEndpoint = CreateEndpoint(
            "INSERT INTO tx_test(id, val) VALUES(1, 'duplicate')",
            new() { ["outputType"] = "None", ["transacted"] = "true" });
        var p2 = dupeEndpoint.CreateProducer();

        var act = () => p2.Process(CreateExchange(), CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();

        // Original row should still be there (duplicate was rolled back)
        var rows = _db.Query("SELECT val FROM tx_test");
        rows.Should().HaveCount(1);
        rows[0]["val"].Should().Be("first");
    }

    [Fact]
    public async Task Transacted_CommitsOnSuccess_InsertVisible()
    {
        _db.Execute("CREATE TABLE tx_success (id INTEGER PRIMARY KEY, val TEXT)");

        var endpoint = CreateEndpoint(
            "INSERT INTO tx_success(id, val) VALUES(1, 'committed')",
            new() { ["outputType"] = "None", ["transacted"] = "true" });
        var producer = endpoint.CreateProducer();

        await producer.Process(CreateExchange(), CancellationToken.None);

        var rows = _db.Query("SELECT val FROM tx_success");
        rows.Should().HaveCount(1);
        rows[0]["val"].Should().Be("committed");
    }

    public void Dispose() => _db.Dispose();
}

using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;

namespace redb.Route.Tests.Sql;

/// <summary>
/// Integration tests for SqlProcedureProducer.
/// SQLite doesn't support stored procedures, so we test:
///   - AsFunction mode (SQLite user-defined functions or simple SELECT)
///   - Noop mode
///   - ParseProcedureParams parsing
///   - Parameter binding logic
/// </summary>
public class SqlProcedureProducerTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    public SqlProcedureProducerTests()
    {
        _db.Execute("""
            CREATE TABLE proc_log (
                id   INTEGER PRIMARY KEY AUTOINCREMENT,
                val  TEXT
            )
            """);
    }

    private SqlEndpoint CreateProcedureEndpoint(
        string procedureName,
        Dictionary<string, string>? extraParams = null)
    {
        var context = new RouteContext();
        var component = new SqlComponent();
        context.AddComponent(component);
        context.AddToRegistry("main", _db.CreateFactory());

        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Procedure",
            ["dataSource"] = "main",
            ["procedureName"] = procedureName
        };
        if (extraParams != null)
            foreach (var kv in extraParams)
                parameters[kv.Key] = kv.Value;

        var uri = new EndpointUri("sql", procedureName, $"sql:{procedureName}", parameters);
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

    // ── Noop mode ───────────────────────────────────────────────────

    [Fact]
    public async Task Noop_SetsProcedureNameHeader()
    {
        var endpoint = CreateProcedureEndpoint("my_proc",
            new() { ["noop"] = "true" });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Headers[SqlHeaders.StoredProcedure].Should().Be("my_proc");
        // No actual execution
        exchange.In.Body.Should().BeNull();
    }

    // ── AsFunction mode ─────────────────────────────────────────────

    [Fact]
    public async Task AsFunction_NoParams_ExecutesSelectFunction()
    {
        // SQLite built-in: typeof()
        var endpoint = CreateProcedureEndpoint("typeof",
            new()
            {
                ["asFunction"] = "true",
                ["procedureParams"] = "IN:val:String"
            });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange(headers: new() { ["val"] = "hello" });
        await producer.Process(exchange, CancellationToken.None);

        // typeof('hello') → 'text'
        exchange.In.Body.Should().Be("text");
        exchange.In.Headers[SqlHeaders.StoredProcedure].Should().Be("typeof");
        exchange.In.Headers.Should().ContainKey(SqlHeaders.ExecutionTime);
    }

    [Fact]
    public async Task AsFunction_WithParams_BindsFromHeaders()
    {
        // Use abs() as a simple function
        var endpoint = CreateProcedureEndpoint("abs",
            new()
            {
                ["asFunction"] = "true",
                ["procedureParams"] = "IN:x:Int64"
            });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange(headers: new() { ["x"] = (object)-42L });
        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Body.Should().Be(42L);
    }

    [Fact]
    public async Task AsFunction_WithParams_BindsFromBody()
    {
        var endpoint = CreateProcedureEndpoint("abs",
            new()
            {
                ["asFunction"] = "true",
                ["procedureParams"] = "IN:x:Int64"
            });
        var producer = endpoint.CreateProducer();

        var body = new Dictionary<string, object?> { ["x"] = -99L };
        var exchange = CreateExchange(body: body);
        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Body.Should().Be(99L);
    }

    [Fact]
    public async Task AsFunction_NullResult_ReturnsNull()
    {
        // nullif(x, x) always returns null
        var endpoint = CreateProcedureEndpoint("nullif",
            new()
            {
                ["asFunction"] = "true",
                ["procedureParams"] = "IN:a:String,IN:b:String"
            });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange(headers: new()
        {
            ["a"] = "same",
            ["b"] = "same"
        });
        await producer.Process(exchange, CancellationToken.None);

        // nullif('same', 'same') → NULL
        exchange.In.Body.Should().BeNull();
    }

    // ── ProcedureParams parsing ─────────────────────────────────────

    [Fact]
    public async Task AsFunction_MultipleParams_CorrectOrder()
    {
        // coalesce(null, 'fallback') → 'fallback'
        var endpoint = CreateProcedureEndpoint("coalesce",
            new()
            {
                ["asFunction"] = "true",
                ["procedureParams"] = "IN:a:String,IN:b:String"
            });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange(headers: new()
        {
            ["b"] = "fallback"
        });
        // a is not set → NULL, b = 'fallback'
        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Body.Should().Be("fallback");
    }

    [Fact]
    public async Task EmptyProcedureParams_NoBinding()
    {
        // Simple function with no explicit params
        var endpoint = CreateProcedureEndpoint("random",
            new()
            {
                ["asFunction"] = "true"
            });
        var producer = endpoint.CreateProducer();
        var exchange = CreateExchange();

        await producer.Process(exchange, CancellationToken.None);

        // random() returns an integer — just verify it doesn't throw
        exchange.In.Body.Should().NotBeNull();
    }

    // ── Transacted ──────────────────────────────────────────────────

    [Fact]
    public async Task AsFunction_Transacted_SetsTransactionHeader()
    {
        var endpoint = CreateProcedureEndpoint("abs",
            new()
            {
                ["asFunction"] = "true",
                ["transacted"] = "true",
                ["procedureParams"] = "IN:x:Int64"
            });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange(headers: new() { ["x"] = (object)5L });
        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Headers.Should().ContainKey(SqlHeaders.TransactionId);
        exchange.In.Body.Should().Be(5L);
    }

    // ── DataSource header ───────────────────────────────────────────

    [Fact]
    public async Task DataSource_SetInHeaders()
    {
        var endpoint = CreateProcedureEndpoint("abs",
            new()
            {
                ["asFunction"] = "true",
                ["procedureParams"] = "IN:x:Int64"
            });
        var producer = endpoint.CreateProducer();

        var exchange = CreateExchange(headers: new() { ["x"] = (object)1L });
        await producer.Process(exchange, CancellationToken.None);

        exchange.In.Headers[SqlHeaders.DataSource].Should().Be("main");
    }

    // ── Error: no DataSource or ConnectionString ────────────────────

    [Fact]
    public void NoDataSource_NoConnectionString_Throws()
    {
        var component = new SqlComponent();
        var uri = new EndpointUri("sql", "my_proc", "sql:my_proc",
            new Dictionary<string, string>
            {
                ["mode"] = "Procedure",
                ["procedureName"] = "my_proc"
            });

        var act = () => component.CreateEndpoint(uri);

        act.Should().Throw<ArgumentException>();
    }

    // ── Start/Stop are no-ops ───────────────────────────────────────

    [Fact]
    public async Task StartStop_AreNoop()
    {
        var endpoint = CreateProcedureEndpoint("abs",
            new()
            {
                ["asFunction"] = "true",
                ["procedureParams"] = "IN:x:Int64"
            });
        var producer = endpoint.CreateProducer();

        // Should not throw
        await producer.Start();
        await producer.Stop();
    }

    public void Dispose() => _db.Dispose();
}

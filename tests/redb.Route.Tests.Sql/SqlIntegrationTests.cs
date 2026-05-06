using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;

namespace redb.Route.Tests.Sql;

/// <summary>
/// End-to-end integration tests: SqlConsumer (Poll) → IProcessor → SqlProducer (Execute).
/// Simulates a real route that reads rows from one table and writes results to another,
/// all running against an in-memory SQLite database.
/// </summary>
public class SqlIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly SqliteTestHelper _db = new();
    private SqlConsumer? _consumer;

    // Captured exchanges
    private readonly List<IExchange> _capturedExchanges = [];

    public Task InitializeAsync()
    {
        // Create tables
        _db.Execute("""
            CREATE TABLE outbox (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                message   TEXT NOT NULL,
                processed INTEGER NOT NULL DEFAULT 0
            )
            """);
        _db.Execute("""
            CREATE TABLE audit (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                source_id  INTEGER NOT NULL,
                message    TEXT NOT NULL,
                status     TEXT NOT NULL DEFAULT 'OK'
            )
            """);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_consumer is not null)
            try { await _consumer.Stop(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private SqlEndpoint CreateConsumerEndpoint(string sql, Dictionary<string, string>? extra = null)
    {
        var context = new RouteContext();
        var component = new SqlComponent();
        context.AddComponent(component);
        context.AddToRegistry("main", _db.CreateFactory());
        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Poll",
            ["dataSource"] = "main",
            ["delay"] = "100"
        };
        if (extra != null)
            foreach (var kv in extra)
                parameters[kv.Key] = kv.Value;

        var uri = new EndpointUri("sql", sql, $"sql:{sql}", parameters);
        return (SqlEndpoint)component.CreateEndpoint(uri);
    }

    private SqlEndpoint CreateProducerEndpoint(string sql, Dictionary<string, string>? extra = null)
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
        if (extra != null)
            foreach (var kv in extra)
                parameters[kv.Key] = kv.Value;

        var uri = new EndpointUri("sql", sql, $"sql:{sql}", parameters);
        return (SqlEndpoint)component.CreateEndpoint(uri);
    }

    private void InsertOutboxRow(string message)
    {
        _db.Execute($"INSERT INTO outbox(message) VALUES('{message}')");
    }

    // ── End-to-end: Consumer polls → Processor → Producer writes ────

    [Fact]
    public async Task EndToEnd_ConsumerPollsRows_ProducerInsertsToAudit()
    {
        InsertOutboxRow("order-created");
        InsertOutboxRow("order-shipped");

        var consumerEp = CreateConsumerEndpoint(
            "SELECT id, message FROM outbox WHERE processed = 0",
            new() { ["repeatCount"] = "1" });

        var producerEp = CreateProducerEndpoint(
            "INSERT INTO audit(source_id, message) VALUES(@id, @message)");
        var producer = producerEp.CreateProducer();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ex = ci.Arg<IExchange>();
                lock (_capturedExchanges) _capturedExchanges.Add(ex);
                await producer.Process(ex, ci.Arg<CancellationToken>());
            });

        _consumer = (SqlConsumer)consumerEp.CreateConsumer(processor);
        await _consumer.Start();

        await WaitForCondition(() =>
        {
            lock (_capturedExchanges) return _capturedExchanges.Count >= 2;
        }, 5000);
        await _consumer.Stop();

        var auditRows = _db.Query("SELECT * FROM audit ORDER BY source_id");
        auditRows.Should().HaveCountGreaterThanOrEqualTo(2);
        ((string)auditRows[0]["message"]!).Should().Be("order-created");
        ((string)auditRows[1]["message"]!).Should().Be("order-shipped");
    }

    [Fact]
    public async Task EndToEnd_OnSuccess_MarksRowsAsProcessed()
    {
        InsertOutboxRow("msg-1");
        InsertOutboxRow("msg-2");

        var consumerEp = CreateConsumerEndpoint(
            "SELECT id, message FROM outbox WHERE processed = 0",
            new()
            {
                ["repeatCount"] = "1",
                ["onSuccess"] = "UPDATE outbox SET processed = 1 WHERE id = @id"
            });

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci =>
            {
                lock (_capturedExchanges) _capturedExchanges.Add(ci.Arg<IExchange>());
            });

        _consumer = (SqlConsumer)consumerEp.CreateConsumer(processor);
        await _consumer.Start();

        await WaitForCondition(() =>
        {
            lock (_capturedExchanges) return _capturedExchanges.Count >= 2;
        }, 5000);
        await _consumer.Stop();

        // All rows should be marked processed
        var unprocessed = _db.Query("SELECT * FROM outbox WHERE processed = 0");
        unprocessed.Should().BeEmpty("all rows should have been marked processed via onSuccess");

        var processed = _db.Query("SELECT * FROM outbox WHERE processed = 1");
        processed.Should().HaveCount(2);
    }

    [Fact]
    public async Task EndToEnd_Headers_PropagatedFromConsumerToProducer()
    {
        InsertOutboxRow("check-headers");

        var consumerEp = CreateConsumerEndpoint(
            "SELECT id, message FROM outbox WHERE processed = 0",
            new() { ["repeatCount"] = "1" });

        var processor = Substitute.For<IProcessor>();
        IExchange? captured = null;
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => captured = ci.Arg<IExchange>());

        _consumer = (SqlConsumer)consumerEp.CreateConsumer(processor);
        await _consumer.Start();

        await WaitForCondition(() => captured != null, 5000);
        await _consumer.Stop();

        captured.Should().NotBeNull();
        captured!.In.Headers.Should().ContainKey(SqlHeaders.Query);
        captured.In.Headers.Should().ContainKey(SqlHeaders.RowCount);
        captured.In.Headers.Should().ContainKey(SqlHeaders.ExecutionTime);
        captured.In.Headers[SqlHeaders.DataSource].Should().Be("main");
        captured.Pattern.Should().Be(ExchangePattern.InOnly);

        // Body should be row dict
        captured.In.Body.Should().BeAssignableTo<Dictionary<string, object?>>();
        var row = (Dictionary<string, object?>)captured.In.Body!;
        row.Should().ContainKey("message");
        ((string)row["message"]!).Should().Be("check-headers");
    }

    [Fact]
    public async Task EndToEnd_TransactedConsumer_CommitsOnSuccess()
    {
        InsertOutboxRow("tx-msg");

        var consumerEp = CreateConsumerEndpoint(
            "SELECT id, message FROM outbox WHERE processed = 0",
            new()
            {
                ["repeatCount"] = "1",
                ["transacted"] = "true",
                ["onSuccess"] = "UPDATE outbox SET processed = 1 WHERE id = @id"
            });

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci =>
            {
                lock (_capturedExchanges) _capturedExchanges.Add(ci.Arg<IExchange>());
            });

        _consumer = (SqlConsumer)consumerEp.CreateConsumer(processor);
        await _consumer.Start();

        await WaitForCondition(() =>
        {
            lock (_capturedExchanges) return _capturedExchanges.Count >= 1;
        }, 5000);
        await _consumer.Stop();

        var processed = _db.Query("SELECT * FROM outbox WHERE processed = 1");
        processed.Should().HaveCount(1, "transacted consumer should commit onSuccess update");
    }

    [Fact]
    public async Task EndToEnd_ProducerSelectList_ReturnsQueryResults()
    {
        _db.Execute("INSERT INTO audit(source_id, message, status) VALUES(1, 'A', 'OK')");
        _db.Execute("INSERT INTO audit(source_id, message, status) VALUES(2, 'B', 'FAIL')");
        _db.Execute("INSERT INTO audit(source_id, message, status) VALUES(3, 'C', 'OK')");

        var producerEp = CreateProducerEndpoint(
            "SELECT * FROM audit ORDER BY source_id",
            new() { ["outputType"] = "SelectList" });
        var producer = producerEp.CreateProducer();

        var exchange = new Exchange(new Message());
        await producer.Process(exchange, CancellationToken.None);

        var rows = exchange.In.Body as List<Dictionary<string, object?>>;
        rows.Should().NotBeNull();
        rows.Should().HaveCount(3);
        ((string)rows![0]["message"]!).Should().Be("A");
        ((string)rows[2]["message"]!).Should().Be("C");
    }

    [Fact]
    public async Task EndToEnd_DataArrivesWhilePolling_StillProcessed()
    {
        var consumerEp = CreateConsumerEndpoint(
            "SELECT id, message FROM outbox WHERE processed = 0",
            new()
            {
                ["delete"] = "false",
                ["onSuccess"] = "UPDATE outbox SET processed = 1 WHERE id = @id"
            });

        var count = 0;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => Interlocked.Increment(ref count));

        _consumer = (SqlConsumer)consumerEp.CreateConsumer(processor);
        await _consumer.Start();

        // Wait a poll cycle
        await Task.Delay(200);

        // Insert data while consumer is running
        InsertOutboxRow("late-arrival-1");
        InsertOutboxRow("late-arrival-2");

        await WaitForCondition(() => count >= 2, 5000);
        await _consumer.Stop();

        count.Should().BeGreaterThanOrEqualTo(2);
        var processed = _db.Query("SELECT * FROM outbox WHERE processed = 1");
        processed.Should().HaveCount(2);
    }

    [Fact]
    public async Task EndToEnd_MaxMessagesPerPoll_LimitsRowsPerCycle()
    {
        for (var i = 0; i < 5; i++)
            InsertOutboxRow($"batch-{i}");

        var consumerEp = CreateConsumerEndpoint(
            "SELECT id, message FROM outbox WHERE processed = 0",
            new()
            {
                ["repeatCount"] = "1",
                ["maxMessagesPerPoll"] = "2",
                ["onSuccess"] = "UPDATE outbox SET processed = 1 WHERE id = @id"
            });

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci =>
            {
                lock (_capturedExchanges) _capturedExchanges.Add(ci.Arg<IExchange>());
            });

        _consumer = (SqlConsumer)consumerEp.CreateConsumer(processor);
        await _consumer.Start();

        await WaitForCondition(() =>
        {
            lock (_capturedExchanges) return _capturedExchanges.Count >= 2;
        }, 5000);
        await _consumer.Stop();

        // Only 2 rows should have been processed in the single poll cycle
        lock (_capturedExchanges)
        {
            _capturedExchanges.Should().HaveCount(2, "maxMessagesPerPoll=2 should limit to 2 rows");
        }
    }

    [Fact]
    public async Task EndToEnd_ConsumerToProducer_TransformInProcessor()
    {
        InsertOutboxRow("transform-me");

        var consumerEp = CreateConsumerEndpoint(
            "SELECT id, message FROM outbox WHERE processed = 0",
            new() { ["repeatCount"] = "1" });

        var producerEp = CreateProducerEndpoint(
            "INSERT INTO audit(source_id, message, status) VALUES(@source_id, @message, @status)");
        var producer = producerEp.CreateProducer();

        // Processor transforms the exchange before sending to producer
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ex = ci.Arg<IExchange>();
                var row = (Dictionary<string, object?>)ex.In.Body!;

                // Transform: set headers for producer parameter binding
                ex.In.Headers["source_id"] = row["id"];
                ex.In.Headers["message"] = ((string)row["message"]!).ToUpperInvariant();
                ex.In.Headers["status"] = "TRANSFORMED";

                await producer.Process(ex, ci.Arg<CancellationToken>());
            });

        _consumer = (SqlConsumer)consumerEp.CreateConsumer(processor);
        await _consumer.Start();

        await WaitForCondition(() => _db.Query("SELECT * FROM audit").Count >= 1, 5000);
        await _consumer.Stop();

        var auditRows = _db.Query("SELECT * FROM audit");
        auditRows.Should().HaveCount(1);
        ((string)auditRows[0]["message"]!).Should().Be("TRANSFORM-ME");
        ((string)auditRows[0]["status"]!).Should().Be("TRANSFORMED");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static async Task WaitForCondition(Func<bool> condition, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(50);
        }
    }
}

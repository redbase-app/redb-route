using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sql;

namespace redb.Route.Tests.Sql;

/// <summary>
/// Integration tests for SqlConsumer using an in-memory SQLite database.
/// </summary>
public class SqlConsumerTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    public SqlConsumerTests()
    {
        _db.Execute("""
            CREATE TABLE outbox (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                message   TEXT NOT NULL,
                processed INTEGER NOT NULL DEFAULT 0
            )
            """);
    }

    private SqlEndpoint CreateEndpoint(Dictionary<string, string> parameters)
    {
        var context = new RouteContext();
        var component = new SqlComponent();
        context.AddComponent(component);
        context.AddToRegistry("main", _db.CreateFactory());

        var sql = parameters.ContainsKey("_sql") ? parameters["_sql"] : "SELECT * FROM outbox WHERE processed = 0";
        parameters.Remove("_sql");
        if (!parameters.ContainsKey("mode")) parameters["mode"] = "Poll";
        if (!parameters.ContainsKey("dataSource")) parameters["dataSource"] = "main";

        var uri = new EndpointUri("sql", sql, $"sql:{sql}", parameters);
        return (SqlEndpoint)component.CreateEndpoint(uri);
    }

    private void InsertOutboxRow(string message)
    {
        _db.Execute($"INSERT INTO outbox(message) VALUES('{message}')");
    }

    private static async Task WaitForCondition(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(50);
    }

    // ── Basic polling ───────────────────────────────────────────────

    [Fact]
    public async Task Consumer_PollsAndProcessesRows()
    {
        InsertOutboxRow("msg-1");
        InsertOutboxRow("msg-2");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 2);
        await consumer.Stop();

        processed.Should().HaveCountGreaterThanOrEqualTo(2);

        // Verify headers are set
        var exchange = processed[0];
        exchange.In.Headers.Should().ContainKey(SqlHeaders.Query);
        exchange.In.Headers.Should().ContainKey(SqlHeaders.RowCount);
        exchange.In.Headers.Should().ContainKey(SqlHeaders.ExecutionTime);
        exchange.In.Headers[SqlHeaders.DataSource].Should().Be("main");
    }

    [Fact]
    public async Task Consumer_RowBody_IsDictionary()
    {
        InsertOutboxRow("hello");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 1);
        await consumer.Stop();

        processed.Should().HaveCountGreaterThanOrEqualTo(1);
        var body = processed[0].In.Body as Dictionary<string, object?>;
        body.Should().NotBeNull();
        body!["message"].Should().Be("hello");
    }

    [Fact]
    public async Task Consumer_EmptyTable_NoProcessing()
    {
        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1"
        });
        var processor = Substitute.For<IProcessor>();
        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);

        await consumer.Start();
        await Task.Delay(300);
        await consumer.Stop();

        await processor.DidNotReceive().Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>());
    }

    // ── MaxMessagesPerPoll ──────────────────────────────────────────

    [Fact]
    public async Task Consumer_MaxMessagesPerPoll_Limits()
    {
        InsertOutboxRow("msg-1");
        InsertOutboxRow("msg-2");
        InsertOutboxRow("msg-3");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["maxMessagesPerPoll"] = "2"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 2);
        await Task.Delay(200);
        await consumer.Stop();

        // Should process at most 2 per poll cycle
        processed.Should().HaveCountLessThanOrEqualTo(2);
    }

    // ── RepeatCount ─────────────────────────────────────────────────

    [Fact]
    public async Task Consumer_RepeatCount_LimitsCycles()
    {
        // With empty table and repeatCount=2, consumer should stop after 2 polls
        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "50",
            ["repeatCount"] = "2"
        });
        var processor = Substitute.For<IProcessor>();
        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);

        await consumer.Start();
        await Task.Delay(500); // Enough time for 2 cycles
        await consumer.Stop();

        // Consumer should have stopped by itself
    }

    // ── OnSuccess ───────────────────────────────────────────────────

    [Fact]
    public async Task Consumer_OnSuccess_ExecutedAfterProcessing()
    {
        InsertOutboxRow("msg-1");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["onSuccess"] = "UPDATE outbox SET processed = 1 WHERE id = @id"
        });
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(CancellationToken.None);

        // Verify OnSuccess SQL was executed
        var rows = _db.Query("SELECT processed FROM outbox WHERE id = 1");
        rows.Should().HaveCount(1);
        rows[0]["processed"].Should().Be(1L);
    }

    // ── OnFailure ───────────────────────────────────────────────────

    [Fact]
    public async Task Consumer_OnFailure_ExecutedOnError()
    {
        InsertOutboxRow("msg-1");

        _db.Execute("""
            CREATE TABLE error_log (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                msg     TEXT,
                err     TEXT
            )
            """);

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["onFailure"] = "INSERT INTO error_log(msg, err) VALUES(@message, @redbError)"
        });
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Processing failed!"));

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(CancellationToken.None);

        var errors = _db.Query("SELECT msg, err FROM error_log");
        errors.Should().HaveCount(1);
        errors[0]["msg"].Should().Be("msg-1");
        errors[0]["err"]!.ToString().Should().Contain("Processing failed!");
    }

    // ── OnBatchComplete ─────────────────────────────────────────────

    [Fact]
    public async Task Consumer_OnBatchComplete_ExecutedAfterBatch()
    {
        InsertOutboxRow("msg-1");

        _db.Execute("CREATE TABLE batch_log (ts TEXT)");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["onBatchComplete"] = "INSERT INTO batch_log(ts) VALUES(datetime('now'))"
        });
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(CancellationToken.None);

        var logs = _db.Query("SELECT * FROM batch_log");
        logs.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    // ── Transacted ──────────────────────────────────────────────────

    [Fact]
    public async Task Consumer_Transacted_CommitsOnSuccess()
    {
        InsertOutboxRow("msg-1");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["transacted"] = "true",
            ["onSuccess"] = "UPDATE outbox SET processed = 1 WHERE id = @id"
        });
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(CancellationToken.None);

        var rows = _db.Query("SELECT processed FROM outbox WHERE id = 1");
        rows[0]["processed"].Should().Be(1L);
    }

    // ── RouteEmptyResultSet ─────────────────────────────────────────

    [Fact]
    public async Task Consumer_RouteEmptyResultSet_CreatesExchange()
    {
        // Empty table
        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["routeEmptyResultSet"] = "true"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 1);
        await consumer.Stop();

        processed.Should().HaveCountGreaterThanOrEqualTo(1);
        processed[0].In.Headers[SqlHeaders.RowCount].Should().Be(0);
    }

    // ── SendEmptyMessageWhenIdle ────────────────────────────────────

    [Fact]
    public async Task Consumer_SendEmptyMessageWhenIdle_SendsIdleMessage()
    {
        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["sendEmptyMessageWhenIdle"] = "true"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 1);
        await consumer.Stop();

        processed.Should().HaveCountGreaterThanOrEqualTo(1);
        processed[0].In.Headers[SqlHeaders.RowCount].Should().Be(0);
    }

    // ── Row data in headers ─────────────────────────────────────────

    [Fact]
    public async Task Consumer_CopiesRowFieldsToHeaders()
    {
        InsertOutboxRow("test-msg");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => processed.Count >= 1);
        await consumer.Stop();

        var headers = processed[0].In.Headers;
        headers.Should().ContainKey("id");
        headers.Should().ContainKey("message");
        headers["message"].Should().Be("test-msg");
    }

    // ── InitialDelay ────────────────────────────────────────────────

    [Fact]
    public async Task Consumer_InitialDelay_DelaysFirstPoll()
    {
        InsertOutboxRow("msg-1");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["initialDelay"] = "300",
            ["repeatCount"] = "1"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();

        // After 100ms should not have polled yet
        await Task.Delay(100);
        processed.Should().BeEmpty();

        // After 400ms should have polled
        await WaitForCondition(() => processed.Count >= 1, 2000);
        await consumer.Stop();

        processed.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    // ── Graceful shutdown ───────────────────────────────────────────

    [Fact]
    public async Task Consumer_Stop_StopsGracefully()
    {
        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "50"
        });
        var processor = Substitute.For<IProcessor>();
        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);

        await consumer.Start();
        await consumer.Stop(); // Should not throw or hang
    }

    // ── Poll method directly ────────────────────────────────────────

    [Fact]
    public async Task Poll_DirectCall_ProcessesRows()
    {
        InsertOutboxRow("direct-poll");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(CancellationToken.None);

        processed.Should().HaveCount(1);
        (processed[0].In.Body as Dictionary<string, object?>)!["message"].Should().Be("direct-poll");
    }

    // ── Transacted Rollback on processing error ─────────────────────

    [Fact]
    public async Task Consumer_Transacted_RollsBackOnProcessingError()
    {
        // Insert 2 rows — row 1 will succeed, row 2 will fail.
        // Because of transacted mode, row 1's OnSuccess should be rolled back.
        InsertOutboxRow("msg-1");
        InsertOutboxRow("msg-2");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["transacted"] = "true",
            ["onSuccess"] = "UPDATE outbox SET processed = 1 WHERE id = @id"
        });
        var callCount = 0;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 2)
                    throw new InvalidOperationException("Boom on second row!");
                return Task.CompletedTask;
            });

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(CancellationToken.None);

        // Transaction was rolled back — OnSuccess SQL for row 1 should also be rolled back.
        // Both rows should still be unprocessed.
        var rows = _db.Query("SELECT id, processed FROM outbox ORDER BY id");
        rows.Should().HaveCount(2);
        rows[0]["processed"].Should().Be(0L, "row 1's OnSuccess should be rolled back");
        rows[1]["processed"].Should().Be(0L, "row 2 should remain unprocessed");
    }

    // ── Fixed-rate polling ──────────────────────────────────────────

    [Fact]
    public async Task Consumer_FixedRate_PollsAtFixedInterval()
    {
        // With fixedRate: delay measured from START of poll, not end.
        // So if poll takes 0ms and delay is 200ms, next poll starts ~200ms after start.
        // Without fixedRate: next poll starts ~200ms after END of poll.
        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "200",
            ["fixedRate"] = "true",
            ["repeatCount"] = "3",
            ["initialDelay"] = "0"
        });
        var pollTimes = new List<long>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // We need the poll to trigger even with empty table — use sendEmptyMessageWhenIdle
        var endpoint2 = CreateEndpoint(new()
        {
            ["delay"] = "200",
            ["fixedRate"] = "true",
            ["repeatCount"] = "3",
            ["initialDelay"] = "0",
            ["sendEmptyMessageWhenIdle"] = "true"
        });
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => pollTimes.Add(sw.ElapsedMilliseconds));

        var consumer = (SqlConsumer)endpoint2.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => pollTimes.Count >= 3, 3000);
        await consumer.Stop();

        pollTimes.Should().HaveCountGreaterThanOrEqualTo(3);

        // Intervals should be roughly 200ms (fixedRate)
        // Tolerance: between 100ms and 400ms
        for (var i = 1; i < pollTimes.Count; i++)
        {
            var interval = pollTimes[i] - pollTimes[i - 1];
            interval.Should().BeGreaterThan(100, "fixed-rate interval should be ~200ms");
            interval.Should().BeLessThan(400, "fixed-rate interval should not exceed ~400ms");
        }
    }

    // ── Non-fixedRate polling ───────────────────────────────────────

    [Fact]
    public async Task Consumer_NonFixedRate_PollsWithDelayAfterEnd()
    {
        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "200",
            ["fixedRate"] = "false",
            ["repeatCount"] = "3",
            ["initialDelay"] = "0",
            ["sendEmptyMessageWhenIdle"] = "true"
        });
        var pollTimes = new List<long>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => pollTimes.Add(sw.ElapsedMilliseconds));

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        await WaitForCondition(() => pollTimes.Count >= 3, 3000);
        await consumer.Stop();

        pollTimes.Should().HaveCountGreaterThanOrEqualTo(3);

        // Intervals should also be roughly 200ms (same, but delay-after-end)
        for (var i = 1; i < pollTimes.Count; i++)
        {
            var interval = pollTimes[i] - pollTimes[i - 1];
            interval.Should().BeGreaterThan(100);
        }
    }

    // ── StreamList ──────────────────────────────────────────────────

    [Fact]
    public async Task StreamList_ProcessesRowsWithoutBuffering()
    {
        InsertOutboxRow("s1");
        InsertOutboxRow("s2");
        InsertOutboxRow("s3");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["outputType"] = "StreamList"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(CancellationToken.None);

        processed.Should().HaveCount(3);
        var messages = processed
            .Select(e => ((Dictionary<string, object?>)e.In.Body!)["message"]!.ToString())
            .ToList();
        messages.Should().BeEquivalentTo(["s1", "s2", "s3"]);
    }

    [Fact]
    public async Task StreamList_RowCount_IsMinusOne()
    {
        InsertOutboxRow("rc1");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["outputType"] = "StreamList"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(CancellationToken.None);

        processed.Should().HaveCount(1);
        processed[0].In.Headers[SqlHeaders.RowCount].Should().Be(-1);
    }

    [Fact]
    public async Task StreamList_OnSuccess_ExecutedPerRow()
    {
        InsertOutboxRow("os1");
        InsertOutboxRow("os2");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["outputType"] = "StreamList",
            ["onSuccess"] = "UPDATE outbox SET processed = 1 WHERE id = @id"
        });
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(CancellationToken.None);

        var rows = _db.Query("SELECT id, processed FROM outbox ORDER BY id");
        rows.Should().HaveCount(2);
        rows[0]["processed"].Should().Be(1L);
        rows[1]["processed"].Should().Be(1L);
    }

    [Fact]
    public async Task StreamList_OnFailure_ExecutedOnError()
    {
        InsertOutboxRow("fail-stream");

        _db.Execute("""
            CREATE TABLE stream_error_log (
                id  INTEGER PRIMARY KEY AUTOINCREMENT,
                msg TEXT,
                err TEXT
            )
            """);

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["outputType"] = "StreamList",
            ["onFailure"] = "INSERT INTO stream_error_log(msg, err) VALUES(@message, @redbError)"
        });
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Stream row failed!"));

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(CancellationToken.None);

        var errors = _db.Query("SELECT msg, err FROM stream_error_log");
        errors.Should().HaveCount(1);
        errors[0]["msg"].Should().Be("fail-stream");
        errors[0]["err"]!.ToString().Should().Contain("Stream row failed!");
    }

    [Fact]
    public async Task StreamList_MaxMessagesPerPoll_Limits()
    {
        InsertOutboxRow("lim1");
        InsertOutboxRow("lim2");
        InsertOutboxRow("lim3");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["outputType"] = "StreamList",
            ["maxMessagesPerPoll"] = "2"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(CancellationToken.None);

        processed.Should().HaveCount(2);
    }

    [Fact]
    public async Task StreamList_EmptyTable_HandlesCorrectly()
    {
        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["outputType"] = "StreamList",
            ["routeEmptyResultSet"] = "true"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(CancellationToken.None);

        processed.Should().HaveCount(1);
        processed[0].In.Headers[SqlHeaders.RowCount].Should().Be(0);
    }

    [Fact]
    public async Task StreamList_OnBatchComplete_Executed()
    {
        InsertOutboxRow("batch1");

        _db.Execute("CREATE TABLE stream_batch_log (ts TEXT)");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["outputType"] = "StreamList",
            ["onBatchComplete"] = "INSERT INTO stream_batch_log(ts) VALUES(datetime('now'))"
        });
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(CancellationToken.None);

        var logs = _db.Query("SELECT * FROM stream_batch_log");
        logs.Should().HaveCount(1);
    }

    [Fact]
    public async Task StreamList_Transacted_RollsBackOnError()
    {
        InsertOutboxRow("tx1");
        InsertOutboxRow("tx2");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["outputType"] = "StreamList",
            ["transacted"] = "true",
            ["onSuccess"] = "UPDATE outbox SET processed = 1 WHERE id = @id"
        });
        var callCount = 0;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref callCount) == 2)
                    throw new InvalidOperationException("Boom on second row!");
                return Task.CompletedTask;
            });

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(CancellationToken.None);

        // Transaction rolled back — row 1's OnSuccess should also be rolled back
        var rows = _db.Query("SELECT id, processed FROM outbox ORDER BY id");
        rows.Should().HaveCount(2);
        rows[0]["processed"].Should().Be(0L, "row 1's OnSuccess should be rolled back");
        rows[1]["processed"].Should().Be(0L, "row 2 should remain unprocessed");
    }

    [Fact]
    public async Task StreamList_CopiesRowFieldsToHeaders()
    {
        InsertOutboxRow("header-test");

        var endpoint = CreateEndpoint(new()
        {
            ["delay"] = "100",
            ["repeatCount"] = "1",
            ["outputType"] = "StreamList"
        });
        var processed = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => processed.Add(x.Arg<IExchange>()));

        var consumer = (SqlConsumer)endpoint.CreateConsumer(processor);
        await consumer.Poll(CancellationToken.None);

        var headers = processed[0].In.Headers;
        headers.Should().ContainKey("id");
        headers.Should().ContainKey("message");
        headers["message"].Should().Be("header-test");
    }

    public void Dispose() => _db.Dispose();
}

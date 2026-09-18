using System.Data;
using System.Data.Common;
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Sql.Mapping;
using redb.Route.Telemetry;
using redb.Route.Tests.Sql.Batch.Fakes;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.Batch;

/// <summary>
/// What a batch reports besides the rows it wrote: the rows its statements return (<c>RETURNING</c> / <c>OUTPUT</c>) in
/// <c>redbSql.generatedKeys</c> — in item order, the body left alone, as Apache Camel's <c>CamelSqlGeneratedKeyRows</c> — and
/// the batch tags on the <c>sql.execute</c> span. Spans are picked by the endpoint marker, never by position: the in-memory
/// exporter listens to the process-wide activity source.
/// </summary>
public sealed class BatchResultsTests : IDisposable
{
    private const string Insert = "INSERT INTO t (id) VALUES (:#id)";
    private readonly SqliteTestHelper _db = new();

    public void Dispose() => _db.Dispose();

    // ── Generated keys ──────────────────────────────────────────────

    [Fact]
    public async Task GeneratedKeys_DbBatch_OrderMatchesItems()
    {
        var connection = new FakeBatchConnection { SupportsBatch = true, OnExecuteBatchReader = (_, batch) => KeyResults(batch) };
        var items = Items(5);

        var (exchange, thrown) = await RunFakeAsync(connection, items, batchSize: 2, outputType: "SelectList");

        thrown.Should().BeNull(Outcome.Describe(thrown));
        connection.BatchSizes.Should().Equal(2, 2, 1);
        exchange.In.Headers.Should().ContainKey(SqlHeaders.GeneratedKeys);
        exchange.In.Headers[SqlHeaders.GeneratedKeys].Should().BeAssignableTo<IList<Dictionary<string, object?>>>()
            .Which.Select(k => k["key"]).Should().Equal(new object?[] { 0L, 10L, 20L, 30L, 40L }, "keys follow the items across chunks");
        exchange.In.Headers["redbSql.generatedKeysRowCount"].Should().Be(5);
        exchange.In.Headers[SqlHeaders.UpdateCount].Should().Be(5, "the rows affected are summed over every command of the batch");
        exchange.In.Body.Should().BeSameAs(items, "the returned rows go to a header; the body is left alone");
    }

    /// <summary>Its <c>Key</c> cannot hold the keys the fake returns (10, 20, …): a configuration error, not an item's.</summary>
    public sealed class NarrowKeyRow
    {
        public bool Key { get; set; }
    }

    [Fact]
    public async Task GeneratedKeys_MappingError_IsAConfigurationErrorNotAChunkFailure()
    {
        var connection = new FakeBatchConnection { SupportsBatch = true, OnExecuteBatchReader = (_, batch) => KeyResults(batch) };

        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, new FixedConnectionFactory(connection), Insert, new()
        {
            ["outputType"] = "SelectList",
            ["outputClass"] = typeof(NarrowKeyRow).AssemblyQualifiedName!,
            ["batchSize"] = "2",
        });
        var exchange = new Exchange(new Message(Items(3)));
        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, CancellationToken.None));

        thrown.Should().BeOfType<SqlRowMappingException>(Outcome.Describe(thrown))
            .Which.Message.Should().Contain("Key", "the mapping error reaches the route as itself");
        thrown!.Data.Contains("redbSql.batchFailedChunk").Should().BeFalse("no statement failed; the driver has nothing to name");
        exchange.In.Headers.Should().NotContainKey(SqlHeaders.BatchFailedIndex, "a mapping error is not an item's failure");
        connection.Transaction!.RolledBack.Should().BeTrue();
    }

    [Fact]
    public async Task GeneratedKeys_OutputTypeAuto_InsertCollectsNothing()
    {
        var connection = new FakeBatchConnection { SupportsBatch = true };

        var (exchange, thrown) = await RunFakeAsync(connection, Items(3), batchSize: 10, outputType: "Auto");

        thrown.Should().BeNull(Outcome.Describe(thrown));
        exchange.In.Headers.Should().NotContainKey(SqlHeaders.GeneratedKeys,
            "Auto makes an INSERT a statement without rows; outputType is set explicitly to collect RETURNING / OUTPUT rows");
        exchange.In.Headers.Should().NotContainKey("redbSql.generatedKeysRowCount");
    }

    // ── Telemetry ───────────────────────────────────────────────────

    [Fact]
    public async Task Telemetry_SuccessfulBatch_TagsSizeAndStrategy()
    {
        _db.Execute("CREATE TABLE tel_items (id INTEGER PRIMARY KEY)");
        var marker = $"batch-telemetry-{Guid.NewGuid():N}";

        var span = await CaptureSpanAsync(marker, async () =>
        {
            await using var context = new RouteContext();
            var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(),
                $"INSERT INTO tel_items (id) VALUES (:#id) /* {marker} */", new() { ["batchSize"] = "10" });
            return await Outcome.Of(() => endpoint.CreateProducer().Process(new Exchange(new Message(Items(3))), CancellationToken.None));
        });

        span.GetTagItem("db.operation.batch.size").Should().Be(3, "the OpenTelemetry database convention's batch size");
        span.GetTagItem("redb.sql.batch.strategy").Should().Be("Commands");
        span.GetTagItem("redb.sql.batch.chunks").Should().BeNull("only a DbBatch has round trips to count");
        span.GetTagItem("redb.sql.batch.failed_index").Should().BeNull();
    }

    [Fact]
    public async Task Telemetry_SingleItemBatch_NoBatchSizeTag()
    {
        _db.Execute("CREATE TABLE tel_one (id INTEGER PRIMARY KEY)");
        var marker = $"batch-telemetry-{Guid.NewGuid():N}";

        var span = await CaptureSpanAsync(marker, async () =>
        {
            await using var context = new RouteContext();
            var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(),
                $"INSERT INTO tel_one (id) VALUES (:#id) /* {marker} */", new() { ["batchSize"] = "10" });
            return await Outcome.Of(() => endpoint.CreateProducer().Process(new Exchange(new Message(Items(1))), CancellationToken.None));
        });

        span.GetTagItem("redb.sql.batch.strategy").Should().Be("Commands");
        span.GetTagItem("db.operation.batch.size").Should().BeNull("the convention counts a batch only from two operations");
    }

    [Fact]
    public async Task Telemetry_FailedDbBatch_TagsFailedIndex()
    {
        var marker = $"batch-telemetry-{Guid.NewGuid():N}";
        var connection = new FakeBatchConnection
        {
            SupportsBatch = true,
            OnExecuteBatch = (index, batch) => index == 1
                ? throw new FakeDbException("duplicate key", batch.BatchCommands[1])
                : batch.BatchCommands.Count,
        };

        var span = await CaptureSpanAsync(marker, async () =>
        {
            var (_, thrown) = await RunFakeAsync(connection, Items(10), batchSize: 3, outputType: "None", marker);
            thrown.Should().BeOfType<FakeDbException>(Outcome.Describe(thrown));
            return null;
        });

        span.GetTagItem("redb.sql.batch.failed_index").Should().Be(4, "the second command of the second chunk is item 4");
        span.GetTagItem("redb.sql.batch.strategy").Should().Be("DbBatch");
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static List<Dictionary<string, object?>> Items(int count) =>
        Enumerable.Range(0, count).Select(i => new Dictionary<string, object?> { ["id"] = i }).ToList();

    /// <summary>One result set per command of the batch: a single row whose <c>key</c> is ten times the command's id.</summary>
    private static DbDataReader KeyResults(FakeDbBatch batch)
    {
        var tables = batch.BatchCommands.Cast<DbBatchCommand>().Select(command =>
        {
            var table = new DataTable();
            table.Columns.Add("key", typeof(long));
            table.Rows.Add(Convert.ToInt64(command.Parameters["id"].Value) * 10);
            return table;
        }).ToArray();
        return new DataTableReader(tables);
    }

    private static async Task<(Exchange Exchange, Exception? Thrown)> RunFakeAsync(
        FakeBatchConnection connection, List<Dictionary<string, object?>> items, int batchSize, string outputType, string? marker = null)
    {
        await using var context = new RouteContext();
        var sql = marker is null ? Insert : $"{Insert} /* {marker} */";
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, new FixedConnectionFactory(connection), sql, new()
        {
            ["outputType"] = outputType,
            ["batchSize"] = batchSize.ToString(),
        });
        var exchange = new Exchange(new Message(items));
        var thrown = await Outcome.Of(() => endpoint.CreateProducer().Process(exchange, CancellationToken.None));
        return (exchange, thrown);
    }

    private static async Task<Activity> CaptureSpanAsync(string marker, Func<Task<Exception?>> run)
    {
        var activities = new List<Activity>();
        using (var tracer = Sdk.CreateTracerProviderBuilder()
                   .AddSource(RouteActivitySource.SourceName)
                   .AddInMemoryExporter(activities)
                   .Build()!)
        {
            var thrown = await run();
            thrown.Should().BeNull(Outcome.Describe(thrown));
            tracer.ForceFlush(1000);
        }

        return activities.Should().ContainSingle(a => HasMarker(a, marker)).Which;
    }

    private static bool HasMarker(Activity activity, string marker) =>
        activity.GetTagItem("redb.route.endpoint") is string endpoint && endpoint.Contains(marker, StringComparison.Ordinal);
}

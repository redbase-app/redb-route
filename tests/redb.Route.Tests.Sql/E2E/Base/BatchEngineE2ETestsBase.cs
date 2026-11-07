using System.Data.Common;
using System.Diagnostics;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;
using Xunit.Abstractions;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// The batch engine against a real database: large batches arrive whole, the strategy follows the driver's capability
/// (<c>DbBatch</c> where the connection can create one), a failure deep in the batch leaves nothing and names the item.
/// The large batch logs its time: a measurement for the wave report, not an assertion.
/// </summary>
public abstract class BatchEngineE2ETestsBase(ITestOutputHelper output) : IAsyncLifetime
{
    private SqlE2EDatabase? _db;

    /// <summary>The provider under test.</summary>
    protected abstract SqlE2EProvider Provider { get; }

    private SqlE2EDatabase Db => _db ?? throw new InvalidOperationException("The table is created in InitializeAsync.");

    private string Insert => $"INSERT INTO {Db.Table} (id, val, n) VALUES (:#id, :#val, :#n)";

    /// <inheritdoc />
    public async Task InitializeAsync() => _db = await SqlE2EDatabase.CreateAsync(Provider, Provider.CreateNullableTableSql);

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        try
        {
            if (_db is not null)
                await _db.DisposeAsync();
        }
        finally
        {
            (Provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task LargeBatch_AllPersisted_StrategyByDriverCapability()
    {
        const int count = 10_000;
        var items = Enumerable.Range(1, count)
            .Select(i => new Dictionary<string, object?> { ["id"] = i, ["val"] = "v" + i, ["n"] = (long)i })
            .ToList();

        var stopwatch = Stopwatch.StartNew();
        var (exchange, thrown) = await SqlBatchRun.RunAsync(Db, Insert, items, breakOnError: null, batchSize: 500);
        stopwatch.Stop();

        var strategy = exchange.In.Headers.TryGetValue(SqlHeaders.BatchStrategy, out var s) ? s : "none";
        output.WriteLine($"[batch-measure] {Provider.Name}: {count} rows, batchSize=500, strategy={strategy}, {stopwatch.ElapsedMilliseconds} ms");

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Convert.ToInt32((await Db.QueryAsync($"SELECT COUNT(*) FROM {Db.Table}"))[0][0]).Should().Be(count);
        Convert.ToInt32(exchange.In.Headers[SqlHeaders.UpdateCount]).Should().Be(count);

        if (Provider.CanCreateBatch)
        {
            strategy.Should().Be("DbBatch", "the connection can create a DbBatch and the batch breaks on the first error");
            exchange.In.Headers["redbSql.batchChunkCount"].Should().Be(20, "10 000 statements in round trips of 500");
        }
        else
        {
            strategy.Should().Be("Commands");
            exchange.In.Headers.Should().NotContainKey("redbSql.batchChunkCount");
        }
    }

    [Fact]
    public async Task FailureInLaterChunk_NoRowsAndExactIndex()
    {
        var items = Enumerable.Range(1, 1200)
            .Select(i => new Dictionary<string, object?> { ["id"] = i == 701 ? 1 : i, ["val"] = "v" + i, ["n"] = null })
            .ToList();

        var (_, thrown) = await SqlBatchRun.RunAsync(Db, Insert, items, breakOnError: true, batchSize: 500);

        thrown.Should().BeAssignableTo<DbException>(Outcome.Describe(thrown));
        thrown!.Data[SqlHeaders.BatchFailedIndex].Should().Be(700, "item 700 repeats the key of item 0");
        (await Db.ReadIdsAsync()).Should().BeEmpty("a batch that breaks on error rolls every chunk back");
    }

    [Fact]
    public async Task NullsAndMixedClrTypes_Persisted()
    {
        var items = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = 1, ["val"] = "a", ["n"] = 1 },
            new() { ["id"] = 2, ["val"] = null, ["n"] = 2L },
            new() { ["id"] = 3, ["val"] = "ccc", ["n"] = null },
        };

        var (_, thrown) = await SqlBatchRun.RunAsync(Db, Insert, items, breakOnError: null);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        var rows = await Db.QueryAsync($"SELECT id, val, n FROM {Db.Table} ORDER BY id");
        rows.Select(r => $"{Convert.ToInt64(r[0])}|{r[1]}|{(r[2] is null ? "" : Convert.ToInt64(r[2]).ToString())}")
            .Should().Equal("1|a|1", "2||2", "3|ccc|");
    }
}

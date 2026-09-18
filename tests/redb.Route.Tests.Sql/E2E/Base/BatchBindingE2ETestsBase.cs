using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Aggregation;
using redb.Route.Core;
using redb.Route.DataFormats.Csv;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// Batch items in the shapes real routes produce — CSV rows, JSON objects, POCOs, aggregated exchanges — written to a real
/// database: every source record must arrive with its own values.
/// </summary>
public abstract class BatchBindingE2ETestsBase : IAsyncLifetime
{
    private const string EntryUri = "direct://batch-binding";

    private SqlE2EDatabase? _db;

    /// <summary>The provider under test.</summary>
    protected abstract SqlE2EProvider Provider { get; }

    private SqlE2EDatabase Db => _db ?? throw new InvalidOperationException("The table is created in InitializeAsync.");

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
    public async Task CsvWithHeader_ToBatch_AllRowsPersisted()
    {
        const string csv = "id,val,n\n1,a,10\n2,b,\n3,,30\n";
        // CSV values are text, and PostgreSQL does not convert a text parameter to an integer column: the statement casts.
        var sql = $"INSERT INTO {Db.Table} (id, val, n) VALUES (CAST(:#id AS int), :#val, CAST(:#n AS bigint))";

        await RunRouteAsync(route => route.UnmarshalCsv<List<Dictionary<string, string>>>().To(BatchUri(sql)), csv);

        (await ReadRowsAsync()).Should().Equal("1|a|10", "2|b|", "3||30");
    }

    [Fact]
    public async Task JsonArray_ToBatch_AllRowsPersisted()
    {
        const string json = """[{"id":1,"val":"a","n":10},{"id":2,"val":"b","n":null},{"id":3,"val":null,"n":9007199254740993}]""";
        var sql = $"INSERT INTO {Db.Table} (id, val, n) VALUES (:#id, :#val, :#n)";

        await RunRouteAsync(route => route.Unmarshal<List<Dictionary<string, object?>>>("application/json").To(BatchUri(sql)), json);

        (await ReadRowsAsync()).Should().Equal("1|a|10", "2|b|", "3||9007199254740993");
    }

    [Fact]
    public async Task JsonArrayUnmarshalledToObject_ToBatch_AllRowsPersisted()
    {
        const string json = """[{"id":1,"val":"a","n":10},{"id":2,"val":"b","n":null},{"id":3,"val":null,"n":30}]""";
        var sql = $"INSERT INTO {Db.Table} (id, val, n) VALUES (:#id, :#val, :#n)";

        // Unmarshal to object gives one JsonElement array: a batch source since 17.4 (decision 18).
        await RunRouteAsync(route => route.Unmarshal<object>("application/json").To(BatchUri(sql)), json);

        (await ReadRowsAsync()).Should().Equal("1|a|10", "2|b|", "3||30");
    }

    [Fact]
    public async Task PocoList_ToBatch_AllRowsPersisted()
    {
        var items = new List<BindingRow>
        {
            new() { Id = 1, Val = "a", N = 10 },
            new() { Id = 2, Val = "b" },
            new() { Id = 3, N = 30 },
        };

        var (_, thrown) = await SqlBatchRun.RunAsync(Db, $"INSERT INTO {Db.Table} (id, val, n) VALUES (:#id, :#val, :#n)", items, breakOnError: null);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        (await ReadRowsAsync()).Should().Equal("1|a|10", "2|b|", "3||30");
    }

    [Fact]
    public async Task AggregateGroupedExchange_ParamsFromHeaders_ToBatch()
    {
        const int size = 50;
        var sql = $"INSERT INTO {Db.Table} (id, val) VALUES (:#id, :#val)";

        await using var context = CreateContext();
        context.AddRoutes(r => r.From(EntryUri)
            .Aggregate(_ => "all", AggregationStrategies.GroupedExchange(), agg => agg.In.Body is List<IExchange> { Count: >= size })
                .To(BatchUri(sql, "&param.val=${header.auditVal}"))
            .EndAggregate());
        await context.Start();
        var producer = context.GetEndpoint(EntryUri).CreateProducer();
        await producer.Start();

        for (var i = 1; i <= size; i++)
        {
            var exchange = new Exchange(new Message($"event-{i}"));
            exchange.In.Headers["id"] = i;
            exchange.In.Headers["auditVal"] = $"v{i}";
            await producer.Process(exchange);
        }

        var rows = await WaitForRowsAsync(size);
        rows.Should().Equal(Enumerable.Range(1, size).Select(i => $"{i}|v{i}|"),
            "every aggregated message is one item, bound from its own headers");
    }

    public sealed class BindingRow
    {
        public int Id { get; set; }
        public string? Val { get; set; }
        public long? N { get; set; }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string BatchUri(string sql, string extra = "") =>
        $"sql:{sql}?dataSource={SqlEndpointHarness.DataSourceName}&batchSize=500&outputType=None{extra}";

    private RouteContext CreateContext()
    {
        var context = new RouteContext();
        context.AddComponent(new SqlComponent());
        context.AddToRegistry(SqlEndpointHarness.DataSourceName, Db.CreateConnectionFactory());
        return context;
    }

    private async Task RunRouteAsync(Func<IRouteDefinition, IRouteDefinition> build, object body)
    {
        await using var context = CreateContext();
        context.AddRoutes(r => build(r.From(EntryUri)));
        await context.Start();
        var producer = context.GetEndpoint(EntryUri).CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message(body)));
    }

    private async Task<List<string>> ReadRowsAsync()
    {
        var rows = await Db.QueryAsync($"SELECT id, val, n FROM {Db.Table} ORDER BY id");
        return rows.Select(r => $"{Convert.ToInt64(r[0])}|{r[1]}|{(r[2] is null ? "" : Convert.ToInt64(r[2]).ToString())}").ToList();
    }

    private async Task<List<string>> WaitForRowsAsync(int count)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var rows = await ReadRowsAsync();
            if (rows.Count >= count || stopwatch.Elapsed > TimeSpan.FromSeconds(10))
                return rows;
            await Task.Delay(100);
        }
    }
}

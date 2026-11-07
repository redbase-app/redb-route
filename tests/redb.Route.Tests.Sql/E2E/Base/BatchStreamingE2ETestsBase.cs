using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// A streamed query feeding a batch in the same database: <c>outputType=StreamList</c> hands the next endpoint an
/// <c>IAsyncEnumerable</c> that holds a reader and its connection; the batch reads it chunk by chunk and must release that
/// connection when it is done. Not for SQLite: a reader and a writer on the same file block each other.
/// </summary>
public abstract class BatchStreamingE2ETestsBase : IAsyncLifetime
{
    private const int Rows = 20_000;

    private SqlE2EDatabase? _source;
    private SqlE2EDatabase? _target;

    /// <summary>The provider under test.</summary>
    protected abstract SqlE2EProvider Provider { get; }

    private SqlE2EDatabase Source => _source ?? throw new InvalidOperationException("The tables are created in InitializeAsync.");
    private SqlE2EDatabase Target => _target ?? throw new InvalidOperationException("The tables are created in InitializeAsync.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _source = await SqlE2EDatabase.CreateAsync(Provider, Provider.CreateNullableTableSql);
        _target = await SqlE2EDatabase.CreateAsync(Provider, Provider.CreateNullableTableSql);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        try
        {
            if (_source is not null)
                await _source.DisposeAsync();
        }
        finally
        {
            try
            {
                if (_target is not null)
                    await _target.DisposeAsync();
            }
            finally
            {
                (Provider as IDisposable)?.Dispose();
            }
        }
    }

    [Fact]
    public async Task StreamList_ToBatch_SameDatabase_AllCopiedAndSourceConnectionReleased()
    {
        var seed = Enumerable.Range(1, Rows)
            .Select(i => new Dictionary<string, object?> { ["id"] = i, ["val"] = "v" + i, ["n"] = (long)i })
            .ToList();
        var (_, seedError) = await SqlBatchRun.RunAsync(Source, $"INSERT INTO {Source.Table} (id, val, n) VALUES (:#id, :#val, :#n)",
            seed, breakOnError: null, batchSize: 1000);
        seedError.Should().BeNull(Outcome.Describe(seedError));

        var factory = Source.CreateConnectionFactory();
        await using var context = new RouteContext();
        context.AddComponent(new SqlComponent());
        context.AddToRegistry(SqlEndpointHarness.DataSourceName, factory);
        context.AddRoutes(r => r.From("direct://copy")
            .To($"sql:SELECT id, val, n FROM {Source.Table} ORDER BY id?dataSource={SqlEndpointHarness.DataSourceName}&outputType=StreamList")
            .To($"sql:INSERT INTO {Target.Table} (id, val, n) VALUES (:#id, :#val, :#n)?dataSource={SqlEndpointHarness.DataSourceName}&batchSize=1000&outputType=None"));
        await context.Start();
        var producer = context.GetEndpoint("direct://copy").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("copy"));
        var thrown = await Outcome.Of(() => producer.Process(exchange));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Convert.ToInt32((await Target.QueryAsync($"SELECT COUNT(*) FROM {Target.Table}"))[0][0]).Should().Be(Rows);
        exchange.In.Headers[SqlHeaders.BatchItemCount].Should().Be(Rows);
        factory.OpenConnections.Should().Be(0,
            "the batch consumed the streamed result to its end, which closes the reader's connection, and its own connection is disposed");
    }
}

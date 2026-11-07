using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.CrossProvider;

/// <summary>
/// The same route shape across two databases: a PostgreSQL query streamed straight into a SQL Server batch. The connector is
/// one component for any ADO.NET provider; only the data sources differ.
/// </summary>
[Trait("Category", "Integration")]
[Trait("SqlProvider", "postgres+mssql")]
[Trait("SqlSuite", "Connector")]
public sealed class PostgresToMsSqlStreamingE2ETests : IAsyncLifetime
{
    private const int Rows = 5_000;

    private readonly PostgresE2EProvider _postgres = new();
    private readonly MsSqlE2EProvider _msSql = new(xactAbortOn: false);
    private SqlE2EDatabase? _source;
    private SqlE2EDatabase? _target;

    private SqlE2EDatabase Source => _source ?? throw new InvalidOperationException("The tables are created in InitializeAsync.");
    private SqlE2EDatabase Target => _target ?? throw new InvalidOperationException("The tables are created in InitializeAsync.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _source = await SqlE2EDatabase.CreateAsync(_postgres, _postgres.CreateNullableTableSql);
        _target = await SqlE2EDatabase.CreateAsync(_msSql, _msSql.CreateNullableTableSql);
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
            if (_target is not null)
                await _target.DisposeAsync();
        }
    }

    [Fact]
    public async Task StreamList_Postgres_ToBatch_MsSql_AllCopied()
    {
        var seed = Enumerable.Range(1, Rows)
            .Select(i => new Dictionary<string, object?> { ["id"] = i, ["val"] = "v" + i, ["n"] = i % 7 == 0 ? null : (long)i })
            .ToList();
        var (_, seedError) = await SqlBatchRun.RunAsync(Source, $"INSERT INTO {Source.Table} (id, val, n) VALUES (:#id, :#val, :#n)",
            seed, breakOnError: null, batchSize: 1000);
        seedError.Should().BeNull(Outcome.Describe(seedError));

        var sourceFactory = Source.CreateConnectionFactory();
        var targetFactory = Target.CreateConnectionFactory();
        await using var context = new RouteContext();
        context.AddComponent(new SqlComponent());
        context.AddToRegistry("pg", sourceFactory);
        context.AddToRegistry("ms", targetFactory);
        context.AddRoutes(r => r.From("direct://pg-to-ms")
            .To($"sql:SELECT id, val, n FROM {Source.Table} ORDER BY id?dataSource=pg&outputType=StreamList")
            .To($"sql:INSERT INTO {Target.Table} (id, val, n) VALUES (:#id, :#val, :#n)?dataSource=ms&batchSize=500&outputType=None"));
        await context.Start();
        var producer = context.GetEndpoint("direct://pg-to-ms").CreateProducer();
        await producer.Start();

        var thrown = await Outcome.Of(() => producer.Process(new Exchange(new Message("copy"))));

        thrown.Should().BeNull(Outcome.Describe(thrown));
        var copied = await Target.QueryAsync($"SELECT COUNT(*), COUNT(n), MAX(id) FROM {Target.Table}");
        Convert.ToInt32(copied[0][0]).Should().Be(Rows);
        Convert.ToInt32(copied[0][1]).Should().Be(Rows - Rows / 7, "NULLs travel as NULLs");
        Convert.ToInt32(copied[0][2]).Should().Be(Rows);
        sourceFactory.OpenConnections.Should().Be(0, "the PostgreSQL reader's connection is released after the stream is consumed");
        targetFactory.OpenConnections.Should().Be(0);
    }
}

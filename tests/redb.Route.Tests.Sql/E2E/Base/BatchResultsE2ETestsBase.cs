using System.Collections;
using redb.Route.Sql;
using redb.Route.Sql.Mapping;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.E2E.Base;

/// <summary>
/// The rows a batch's statements return (<c>INSERT … RETURNING</c> on PostgreSQL and SQLite, <c>OUTPUT inserted.*</c> on SQL
/// Server) reach <c>redbSql.generatedKeys</c> in item order — across <c>DbBatch</c> chunks too — while the body stays the
/// list that was written. A failed item of a batch that goes on returns no keys.
/// </summary>
public abstract class BatchResultsE2ETestsBase : IAsyncLifetime
{
    private SqlE2EDatabase? _db;

    /// <summary>The provider under test.</summary>
    protected abstract SqlE2EProvider Provider { get; }

    private SqlE2EDatabase Db => _db ?? throw new InvalidOperationException("The table is created in InitializeAsync.");

    public sealed class KeyRow
    {
        public int Id { get; set; }
    }

    /// <inheritdoc />
    public async Task InitializeAsync() => _db = await SqlE2EDatabase.CreateAsync(Provider);

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
    public async Task ReturningKeys_CollectedInItemOrder_AcrossChunks()
    {
        var items = Enumerable.Range(11, 5).Select(i => new Dictionary<string, object?> { ["id"] = i, ["val"] = "v" + i }).ToList();

        var (exchange, thrown) = await SqlBatchRun.RunAsync(Db, Provider.ReturningInsertSql(Db.Table), items, breakOnError: null,
            batchSize: 2, new() { ["outputType"] = "SelectList" });

        thrown.Should().BeNull(Outcome.Describe(thrown));
        KeyIds(exchange).Should().Equal(new[] { 11, 12, 13, 14, 15 }, "keys follow the items, chunk after chunk");
        exchange.In.Headers["redbSql.generatedKeysRowCount"].Should().Be(5);
        Convert.ToInt32(exchange.In.Headers[SqlHeaders.UpdateCount]).Should().Be(5);
        exchange.In.Body.Should().BeSameAs(items, "the returned rows go to a header; the body is left alone");
        (await Db.ReadIdsAsync()).Should().Equal(11, 12, 13, 14, 15);
    }

    /// <summary>Its <c>Id</c> cannot hold an integer key: a configuration error, the same for every item.</summary>
    public sealed class NarrowKeyRow
    {
        public bool Id { get; set; }
    }

    [Fact]
    public async Task ReturningKeys_MappingError_EndsTheBatchInContinueModeToo()
    {
        var items = new List<Dictionary<string, object?>> { new() { ["id"] = 31, ["val"] = "a" }, new() { ["id"] = 32, ["val"] = "b" } };

        var (exchange, thrown) = await SqlBatchRun.RunAsync(Db, Provider.ReturningInsertSql(Db.Table), items, breakOnError: false,
            batchSize: 10, new() { ["outputType"] = "SelectList", ["outputClass"] = typeof(NarrowKeyRow).AssemblyQualifiedName! });

        thrown.Should().BeOfType<SqlRowMappingException>("a key that cannot be mapped is the endpoint's configuration, not an item's " +
            "failure to undo and go past: " + Outcome.Describe(thrown));
        exchange.In.Headers.Should().NotContainKey(SqlHeaders.BatchErrors);
        (await Db.ReadIdsAsync()).Should().BeEmpty("the batch rolled back");
    }

    [Fact]
    public async Task ReturningKeys_OutputClass_TypedList()
    {
        var items = new List<Dictionary<string, object?>> { new() { ["id"] = 21, ["val"] = "a" }, new() { ["id"] = 22, ["val"] = "b" } };

        var (exchange, thrown) = await SqlBatchRun.RunAsync(Db, Provider.ReturningInsertSql(Db.Table), items, breakOnError: null,
            batchSize: 10, new() { ["outputType"] = "SelectList", ["outputClass"] = typeof(KeyRow).AssemblyQualifiedName! });

        thrown.Should().BeNull(Outcome.Describe(thrown));
        exchange.In.Headers.Should().ContainKey(SqlHeaders.GeneratedKeys);
        exchange.In.Headers[SqlHeaders.GeneratedKeys].Should().BeAssignableTo<IList<KeyRow>>()
            .Which.Select(k => k.Id).Should().Equal(21, 22);
    }

    [Fact]
    public async Task Continue_FailedItemReturnsNoKeys()
    {
        var (exchange, thrown) = await SqlBatchRun.RunAsync(Db, Provider.ReturningInsertSql(Db.Table), Provider.DuplicateKeyItems(),
            breakOnError: false, batchSize: 10, new() { ["outputType"] = "SelectList" });

        if (Provider.DuplicateKeyDestroysTransaction)
        {
            thrown.Should().NotBeNull("the duplicate key ends the transaction on this provider, so the batch stops");
            return;
        }

        thrown.Should().BeNull(Outcome.Describe(thrown));
        SqlBatchRun.BatchErrorIndexes(exchange).Should().Equal(1);
        KeyIds(exchange).Should().Equal(new[] { 1, 3 }, "the item undone to its savepoint returned no key");
        exchange.In.Headers["redbSql.generatedKeysRowCount"].Should().Be(2);
    }

    [Fact]
    public async Task PlainInsert_AutoOutputType_NoKeysHeader()
    {
        var (exchange, thrown) = await SqlBatchRun.RunAsync(Db, Provider.InsertSql(Db.Table), Provider.DuplicateKeyItems().Take(1).ToList(),
            breakOnError: null, batchSize: 10, new() { ["outputType"] = "Auto" });

        thrown.Should().BeNull(Outcome.Describe(thrown));
        exchange.In.Headers.Should().NotContainKey(SqlHeaders.GeneratedKeys);
    }

    private static List<int> KeyIds(redb.Route.Core.Exchange exchange)
    {
        exchange.In.Headers.Should().ContainKey(SqlHeaders.GeneratedKeys, "RETURNING / OUTPUT rows are collected with outputType=SelectList");
        return exchange.In.Headers[SqlHeaders.GeneratedKeys].Should().BeAssignableTo<IEnumerable>().Subject
            .Cast<IDictionary<string, object?>>()
            .Select(row => Convert.ToInt32(row.Values.First()))
            .ToList();
    }
}

using redb.Route.Core;
using redb.Route.Sql;
using redb.Route.Tests.Sql.E2E.Infrastructure;

namespace redb.Route.Tests.Sql.Batch;

/// <summary>
/// A placeholder with no value anywhere is an error, as in Apache Camel ("Cannot find key ... to use when setting named
/// parameter"); a key that is present with a null value binds NULL.
/// </summary>
public sealed class BatchStrictBindingTests : IDisposable
{
    private const string Insert = "INSERT INTO strict_items (id, val) VALUES (:#id, :#val)";

    private readonly SqliteTestHelper _db = new();

    public BatchStrictBindingTests() => _db.Execute("CREATE TABLE strict_items (id INTEGER NOT NULL, val TEXT NULL)");

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Batch_MissingKey_BreakMode_FailsWithItemIndexAndParameterName()
    {
        var carrier = Carrier(new Dictionary<string, object?> { ["id"] = 1, ["val"] = "a" }, new Dictionary<string, object?> { ["id"] = 2 });

        var thrown = await RunAsync(carrier, breakOnError: true);

        thrown.Should().BeOfType<InvalidOperationException>(Outcome.Describe(thrown));
        thrown!.Message.Should().Contain("item 1").And.Contain(":#val");
        thrown.Data[SqlHeaders.BatchFailedIndex].Should().Be(1);
        Ids().Should().BeEmpty("the batch breaks on that item and rolls back");
    }

    [Fact]
    public async Task Batch_MissingKey_ContinueMode_ReportedAsItemError()
    {
        var carrier = Carrier(new Dictionary<string, object?> { ["id"] = 1, ["val"] = "a" }, new Dictionary<string, object?> { ["id"] = 2 }, new Dictionary<string, object?> { ["id"] = 3, ["val"] = "c" });

        var thrown = await RunAsync(carrier, breakOnError: false);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Ids().Should().Equal(new object?[] { 1L, 3L }, "the item without a value is skipped, the others commit");
        carrier.In.Headers.Should().ContainKey(SqlHeaders.BatchErrors);
        var errors = carrier.In.Headers[SqlHeaders.BatchErrors].Should().BeAssignableTo<IReadOnlyList<SqlBatchItemError>>().Subject;
        errors.Should().ContainSingle().Which.Index.Should().Be(1);
        errors[0].Message.Should().Contain(":#val");
    }

    [Fact]
    public async Task Batch_KeyWithNullValue_BindsNull()
    {
        var carrier = Carrier(new Dictionary<string, object?> { ["id"] = 1, ["val"] = null });

        var thrown = await RunAsync(carrier);

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Values().Should().Equal(new object?[] { null }, "a key that is present binds its value, null included");
    }

    [Fact]
    public async Task Batch_ExplicitTemplateEvaluatingToNull_BindsNull()
    {
        var carrier = Carrier(new Dictionary<string, object?> { ["id"] = 1 });

        var thrown = await RunAsync(carrier, options: new() { ["param.val"] = "${header.missing}" });

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Values().Should().Equal(new object?[] { null }, "an explicit param.* always has a value, even when it evaluates to null");
    }

    [Fact]
    public async Task Batch_ScalarItems_AllNamesResolvedElsewhere_Bind()
    {
        var carrier = new Exchange(new Message(new List<int> { 1, 2 }));
        carrier.In.Headers["val"] = "h";

        var thrown = await RunAsync(carrier, options: new() { ["param.id"] = "${body}" });

        thrown.Should().BeNull(Outcome.Describe(thrown));
        Ids().Should().Equal(new object?[] { 1L, 2L });
        Values().Should().Equal(new object?[] { "h", "h" }, "a scalar item carries no names, the header still provides one");
    }

    private static Exchange Carrier(params Dictionary<string, object?>[] items) => new(new Message(items.ToList()));

    private async Task<Exception?> RunAsync(Exchange carrier, bool? breakOnError = null, Dictionary<string, string>? options = null)
    {
        var parameters = new Dictionary<string, string> { ["outputType"] = "None", ["batchSize"] = "10" };
        if (breakOnError is { } mode)
            parameters["breakBatchOnError"] = mode ? "true" : "false";
        if (options is not null)
            foreach (var (key, value) in options)
                parameters[key] = value;

        await using var context = new RouteContext();
        var endpoint = SqlEndpointHarness.CreateEndpoint(context, _db.CreateFactory(), Insert, parameters);
        return await Outcome.Of(() => endpoint.CreateProducer().Process(carrier, CancellationToken.None));
    }

    private List<object?> Ids() => _db.Query("SELECT id FROM strict_items ORDER BY id").Select(r => r["id"]).ToList();

    private List<object?> Values() => _db.Query("SELECT val FROM strict_items ORDER BY id").Select(r => r["val"]).ToList();
}

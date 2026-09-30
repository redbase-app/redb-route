using redb.Core;
using redb.Core.Attributes;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Tests.Core.Integration;

/// <summary>A props type for the query probes: a run tag and a rank.</summary>
[RedbScheme("Route Query Probe")]
public sealed class QueryProbeProps
{
    public string? Tag { get; set; }
    public int Rank { get; set; }
}

/// <summary>
/// <c>RedbQuery(whereRedb:, orderByRedb:)</c> against a real database: the base-field condition
/// reaches the provider's SQL and filters there, combined with a props condition. Subclassed per
/// provider. Every object carries this instance's tag and hangs under its own parent, so
/// parallel runs over the shared test database never see each other's rows.
/// </summary>
public abstract class RedbQueryBaseFieldsIntegrationTests : IAsyncLifetime
{
    private readonly IRedbService _redb;
    private readonly string _tag = Guid.NewGuid().ToString("N");
    private readonly Guid _key = Guid.NewGuid();
    private readonly List<long> _created = [];
    private long _parent;
    private long _first;
    private long _second;

    protected RedbQueryBaseFieldsIntegrationTests(IRedbService redb) => _redb = redb;

    public async Task InitializeAsync()
    {
        await _redb.SyncSchemeAsync<QueryProbeProps>();
        _parent = await Save(new RedbObject<QueryProbeProps>(new QueryProbeProps { Tag = _tag, Rank = 0 }));
        _first = await Save(new RedbObject<QueryProbeProps>(new QueryProbeProps { Tag = _tag, Rank = 1 })
            { ParentId = _parent, ValueGuid = _key, ValueString = "A-" + _tag });
        _second = await Save(new RedbObject<QueryProbeProps>(new QueryProbeProps { Tag = _tag, Rank = 2 })
            { ParentId = _parent, ValueGuid = Guid.NewGuid(), ValueString = "B-" + _tag });
    }

    public async Task DisposeAsync()
    {
        // Children first, then the parent.
        foreach (var id in Enumerable.Reverse(_created))
            await _redb.DeleteAsync(id);
    }

    private async Task<long> Save(RedbObject<QueryProbeProps> entity)
    {
        var id = await _redb.SaveAsync(entity);
        _created.Add(id);
        return id;
    }

    private async Task<List<RedbObject<QueryProbeProps>>> Run(Action<IRouteDefinition> query,
        params (string Key, object? Value)[] headers)
        => (await RunRaw(query, headers)).Should().BeOfType<List<RedbObject<QueryProbeProps>>>().Subject;

    private async Task<object?> RunRaw(Action<IRouteDefinition> query,
        params (string Key, object? Value)[] headers)
    {
        await using var context = new RouteContext();
        context.AddService(typeof(IRedbService), _redb);
        context.AddRoutes(r => query(r.From("direct://probe")));
        await context.Start();
        var producer = context.GetEndpoint("direct://probe").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message(null));
        foreach (var (key, value) in headers)
            exchange.In.Headers[key] = value;
        exchange.In.Headers["parent"] = _parent;
        await producer.Process(exchange);
        exchange.Exception.Should().BeNull();
        return exchange.In.Body;
    }

    [Fact]
    public async Task OutputFirst_FollowsTheOrdering_AndIsNullWhenNothingMatches()
    {
        var latest = await RunRaw(r => r.RedbQuery(typeof(QueryProbeProps),
            whereRedb: "ParentId == header.parent", orderByRedb: "Id", descending: true,
            outputType: RedbQueryOutput.First));
        var none = await RunRaw(r => r.RedbQuery(typeof(QueryProbeProps),
            whereRedb: "ParentId == header.parent AND ValueString == 'nothing'",
            outputType: RedbQueryOutput.First));

        latest.Should().BeOfType<RedbObject<QueryProbeProps>>().Which.Id.Should().Be(Math.Max(_first, _second));
        none.Should().BeNull();
    }

    [Fact]
    public async Task OutputCountAndAny_AnswerInTheDatabase()
    {
        var count = await RunRaw(r => r.RedbQuery(typeof(QueryProbeProps),
            whereRedb: "ParentId == header.parent", outputType: RedbQueryOutput.Count));
        var any = await RunRaw(r => r.RedbQuery(typeof(QueryProbeProps),
            whereRedb: "ParentId == header.parent", where: "Rank > 5", outputType: RedbQueryOutput.Any));

        count.Should().Be(2);
        any.Should().Be(false);
    }

    [Fact]
    public async Task WhereRedb_OnValueGuidAndParent_FiltersInTheDatabase()
    {
        var found = await Run(r => r.RedbQuery(typeof(QueryProbeProps),
            whereRedb: "ParentId == header.parent AND ValueGuid == header.key"),
            ("key", _key.ToString()));

        found.Select(o => o.Id).Should().Equal(_first);
    }

    [Fact]
    public async Task WhereRedb_ValueStringIn_AListFromAHeader_FiltersInTheDatabase()
    {
        // One query for a list of keys: the IN goes to the provider's SQL, not to a loop.
        var found = await Run(r => r.RedbQuery(typeof(QueryProbeProps),
            whereRedb: "ParentId == header.parent AND ValueString in header.codes"),
            ("codes", new List<string> { "A-" + _tag, "missing" }));

        found.Select(o => o.Id).Should().Equal(_first);
    }

    [Fact]
    public async Task WhereRedb_IdNotIn_ALiteralList()
    {
        var found = await Run(r => r.RedbQuery(typeof(QueryProbeProps),
            whereRedb: $"ParentId == header.parent AND Id not in ({_first})"));

        found.Select(o => o.Id).Should().Equal(_second);
    }

    [Fact]
    public async Task Where_PropsIn_ConvertsTheElementsToTheMembersType()
    {
        // Rank is an int props field; the header carries strings. Each element converts as a single
        // comparison converts its value, and the storage runs the IN on the props column.
        var found = await Run(r => r.RedbQuery(typeof(QueryProbeProps),
            whereRedb: "ParentId == header.parent", where: "Rank in header.ranks"),
            ("ranks", new List<string> { "2", "9" }));

        found.Select(o => o.Id).Should().Equal(_second);
    }

    [Fact]
    public async Task An_empty_list_finds_nothing_in_the_database()
    {
        var found = await Run(r => r.RedbQuery(typeof(QueryProbeProps),
            whereRedb: "ParentId == header.parent AND ValueString in header.codes"),
            ("codes", new List<string>()));

        found.Should().BeEmpty("an empty IN means nothing matches, not everything and not an error");
    }

    [Fact]
    public async Task WhereRedb_AndWhere_Combine()
    {
        var found = await Run(r => r.RedbQuery(typeof(QueryProbeProps),
            whereRedb: "ParentId == header.parent", where: "Rank >= 2"));

        found.Select(o => o.Id).Should().Equal(_second);
    }

    [Fact]
    public async Task WhereRedb_ValueStringFromAHeader_AndDateCreateFromText()
    {
        var found = await Run(r => r.RedbQuery(typeof(QueryProbeProps),
            whereRedb: "ValueString == header.code AND DateCreate >= header.since AND ParentId == header.parent"),
            ("code", "B-" + _tag), ("since", DateTimeOffset.UtcNow.AddDays(-1).ToString("O")));

        found.Select(o => o.Id).Should().Equal(_second);
    }

    [Fact]
    public async Task OrderByRedb_Id_Descending_TakesTheLatestChild()
    {
        var found = await Run(r => r.RedbQuery(typeof(QueryProbeProps),
            whereRedb: "ParentId == header.parent", orderByRedb: "Id", descending: true, take: 1));

        found.Select(o => o.Id).Should().Equal(Math.Max(_first, _second));
    }
}

[Collection("Postgres")]
public sealed class PostgresRedbQueryBaseFieldsTests(PostgresFixture fixture)
    : RedbQueryBaseFieldsIntegrationTests(fixture.Redb);

[Collection("MsSql")]
public sealed class MsSqlRedbQueryBaseFieldsTests(MsSqlFixture fixture)
    : RedbQueryBaseFieldsIntegrationTests(fixture.Redb);

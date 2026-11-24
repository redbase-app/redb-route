using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Tests.Core.Integration;

/// <summary>Props with two numeric fields for a field-against-field <c>redbQuery</c>.</summary>
public sealed class FieldPairProbeProps
{
    public string? Tag { get; set; }
    public int Price { get; set; }
    public int Cost { get; set; }
}

/// <summary>
/// A <c>redbQuery</c> where-string that compares a field with another field (<c>Price &gt; Cost</c>) against a real
/// database: the storage filters on both columns and returns the rows LINQ gives, in either order of the operands.
/// Every object hangs under this instance's own parent, so parallel runs never see each other's rows.
/// </summary>
public abstract class RedbQueryFieldAgainstFieldIntegrationTests : IAsyncLifetime
{
    private readonly IRedbService _redb;
    private readonly string _tag = Guid.NewGuid().ToString("N");
    private readonly List<long> _created = [];
    private long _parent;

    protected RedbQueryFieldAgainstFieldIntegrationTests(IRedbService redb) => _redb = redb;

    public async Task InitializeAsync()
    {
        await _redb.SyncSchemeAsync<FieldPairProbeProps>();
        _parent = await Save(new RedbObject<FieldPairProbeProps>(new FieldPairProbeProps { Tag = _tag }));
        foreach (var (price, cost) in new[] { (5, 3), (3, 5), (4, 4) })
            await Save(new RedbObject<FieldPairProbeProps>(new FieldPairProbeProps { Tag = _tag, Price = price, Cost = cost })
                { ParentId = _parent });
    }

    public async Task DisposeAsync()
    {
        // Children first, then the parent.
        foreach (var id in Enumerable.Reverse(_created))
            await _redb.DeleteAsync(id);
    }

    private async Task<long> Save(RedbObject<FieldPairProbeProps> entity)
    {
        var id = await _redb.SaveAsync(entity);
        _created.Add(id);
        return id;
    }

    [Theory]
    [InlineData("Price > Cost", new[] { 5 })]
    [InlineData("Cost < Price", new[] { 5 })]
    [InlineData("Price == Cost", new[] { 4 })]
    [InlineData("Cost == Price", new[] { 4 })]
    [InlineData("Price <= Cost", new[] { 3, 4 })]
    [InlineData("Price != Cost", new[] { 3, 5 })]
    public async Task A_field_against_another_field_returns_the_rows_linq_gives(string where, int[] expectedPrices)
    {
        await using var context = new RouteContext();
        context.AddService(typeof(IRedbService), _redb);
        context.AddRoutes(r => r.From("direct://pair")
            .RedbQuery(typeof(FieldPairProbeProps), where: where, whereRedb: "ParentId == header.parent"));
        await context.Start();
        var producer = context.GetEndpoint("direct://pair").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message(null));
        exchange.In.Headers["parent"] = _parent;

        await producer.Process(exchange);

        exchange.Exception.Should().BeNull();
        exchange.In.Body.Should().BeOfType<List<RedbObject<FieldPairProbeProps>>>().Subject
            .Select(o => o.Props.Price).OrderBy(p => p).Should().Equal(expectedPrices, $"where: {where}");
    }
}

[Collection("Postgres")]
public sealed class PostgresRedbQueryFieldAgainstFieldTests(PostgresFixture fixture)
    : RedbQueryFieldAgainstFieldIntegrationTests(fixture.Redb);

[Collection("MsSql")]
public sealed class MsSqlRedbQueryFieldAgainstFieldTests(MsSqlFixture fixture)
    : RedbQueryFieldAgainstFieldIntegrationTests(fixture.Redb);

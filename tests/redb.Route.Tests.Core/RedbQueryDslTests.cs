using System.Linq.Expressions;
using redb.Core;
using redb.Core.Models.Entities;
using redb.Core.Query;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.RedbCore.Extensions;
using redb.Route.RedbCore.Query;

namespace redb.Route.Tests.Core;

/// <summary>
/// The <c>RedbQuery</c> verb (Route-XML Шаг 2): the where-string rides the engine's ONE
/// expression AST into redb LINQ. The queryable is substituted and the predicate that reaches
/// <c>Where</c> is COMPILED and probed against sample props — semantic checks, no database.
/// </summary>
public sealed class RedbQueryDslTests
{
    public sealed class CustomerProps
    {
        public string? Name { get; set; }
    }

    public sealed class OrderProps
    {
        public string? Status { get; set; }
        public int Total { get; set; }
        public string? Note { get; set; }
        public bool Urgent { get; set; }
        public CustomerProps? Customer { get; set; }
    }

    private readonly IRedbService _redb = Substitute.For<IRedbService>();
    private readonly IRouteContext _context = Substitute.For<IRouteContext>();
    private readonly IOrderedRedbQueryable<OrderProps> _query = Substitute.For<IOrderedRedbQueryable<OrderProps>>();
    private Expression<Func<OrderProps, bool>>? _captured;
    private Expression<Func<redb.Core.Models.Contracts.IRedbObject, bool>>? _capturedRedb;

    public RedbQueryDslTests()
    {
        _context.GetService<IRedbService>().Returns(_redb);
        _redb.Query<OrderProps>().Returns(_query);
        _query.Where(Arg.Do<Expression<Func<OrderProps, bool>>>(e => _captured = e)).Returns(_query);
        _query.WhereRedb(Arg.Do<Expression<Func<redb.Core.Models.Contracts.IRedbObject, bool>>>(e => _capturedRedb = e)).Returns(_query);
        _query.OrderBy(Arg.Any<Expression<Func<OrderProps, int>>>()).Returns(_query);
        _query.OrderByDescending(Arg.Any<Expression<Func<OrderProps, int>>>()).Returns(_query);
        _query.Skip(Arg.Any<int>()).Returns(_query);
        _query.Take(Arg.Any<int>()).Returns(_query);
        _query.ToListAsync().Returns([]);
    }

    private RouteDefinition NewRoute()
    {
        var route = new RouteDefinition();
        route._context = _context;
        return route;
    }

    private async Task<Func<OrderProps, bool>> PredicateFor(string where,
        params (string Key, object? Value)[] headers)
    {
        var route = NewRoute();
        route.RedbQuery(typeof(OrderProps), where: where);
        var exchange = new Exchange();
        foreach (var (key, value) in headers)
            exchange.In.Headers[key] = value;
        await route.Outputs[0].CreateProcessor(_context).Process(exchange, CancellationToken.None);
        _captured.Should().NotBeNull();
        return _captured!.Compile();
    }

    // ── translation semantics (predicate probed on samples) ──────────

    [Fact]
    public async Task Comparison_OnAStringProp()
    {
        var predicate = await PredicateFor("Status == 'open'");

        predicate(new OrderProps { Status = "open" }).Should().BeTrue();
        predicate(new OrderProps { Status = "closed" }).Should().BeFalse();
    }

    [Fact]
    public async Task ValueSide_FoldsPerMessage_FromTheHeader()
    {
        var predicate = await PredicateFor("Total > header.min", ("min", 10));

        predicate(new OrderProps { Total = 11 }).Should().BeTrue();
        predicate(new OrderProps { Total = 10 }).Should().BeFalse();
    }

    [Fact]
    public async Task AndOrNot_Combine()
    {
        var predicate = await PredicateFor("(Status == 'open' OR Urgent == true) AND NOT (Total < 5)");

        predicate(new OrderProps { Status = "open", Total = 5 }).Should().BeTrue();
        predicate(new OrderProps { Status = "closed", Urgent = true, Total = 7 }).Should().BeTrue();
        predicate(new OrderProps { Status = "closed", Total = 7 }).Should().BeFalse();
        predicate(new OrderProps { Status = "open", Total = 4 }).Should().BeFalse();
    }

    [Fact]
    public async Task Contains_TranslatesOntoTheStringProp()
    {
        var predicate = await PredicateFor("contains(Note, header.q)", ("q", "url"));

        predicate(new OrderProps { Note = "urgently" }).Should().BeFalse();
        predicate(new OrderProps { Note = "curl it" }).Should().BeTrue();
    }

    [Fact]
    public async Task NestedPath_WalksTheClrProperties()
    {
        var predicate = await PredicateFor("Customer.Name == 'acme'");

        predicate(new OrderProps { Customer = new CustomerProps { Name = "acme" } }).Should().BeTrue();
        predicate(new OrderProps { Customer = new CustomerProps { Name = "other" } }).Should().BeFalse();
    }

    [Fact]
    public async Task MirroredComparison_ValueOnTheLeft()
    {
        var predicate = await PredicateFor("50 <= Total");

        predicate(new OrderProps { Total = 50 }).Should().BeTrue();
        predicate(new OrderProps { Total = 49 }).Should().BeFalse();
    }

    [Fact]
    public async Task NullComparison_OnANullableProp()
    {
        var predicate = await PredicateFor("Note == null");

        predicate(new OrderProps()).Should().BeTrue();
        predicate(new OrderProps { Note = "x" }).Should().BeFalse();
    }

    [Fact]
    public async Task ValueOnlyGate_FoldsToABooleanConstant()
    {
        var open = await PredicateFor("header.enabled == true AND Status == 'open'", ("enabled", true));
        open(new OrderProps { Status = "open" }).Should().BeTrue();

        var gated = await PredicateFor("header.enabled == true AND Status == 'open'", ("enabled", false));
        gated(new OrderProps { Status = "open" }).Should().BeFalse("the gate folded to false for THIS message");
    }

    // ── loud refusals at route build ─────────────────────────────────

    [Fact]
    public void ArithmeticOnProps_RefusesWithTheSpecHint()
    {
        var act = () => NewRoute().RedbQuery(typeof(OrderProps), where: "Total % 2 == 0");

        act.Should().Throw<InvalidOperationException>().WithMessage("*#spec*");
    }

    [Fact]
    public void UnknownIdentifier_RefusesNamingTheCandidates()
    {
        var act = () => NewRoute().RedbQuery(typeof(OrderProps), where: "Statuss == 'open'");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Statuss*").WithMessage("*Status*", "the property list helps fix the typo");
    }

    [Fact]
    public void OrderingComparisonOnAString_RefusesWithTheHint()
    {
        var act = () => NewRoute().RedbQuery(typeof(OrderProps), where: "Status > 'a'");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*string props property*").WithMessage("*#spec*");
    }

    [Fact]
    public void UnboundedScan_Refuses()
    {
        var act = () => NewRoute().RedbQuery(typeof(OrderProps));

        act.Should().Throw<ArgumentException>().WithMessage("*unbounded*");
    }

    [Fact]
    public void OrderByANonPropsPath_Refuses()
    {
        var act = () => NewRoute().RedbQuery(typeof(OrderProps), orderBy: "header.x");

        act.Should().Throw<InvalidOperationException>().WithMessage("*props property path*");
    }

    // ── query assembly ───────────────────────────────────────────────

    [Fact]
    public async Task OrderBySkipTake_ReachTheQueryable()
    {
        var route = NewRoute();
        route.RedbQuery(typeof(OrderProps), where: "Status == 'open'",
            orderBy: "Total", descending: true, skip: 20, take: 10);

        await route.Outputs[0].CreateProcessor(_context).Process(new Exchange(), CancellationToken.None);

        _query.Received(1).OrderByDescending(Arg.Any<Expression<Func<OrderProps, int>>>());
        _query.Received(1).Skip(20);
        _query.Received(1).Take(10);
        await _query.Received(1).ToListAsync();
    }

    [Fact]
    public async Task FilterSpec_AppliesFromTheRegistry()
    {
        var spec = Substitute.For<IRedbQuerySpec<OrderProps>>();
        spec.Apply(_query).Returns(_query);
        _context.GetFromRegistry<object>("#active").Returns(spec);
        var route = NewRoute();
        route.RedbQuery(typeof(OrderProps), filter: "#active");

        await route.Outputs[0].CreateProcessor(_context).Process(new Exchange(), CancellationToken.None);

        spec.Received(1).Apply(_query);
    }

    [Fact]
    public async Task TheListLandsInTheTarget()
    {
        List<RedbObject<OrderProps>> stored = [new()];
        _query.ToListAsync().Returns(stored);
        var route = NewRoute();
        route.RedbQuery(typeof(OrderProps), take: 5, target: "header:orders");

        var exchange = new Exchange();
        exchange.In.Body = "kept";
        await route.Outputs[0].CreateProcessor(_context).Process(exchange, CancellationToken.None);

        exchange.In.Body.Should().Be("kept");
        exchange.In.Headers["orders"].Should().BeSameAs(stored);
    }

    // ── whereRedb / orderByRedb: the base fields of the stored object ─

    private async Task<Expression<Func<redb.Core.Models.Contracts.IRedbObject, bool>>> BasePredicateFor(string whereRedb,
        params (string Key, object? Value)[] headers)
    {
        var route = NewRoute();
        route.RedbQuery(typeof(OrderProps), whereRedb: whereRedb);
        var exchange = new Exchange();
        foreach (var (key, value) in headers)
            exchange.In.Headers[key] = value;
        await route.Outputs[0].CreateProcessor(_context).Process(exchange, CancellationToken.None);
        _capturedRedb.Should().NotBeNull("whereRedb goes to WhereRedb, not to Where");
        _captured.Should().BeNull();
        // The core's own parser must accept what the markup produced — the provider runs it next.
        var parse = () => new redb.Core.Query.Parsing.FilterExpressionParser().ParseRedbFilter(_capturedRedb!);
        parse.Should().NotThrow();
        return _capturedRedb!;
    }

    [Fact]
    public async Task WhereRedb_ValueGuidFromAHeader()
    {
        var key = Guid.NewGuid();
        var predicate = (await BasePredicateFor("ValueGuid == header.key", ("key", key.ToString()))).Compile();

        predicate(new RedbObject<OrderProps> { ValueGuid = key }).Should().BeTrue();
        predicate(new RedbObject<OrderProps> { ValueGuid = Guid.NewGuid() }).Should().BeFalse();
    }

    [Fact]
    public async Task WhereRedb_IdParentIdAndValueString_Combine()
    {
        var predicate = (await BasePredicateFor("ParentId == 7 AND ValueString == 'A-1' AND Id > 100")).Compile();

        predicate(new RedbObject<OrderProps> { ParentId = 7, ValueString = "A-1", Id = 101 }).Should().BeTrue();
        predicate(new RedbObject<OrderProps> { ParentId = 7, ValueString = "A-1", Id = 100 }).Should().BeFalse();
        predicate(new RedbObject<OrderProps> { ParentId = 8, ValueString = "A-1", Id = 101 }).Should().BeFalse();
    }

    [Fact]
    public async Task WhereRedb_NullParent_AndStartsWithOnName()
    {
        var predicate = (await BasePredicateFor("ParentId == null AND startsWith(Name, 'INC-')")).Compile();

        predicate(new RedbObject<OrderProps> { Name = "INC-1" }).Should().BeTrue();
        predicate(new RedbObject<OrderProps> { Name = "INC-1", ParentId = 3 }).Should().BeFalse();
    }

    [Fact]
    public async Task WhereRedb_DateCreate_TakesATextDate()
    {
        var predicate = (await BasePredicateFor("DateCreate >= header.since", ("since", "2026-09-01T00:00:00+00:00"))).Compile();

        predicate(new RedbObject<OrderProps> { DateCreate = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero) }).Should().BeTrue();
        predicate(new RedbObject<OrderProps> { DateCreate = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero) }).Should().BeFalse();
    }

    [Fact]
    public async Task WhereAndWhereRedb_BothReachTheQueryable()
    {
        var route = NewRoute();
        route.RedbQuery(typeof(OrderProps), where: "Status == 'open'", whereRedb: "ValueLong == 5");

        await route.Outputs[0].CreateProcessor(_context).Process(new Exchange(), CancellationToken.None);

        _captured.Should().NotBeNull();
        _capturedRedb.Should().NotBeNull();
    }

    [Fact]
    public void WhereRedb_APropsName_RefusesNamingTheBaseFields()
    {
        var act = () => NewRoute().RedbQuery(typeof(OrderProps), whereRedb: "Status == 'open'");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("whereRedb 'Status == 'open'': 'Status' is neither a base field*")
            .WithMessage("*ValueGuid*", "the base-field list helps fix it");
    }

    [Fact]
    public void WhereRedb_AComputedMemberOfIRedbObject_IsNotABaseField()
    {
        // HasParent is on IRedbObject but has no column; the core would map it to _id silently.
        var act = () => NewRoute().RedbQuery(typeof(OrderProps), whereRedb: "HasParent == true");

        act.Should().Throw<InvalidOperationException>().WithMessage("*'HasParent' is neither a base field*");
    }

    [Fact]
    public void WhereRedb_ValueBytes_IsRefused()
    {
        var act = () => NewRoute().RedbQuery(typeof(OrderProps), whereRedb: "ValueBytes == null");

        act.Should().Throw<InvalidOperationException>().WithMessage("*'ValueBytes' is neither a base field*");
    }

    [Fact]
    public void WhereRedb_ANestedPath_Refuses()
    {
        var act = () => NewRoute().RedbQuery(typeof(OrderProps), whereRedb: "Name.Length == 3");

        act.Should().Throw<InvalidOperationException>().WithMessage("*base fields are flat*");
    }

    [Fact]
    public async Task OrderByRedb_ReachesOrderByDescendingRedb_WithTheFieldType()
    {
        _query.OrderByDescendingRedb(Arg.Any<Expression<Func<redb.Core.Models.Contracts.IRedbObject, DateTimeOffset>>>()).Returns(_query);
        var route = NewRoute();
        route.RedbQuery(typeof(OrderProps), whereRedb: "ParentId == 1", orderByRedb: "DateCreate", descending: true);

        await route.Outputs[0].CreateProcessor(_context).Process(new Exchange(), CancellationToken.None);

        _query.Received(1).OrderByDescendingRedb(
            Arg.Is<Expression<Func<redb.Core.Models.Contracts.IRedbObject, DateTimeOffset>>>(e =>
                new redb.Core.Query.OrderingExpressionParser().ParseRedbOrdering(e, redb.Core.Query.QueryExpressions.SortDirection.Descending) != null));
    }

    [Fact]
    public void OrderByAndOrderByRedb_Together_Refuse()
    {
        var act = () => NewRoute().RedbQuery(typeof(OrderProps), orderBy: "Total", orderByRedb: "Id");

        act.Should().Throw<ArgumentException>().WithMessage("*orders by one key*");
    }

    [Fact]
    public void OrderByRedb_AnUnknownField_Refuses()
    {
        var act = () => NewRoute().RedbQuery(typeof(OrderProps), orderByRedb: "Total");

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a base field path of IRedbObject*");
    }

    // ── outputType: the terminal operation ───────────────────────────

    private async Task<Exchange> RunOutput(RedbQueryOutput output, string? whereRedb = "Id > 0")
    {
        var route = NewRoute();
        route.RedbQuery(typeof(OrderProps), whereRedb: whereRedb, outputType: output);
        var exchange = new Exchange();
        await route.Outputs[0].CreateProcessor(_context).Process(exchange, CancellationToken.None);
        return exchange;
    }

    [Fact]
    public async Task OutputFirst_IsFirstOrDefault_AndLandsTheObjectOrNull()
    {
        var found = new RedbObject<OrderProps>();
        _query.FirstOrDefaultAsync().Returns(found, (RedbObject<OrderProps>?)null);

        (await RunOutput(RedbQueryOutput.First)).In.Body.Should().BeSameAs(found);
        (await RunOutput(RedbQueryOutput.First)).In.Body.Should().BeNull("nothing matched is null, the markup decides");
        await _query.DidNotReceive().ToListAsync();
    }

    [Fact]
    public async Task OutputCountAndAny_AreCountAsyncAndAnyAsync()
    {
        _query.CountAsync().Returns(3);
        _query.AnyAsync().Returns(true);

        (await RunOutput(RedbQueryOutput.Count)).In.Body.Should().Be(3);
        (await RunOutput(RedbQueryOutput.Any)).In.Body.Should().Be(true);
        await _query.DidNotReceive().ToListAsync();
    }

    [Fact]
    public async Task OutputCount_WithoutACondition_IsNoUnboundedScan()
    {
        // The guard is about pulling every row into memory; a count reads one number.
        _query.CountAsync().Returns(7);

        (await RunOutput(RedbQueryOutput.Count, whereRedb: null)).In.Body.Should().Be(7);
    }

    [Fact]
    public void OutputFirst_WithTake_Refuses()
    {
        var act = () => NewRoute().RedbQuery(typeof(OrderProps), whereRedb: "Id > 0", take: 1,
            outputType: RedbQueryOutput.First);

        act.Should().Throw<ArgumentException>().WithMessage("*First reads one object*take does not apply*");
    }

    [Fact]
    public void OutputCount_WithOrdering_Refuses()
    {
        var act = () => NewRoute().RedbQuery(typeof(OrderProps), whereRedb: "Id > 0", orderByRedb: "Id",
            outputType: RedbQueryOutput.Count);

        act.Should().Throw<ArgumentException>().WithMessage("*Count answers for all the matches*");
    }
}

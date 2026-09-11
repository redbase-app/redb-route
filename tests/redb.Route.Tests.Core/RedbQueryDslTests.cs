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

    public RedbQueryDslTests()
    {
        _context.GetService<IRedbService>().Returns(_redb);
        _redb.Query<OrderProps>().Returns(_query);
        _query.Where(Arg.Do<Expression<Func<OrderProps, bool>>>(e => _captured = e)).Returns(_query);
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
}

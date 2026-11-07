using System.Linq.Expressions;
using redb.Core;
using redb.Core.Models.Contracts;
using redb.Core.Models.Entities;
using redb.Core.Query;
using redb.Core.Query.Parsing;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.RedbCore.Extensions;

namespace redb.Route.Tests.Core;

/// <summary>
/// <c>in</c> / <c>not in</c> in a <c>redbQuery</c> where-string: a search by a list of keys in one
/// query (<c>ValueString in header.codes</c>, <c>Id in body.ids</c>). The storage already turns
/// <c>Enumerable.Contains</c> over a list into an SQL <c>IN</c>, for props and for base fields alike,
/// so the translator emits exactly that — over an array typed as the member, each element converted
/// the way a single comparison converts its value.
/// <para>
/// An empty list never reaches the storage: it becomes a constant "nothing found" (or "everything"
/// for <c>not in</c>) in the translator. What the storage does with <c>{"$in": []}</c> is decided in
/// SQL we do not see from here, and "nothing found" must not depend on it.
/// </para>
/// </summary>
public sealed class RedbQueryInOperatorTests
{
    public sealed class OrderProps
    {
        public string? Status { get; set; }
        public int Total { get; set; }
    }

    private readonly IRedbService _redb = Substitute.For<IRedbService>();
    private readonly IRouteContext _context = Substitute.For<IRouteContext>();
    private readonly IOrderedRedbQueryable<OrderProps> _query = Substitute.For<IOrderedRedbQueryable<OrderProps>>();
    private Expression<Func<OrderProps, bool>>? _captured;
    private Expression<Func<IRedbObject, bool>>? _capturedRedb;

    public RedbQueryInOperatorTests()
    {
        _context.GetService<IRedbService>().Returns(_redb);
        _redb.Query<OrderProps>().Returns(_query);
        _query.Where(Arg.Do<Expression<Func<OrderProps, bool>>>(e => _captured = e)).Returns(_query);
        _query.WhereRedb(Arg.Do<Expression<Func<IRedbObject, bool>>>(e => _capturedRedb = e)).Returns(_query);
        _query.ToListAsync().Returns([]);
    }

    private async Task Run(Action<RouteDefinition> configure, params (string Key, object? Value)[] headers)
    {
        var route = new RouteDefinition { _context = _context };
        configure(route);
        var exchange = new Exchange();
        foreach (var (key, value) in headers)
            exchange.In.Headers[key] = value;
        await route.Outputs[0].CreateProcessor(_context).Process(exchange, CancellationToken.None);
    }

    private async Task<Func<OrderProps, bool>> Props(string where, params (string Key, object? Value)[] headers)
    {
        await Run(r => r.RedbQuery(typeof(OrderProps), where: where), headers);
        _captured.Should().NotBeNull();
        // The storage's own parser must accept what we produced — the provider runs it next.
        var parse = () => new FilterExpressionParser().ParseFilter<OrderProps>(_captured!);
        parse.Should().NotThrow();
        return _captured!.Compile();
    }

    private async Task<Func<IRedbObject, bool>> Base(string whereRedb, params (string Key, object? Value)[] headers)
    {
        await Run(r => r.RedbQuery(typeof(OrderProps), whereRedb: whereRedb), headers);
        _capturedRedb.Should().NotBeNull();
        var parse = () => new FilterExpressionParser().ParseRedbFilter(_capturedRedb!);
        parse.Should().NotThrow();
        return _capturedRedb!.Compile();
    }

    [Fact]
    public async Task A_props_member_in_a_collection_from_a_header()
    {
        var predicate = await Props("Status in header.statuses", ("statuses", new List<string> { "open", "held" }));

        predicate(new OrderProps { Status = "open" }).Should().BeTrue();
        predicate(new OrderProps { Status = "held" }).Should().BeTrue();
        predicate(new OrderProps { Status = "closed" }).Should().BeFalse();
    }

    [Fact]
    public async Task Not_in_negates()
    {
        var predicate = await Props("Status not in ('closed','void')");

        predicate(new OrderProps { Status = "open" }).Should().BeTrue();
        predicate(new OrderProps { Status = "void" }).Should().BeFalse();
    }

    [Fact]
    public async Task Elements_are_converted_to_the_members_type()
    {
        // Strings from a header, an int member: each element is converted as a single comparison
        // converts its value, so the storage gets an int[] and not a list of strings it cannot match.
        var predicate = await Props("Total in header.totals", ("totals", new List<string> { "10", "20" }));

        predicate(new OrderProps { Total = 20 }).Should().BeTrue();
        predicate(new OrderProps { Total = 30 }).Should().BeFalse();
    }

    [Fact]
    public async Task A_base_field_in_a_list_of_ids()
    {
        var predicate = await Base("Id in header.ids", ("ids", new List<long> { 7, 9 }));

        predicate(new RedbObject<OrderProps> { Id = 9 }).Should().BeTrue();
        predicate(new RedbObject<OrderProps> { Id = 8 }).Should().BeFalse();
    }

    [Fact]
    public async Task A_base_string_field_in_a_literal_list()
    {
        var predicate = await Base("ValueString in ('A-1','A-2')");

        predicate(new RedbObject<OrderProps> { ValueString = "A-2" }).Should().BeTrue();
        predicate(new RedbObject<OrderProps> { ValueString = "B-1" }).Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_list_finds_nothing_and_never_reaches_the_storage_as_an_empty_in()
    {
        var predicate = await Props("Status in header.statuses", ("statuses", new List<string>()));

        predicate(new OrderProps { Status = "open" }).Should().BeFalse();
        _captured!.ToString().Should().NotContain("Contains", "an empty IN is decided here, not in SQL we do not see");
    }

    [Fact]
    public async Task An_empty_list_under_not_in_keeps_everything()
    {
        var predicate = await Props("Status not in header.statuses", ("statuses", new List<string>()));

        predicate(new OrderProps { Status = "open" }).Should().BeTrue();
    }

    [Fact]
    public async Task A_member_on_the_right_of_in_is_refused()
    {
        var act = () => Props("'open' in Status");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("in");
    }

    [Fact]
    public async Task A_header_only_membership_is_a_per_message_gate()
    {
        // No member on either side: decided once per message, like any other header condition
        // inside a where-string.
        var open = await Props("header.tier in ('gold') AND Status == 'open'", ("tier", "gold"));
        open(new OrderProps { Status = "open" }).Should().BeTrue();

        _captured = null;
        var shut = await Props("header.tier in ('gold') AND Status == 'open'", ("tier", "bronze"));
        shut(new OrderProps { Status = "open" }).Should().BeFalse();
    }
}

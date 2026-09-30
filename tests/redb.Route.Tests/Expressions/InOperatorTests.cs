using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// <c>in</c> and <c>not in</c>: membership of a value in a list, as Camel Simple spells it
/// (<c>${header.type} in 'gold,silver'</c>). Asked for 2026-09-25 so markup can search by a list of
/// keys in one step. The right-hand side is either a collection the message carries
/// (<c>header.codes</c>) or a list literal in parentheses — <c>('gold','silver')</c>; Camel's comma
/// string is not adopted, because a comma inside a string already means something else in
/// <c>include</c> and <c>antInclude</c>.
/// <para>
/// Elements are compared with the same equality <c>==</c> uses, so <c>in</c> can never disagree with
/// <c>==</c> about whether <c>2</c> equals <c>2L</c> or <c>"2"</c>.
/// </para>
/// </summary>
[Collection("ExpressionResolver")]
public class InOperatorTests : IDisposable
{
    public InOperatorTests() => ExpressionResolver.ClearAllCaches();

    public void Dispose()
    {
        ExpressionResolver.ClearAllCaches();
        GC.SuppressFinalize(this);
    }

    private static IExchange With(params (string Key, object? Value)[] headers)
    {
        var exchange = new Exchange(new Message("body"));
        foreach (var (key, value) in headers)
            exchange.In.Headers[key] = value;
        return exchange;
    }

    private static bool Holds(string condition, IExchange exchange)
        => PredicateFactory.FromString(condition).Matches(exchange);

    [Theory]
    [InlineData("b", true)]
    [InlineData("z", false)]
    public void In_tests_membership_in_a_collection_the_message_carries(string code, bool expected)
    {
        var exchange = With(("code", code), ("codes", new List<string> { "a", "b", "c" }));

        Holds("header.code in header.codes", exchange).Should().Be(expected);
        Holds("header.code not in header.codes", exchange).Should().Be(!expected);
    }

    [Fact]
    public void A_list_literal_is_written_in_parentheses()
    {
        var exchange = With(("tier", "silver"));

        Holds("header.tier in ('gold','silver')", exchange).Should().BeTrue();
        Holds("header.tier in ('gold','platinum')", exchange).Should().BeFalse();
        Holds("header.tier not in ('gold','platinum')", exchange).Should().BeTrue();
    }

    [Fact]
    public void Numbers_compare_the_way_equality_compares_them()
    {
        // An int in the header, a long in the list: == treats them as equal, so in must too.
        var exchange = With(("n", 2), ("ids", new List<long> { 1L, 2L, 3L }));

        Holds("header.n in header.ids", exchange).Should().BeTrue();
        Holds("header.n in (1, 2, 3)", exchange).Should().BeTrue();
        Holds("header.n in (4, 5)", exchange).Should().BeFalse();
    }

    [Fact]
    public void The_operator_is_case_insensitive_and_needs_no_space_before_the_parenthesis()
    {
        var exchange = With(("tier", "gold"));

        Holds("header.tier IN ('gold')", exchange).Should().BeTrue();
        Holds("header.tier NOT IN ('gold')", exchange).Should().BeFalse();
        Holds("header.tier in('gold')", exchange).Should().BeTrue("in( must not be read as a call to a function named in");
    }

    [Fact]
    public void An_empty_list_finds_nothing()
    {
        var exchange = With(("code", "a"), ("codes", new List<string>()));

        Holds("header.code in header.codes", exchange).Should().BeFalse();
        Holds("header.code not in header.codes", exchange).Should().BeTrue();
    }

    [Fact]
    public void A_missing_collection_is_an_empty_one()
    {
        // No such header: nothing to be a member of — false, not an exception and not "everything".
        var exchange = With(("code", "a"));

        Holds("header.code in header.codes", exchange).Should().BeFalse();
        Holds("header.code not in header.codes", exchange).Should().BeTrue();
    }

    [Fact]
    public void A_string_on_the_right_is_refused_rather_than_split()
    {
        // Camel reads 'a,b' as a list; we do not, and a string must not quietly become a list of
        // characters either. The author is told which two forms exist.
        var exchange = With(("code", "a"), ("codes", "a,b"));

        var act = () => Holds("header.code in header.codes", exchange);

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("('a','b')");
    }

    [Fact]
    public void In_combines_with_the_other_operators()
    {
        var exchange = With(("tier", "gold"), ("n", 5));

        Holds("header.tier in ('gold','silver') AND header.n > 3", exchange).Should().BeTrue();
        Holds("header.tier in ('bronze') OR header.n > 3", exchange).Should().BeTrue();
        Holds("header.tier in ('bronze') AND header.n > 3", exchange).Should().BeFalse();
    }

    [Fact]
    public void A_header_named_in_is_still_a_header()
    {
        // The detector that routes expressions to the parser must not mistake a path segment for
        // the operator: header.in is a header called "in", exactly as before.
        var exchange = With(("in", "value"));

        new StringExpression("header.in").Evaluate<object>(exchange).Should().Be("value");
    }

    [Fact]
    public async Task In_works_in_a_route_filter()
    {
        var passed = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://in-filter")
            .Filter("header.tier in ('gold','silver')")
            .Process(e => passed.Add((string)e.In.Headers["tier"]!)));
        await context.Start();

        var producer = context.GetEndpoint("direct://in-filter").CreateProducer();
        await producer.Start();
        foreach (var tier in new[] { "gold", "bronze", "silver" })
        {
            var ex = new Exchange(new Message("x"));
            ex.In.Headers["tier"] = tier;
            await producer.Process(ex);
        }

        passed.Should().Equal("gold", "silver");
    }
}

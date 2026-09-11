using FluentAssertions;
using redb.Route.Core;
using redb.Route.Expressions;
using XPathExpr = redb.Route.Expressions.XPathExpression;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// XPath in a condition position asks whether the path matched, not what the matched node says.
/// <para>
/// This is XPath 1.0, not a house rule: a node-set is true when it is non-empty, and the node's
/// content never enters the question. The distinction is invisible until the content happens to
/// look falsy — <c>&lt;discount&gt;0&lt;/discount&gt;</c>, an empty <c>&lt;vip/&gt;</c>, a
/// <c>&lt;flag&gt;false&lt;/flag&gt;</c> — and then a matched node reads as "no match".
/// </para>
/// <para>
/// The give-away that the old reading was a defect rather than a choice is that it disagreed with
/// itself: one <c>&lt;discount&gt;0&lt;/discount&gt;</c> was false while two of them were true,
/// because a multi-node result never got flattened to a scalar.
/// </para>
/// <para>
/// Asking for a CLR type is a different question and keeps its own answer:
/// <c>XPath&lt;bool&gt;("/order/flag")</c> reads the node's text, so it is false. So does an XPath
/// expression that already produces a scalar — <c>string(...)</c>, <c>count(...) &gt; 0</c> — which
/// is read by the one DSL truthiness rule like any other value.
/// </para>
/// </summary>
public class XPathPredicateSemanticsTests
{
    private const string Order = """
        <order>
            <discount>0</discount>
            <vip/>
            <flag>false</flag>
        </order>
        """;

    private const string TwoDiscounts = """
        <order>
            <discount>0</discount>
            <discount>0</discount>
        </order>
        """;

    // ── Harness ──
    //
    // One context per run: a route added after Start() is not started, so sharing a context
    // between two runs inside one test silently answers "did not pass" for the second one.

    private static async Task<bool> PassesFilter(string id, redb.Route.Abstractions.IExpression expression, string xml)
    {
        await using var context = new RouteContext();
        var passed = false;

        context.AddRoutes(r => r.From($"direct://xp-{id}")
            .Filter(expression)
                .Process(_ => passed = true)
            .EndFilter());

        await Send(context, $"xp-{id}", xml);
        return passed;
    }

    private static async Task<bool> MatchesWhen(string id, redb.Route.Abstractions.IExpression expression, string xml)
    {
        await using var context = new RouteContext();
        var matched = false;

        context.AddRoutes(r => r.From($"direct://xp-{id}")
            .Choice()
                .When(expression)
                    .Process(_ => matched = true)
            .EndChoice());

        await Send(context, $"xp-{id}", xml);
        return matched;
    }

    private static async Task Send(RouteContext context, string id, string xml)
    {
        await context.Start();
        var producer = context.GetEndpoint($"direct://{id}").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message(xml)));
    }

    // ── The spelling the semantics exist for ──

    [Fact]
    public async Task An_expression_goes_straight_into_a_condition_verb()
    {
        // A compile-time guard, not a runtime one. Condition verbs are overloaded on IExpression
        // and IPredicate both, so an expression that implemented IPredicate would make this call
        // ambiguous and cost the language the very spelling the semantics were added for. The rest
        // of this class passes expressions through an IExpression-typed parameter, which would have
        // hidden that.
        var passed = false;

        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct://xp-direct")
            .Filter(new XPathExpr("/order/discount"))
                .Process(_ => passed = true)
            .EndFilter());

        await context.Start();
        var producer = context.GetEndpoint("direct://xp-direct").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message(Order)));

        passed.Should().BeTrue();
    }

    // ── The node-set question ──

    [Fact]
    public async Task A_path_that_matches_nothing_is_false()
    {
        var passed = await PassesFilter("miss", new XPathExpr("/order/missing"), Order);
        passed.Should().BeFalse();
    }

    [Fact]
    public async Task A_matched_node_holding_zero_is_still_a_match()
    {
        var passed = await PassesFilter("zero", new XPathExpr("/order/discount"), Order);
        passed.Should().BeTrue("the element exists; XPath 1.0 reads a non-empty node-set as true");
    }

    [Fact]
    public async Task A_matched_empty_element_is_still_a_match()
    {
        var passed = await PassesFilter("vip", new XPathExpr("/order/vip"), Order);
        passed.Should().BeTrue("an element with no content is an element that matched");
    }

    [Fact]
    public async Task A_matched_node_holding_the_word_false_is_still_a_match()
    {
        var passed = await PassesFilter("flag", new XPathExpr("/order/flag"), Order);
        passed.Should().BeTrue("the node's text is not the question a bare path asks");
    }

    [Fact]
    public async Task Two_matched_nodes_are_a_match()
    {
        var passed = await PassesFilter("two", new XPathExpr("/order/discount"), TwoDiscounts);
        passed.Should().BeTrue();
    }

    [Fact]
    public async Task One_match_and_two_matches_answer_the_same_way()
    {
        var one = await PassesFilter("cons1", new XPathExpr("/order/discount"), Order);
        var two = await PassesFilter("cons2", new XPathExpr("/order/discount"), TwoDiscounts);

        one.Should().Be(two, "the same path over the same element name cannot flip on how many matched");
    }

    [Fact]
    public async Task When_reads_a_path_the_same_way_Filter_does()
    {
        var matched = await MatchesWhen("when", new XPathExpr("/order/discount"), Order);
        matched.Should().BeTrue();
    }

    // ── The other two questions, unchanged ──

    [Fact]
    public async Task Asking_for_a_CLR_bool_still_reads_the_node_text()
    {
        var passed = await PassesFilter("typed", new TypedXPathExpression<bool>("/order/flag"), Order);
        passed.Should().BeFalse("XPath<bool> asks to convert the node's text, and the text is 'false'");
    }

    [Fact]
    public async Task An_XPath_boolean_function_keeps_XPath_semantics()
    {
        var present = await PassesFilter("fn1", new XPathExpr("boolean(/order/discount)"), Order);
        var absent = await PassesFilter("fn2", new XPathExpr("boolean(/order/missing)"), Order);

        present.Should().BeTrue();
        absent.Should().BeFalse();
    }

    [Fact]
    public async Task An_XPath_scalar_result_is_read_by_the_one_DSL_truthiness_rule()
    {
        // string() produces a scalar, so the node-set question never arises and the DSL rule
        // applies exactly as it would to a header holding "0".
        var zeroText = await PassesFilter("str", new XPathExpr("string(/order/discount)"), Order);
        var counted = await PassesFilter("cnt", new XPathExpr("count(/order/discount) > 0"), Order);

        zeroText.Should().BeFalse();
        counted.Should().BeTrue();
    }
}

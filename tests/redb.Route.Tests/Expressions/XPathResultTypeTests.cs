using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using XPathExpr = redb.Route.Expressions.XPathExpression;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// The XPath-level result type — Camel's <c>resultQName</c> — as distinct from the CLR type the
/// caller converts to. String, number and boolean are XPath's own coercions, so the engine
/// performs them; node-set and node differ only in how many matches survive.
/// </summary>
public class XPathResultTypeTests
{
    private const string Order = """
        <order>
            <discount>0</discount>
            <qty>7</qty>
            <tags>
                <tag>a</tag>
                <tag>b</tag>
                <tag>c</tag>
            </tags>
        </order>
        """;

    private static IExchange Exchange() => new Exchange(new Message(Order));

    // ── String ──

    [Fact]
    public void String_keeps_the_text_instead_of_smart_parsing_it()
    {
        var asNodeSet = new XPathExpr("/order/discount").Evaluate<object>(Exchange());
        var asString = new XPathExpr("/order/discount", XPathResult.String).Evaluate<object>(Exchange());

        asNodeSet.Should().Be(0, "a node-set result is smart-parsed, and '0' parses as a number");
        asString.Should().Be("0", "string() asked for text, so text is what comes back");
    }

    [Fact]
    public void String_of_a_path_that_matched_nothing_is_empty()
    {
        var value = new XPathExpr("/order/missing", XPathResult.String).Evaluate<string>(Exchange());
        value.Should().BeEmpty();
    }

    // ── Number ──

    [Fact]
    public void Number_converts_the_text_the_way_XPath_does()
    {
        var value = new XPathExpr("/order/qty", XPathResult.Number).Evaluate<double>(Exchange());
        value.Should().Be(7d);
    }

    [Fact]
    public void Number_of_a_path_that_matched_nothing_is_NaN()
    {
        var value = new XPathExpr("/order/missing", XPathResult.Number).Evaluate<double>(Exchange());
        double.IsNaN(value).Should().BeTrue();
    }

    // ── Boolean ──

    [Fact]
    public void Boolean_is_existence_for_a_path()
    {
        var present = new XPathExpr("/order/discount", XPathResult.Boolean).Evaluate<bool>(Exchange());
        var absent = new XPathExpr("/order/missing", XPathResult.Boolean).Evaluate<bool>(Exchange());

        present.Should().BeTrue("boolean() of a non-empty node-set is true whatever the node holds");
        absent.Should().BeFalse();
    }

    // ── Node against NodeSet ──

    [Fact]
    public void Node_keeps_only_the_first_match_while_NodeSet_keeps_all()
    {
        var nodeSet = new XPathExpr("/order/tags/tag").Evaluate<string>(Exchange());
        var node = new XPathExpr("/order/tags/tag", XPathResult.Node).Evaluate<string>(Exchange());

        nodeSet.Should().Be("a, b, c");
        node.Should().Be("a");
    }

    [Fact]
    public void Node_of_a_path_that_matched_nothing_is_absent()
    {
        var value = new XPathExpr("/order/missing", XPathResult.Node).Evaluate<string>(Exchange());
        value.Should().BeNull();
    }

    // ── Composition with the CLR type ──

    [Fact]
    public void The_XPath_type_and_the_CLR_type_compose()
    {
        var value = new TypedXPathExpression<int>("/order/qty", XPathResult.Number).Evaluate<object>(Exchange());
        value.Should().Be(7);
    }

    // ── Template serialization ──

    [Fact]
    public void A_default_expression_still_serializes_to_a_template()
    {
        new XPathExpr("/order/qty").ToTemplateString().Should().Be("${xpath(/order/qty)}");
    }

    [Fact]
    public void A_result_type_that_the_template_cannot_carry_is_refused_rather_than_dropped()
    {
        var act = () => new XPathExpr("/order/qty", XPathResult.String).ToTemplateString();

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*only a path*");
    }
}

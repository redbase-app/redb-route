using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using XPathExpr = redb.Route.Expressions.XPathExpression;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// The remaining option-level gaps against Camel's XPath language: reaching namespaces from the
/// DSL, and trimming extracted text.
/// </summary>
public class XPathParityOptionsTests
{
    private const string Namespaced =
        """
        <env:Envelope xmlns:env="http://schemas.xmlsoap.org/soap/envelope/">
          <env:Body><order xmlns="urn:acme:orders"><id>A-1</id></order></env:Body>
        </env:Envelope>
        """;

    private const string Padded =
        """
        <order>
            <name> John </name>
            <tags>
                <tag> a </tag>
                <tag> b </tag>
            </tags>
        </order>
        """;

    private static IExchange Exchange(string xml) => new Exchange(new Message(xml));

    // ── Namespaces ──

    [Fact]
    public void A_namespaced_document_is_queryable_from_the_DSL()
    {
        var value = new XPathExpr("/e:Envelope/e:Body/o:order/o:id")
            .WithNamespaces(
                ("e", "http://schemas.xmlsoap.org/soap/envelope/"),
                ("o", "urn:acme:orders"))
            .Evaluate<string>(Exchange(Namespaced));

        value.Should().Be("A-1");
    }

    [Fact]
    public void The_prefixes_are_the_expressions_own_and_need_not_match_the_document()
    {
        // The document says env:; the expression says whatever it likes, as long as the URI matches.
        var value = new XPathExpr("/soap:Envelope/soap:Body/ord:order/ord:id")
            .WithNamespaces(
                ("soap", "http://schemas.xmlsoap.org/soap/envelope/"),
                ("ord", "urn:acme:orders"))
            .Evaluate<string>(Exchange(Namespaced));

        value.Should().Be("A-1");
    }

    [Fact]
    public void WithNamespaces_returns_a_new_expression()
    {
        var bare = new XPathExpr("/e:Envelope");
        var bound = bare.WithNamespaces(("e", "http://schemas.xmlsoap.org/soap/envelope/"));

        bound.Should().NotBeSameAs(bare);
        ((IPredicateExpression)bound).Matches(Exchange(Namespaced)).Should().BeTrue();

        // Without a binding the prefix is not merely unmatched — the path will not run at all,
        // which is why a namespaced document was unreachable from the DSL rather than just awkward.
        var act = () => bare.Evaluate<string>(Exchange(Namespaced));
        act.Should().Throw<InvalidOperationException>().WithMessage("*Namespace Manager*");
    }

    [Fact]
    public void The_namespace_binding_survives_the_other_options()
    {
        var value = new XPathExpr("/soap:Envelope/soap:Body/ord:order/ord:id", XPathResult.String)
            .WithNamespaces(
                ("soap", "http://schemas.xmlsoap.org/soap/envelope/"),
                ("ord", "urn:acme:orders"))
            .From(new HeaderExpression("doc"))
            .Evaluate<object>(WithHeader());

        value.Should().Be("A-1");

        static IExchange WithHeader()
        {
            var exchange = new Exchange(new Message("<other/>"));
            exchange.In.Headers["doc"] = Namespaced;
            return exchange;
        }
    }

    // ── Trim ──

    [Fact]
    public void Text_keeps_its_whitespace_unless_asked_otherwise()
    {
        new XPathExpr("/order/name").Evaluate<string>(Exchange(Padded)).Should().Be(" John ");
    }

    [Fact]
    public void Trimmed_removes_the_surrounding_whitespace()
    {
        new XPathExpr("/order/name").Trimmed().Evaluate<string>(Exchange(Padded)).Should().Be("John");
    }

    [Fact]
    public void Trimmed_reaches_every_value_of_a_node_set()
    {
        // This is the case XPath cannot answer for itself: normalize-space() takes the first node
        // of a node-set, so several padded values have no in-language fix.
        new XPathExpr("/order/tags/tag").Trimmed().Evaluate<string[]>(Exchange(Padded))
            .Should().Equal("a", "b");
    }

    [Fact]
    public void Trimmed_applies_to_a_string_result_too()
    {
        new XPathExpr("/order/name", XPathResult.String).Trimmed()
            .Evaluate<string>(Exchange(Padded)).Should().Be("John");
    }

    [Fact]
    public void Trimmed_can_be_turned_back_off()
    {
        new XPathExpr("/order/name").Trimmed().Trimmed(false)
            .Evaluate<string>(Exchange(Padded)).Should().Be(" John ");
    }

    // ── A per-message path is now an expression ──

    [Fact]
    public async Task A_path_computed_per_message_can_be_used_where_an_expression_is_taken()
    {
        var seen = new List<object?>();

        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct://dyn-xpath")
            .SetHeader("picked", new CompiledXPathExpression(e => e.In.GetHeader<string>("selector")!))
            .Process(e => seen.Add(e.In.Headers["picked"])));

        await context.Start();
        var producer = context.GetEndpoint("direct://dyn-xpath").CreateProducer();
        await producer.Start();

        var first = new Exchange(new Message("<order><a>1</a><b>2</b></order>"));
        first.In.Headers["selector"] = "/order/a";
        await producer.Process(first);

        var second = new Exchange(new Message("<order><a>1</a><b>2</b></order>"));
        second.In.Headers["selector"] = "/order/b";
        await producer.Process(second);

        seen.Should().Equal(1, 2);
    }
}

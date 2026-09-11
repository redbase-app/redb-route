using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.XPath2;
using static redb.Route.XPath2.XPath2Dsl;

// Deliberately outside the redb.Route.* chain. Inside it, the simple name XPath2 resolves to the
// namespace redb.Route.XPath2 and shadows the statically imported XPath2(...) factory — the same
// trap a user hits only if they write routes in a namespace under redb.Route, where the answer is
// to spell it XPath2Dsl.XPath2(...). Documented in the package README.
namespace XPath2PackageTests;

/// <summary>
/// XPath 2.0 as an expression: what it adds over 1.0, and where its boundary actually runs.
/// </summary>
public class XPath2ExpressionTests
{
    private const string Orders =
        """
        <orders>
          <order id="A-1"><total>10</total><ref>INV-2024-7</ref></order>
          <order id="B-2"><total>25</total><ref>INV-2025-3</ref></order>
          <order id="C-3"><total>7</total><ref>draft</ref></order>
        </orders>
        """;

    private static IExchange Exchange(string body = Orders, params (string Key, object? Value)[] headers)
    {
        var exchange = new Exchange(new Message(body));
        foreach (var (key, value) in headers)
            exchange.In.Headers[key] = value;
        return exchange;
    }

    // ── What 1.0 could not do ──

    [Fact]
    public void Regular_expressions()
    {
        XPath2("matches(/orders/order[1]/ref, 'INV-\\d{4}-\\d+')").Evaluate<bool>(Exchange()).Should().BeTrue();
        XPath2("replace(/orders/order[1]/ref, '[0-9]', '#')").Evaluate<string>(Exchange()).Should().Be("INV-####-#");
        XPath2("tokenize('a,b,c', ',')").Evaluate<string[]>(Exchange()).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void Sequences_and_their_functions()
    {
        XPath2("distinct-values((1, 2, 2, 3))").Evaluate<string[]>(Exchange()).Should().Equal("1", "2", "3");
        XPath2("reverse((1, 2, 3))").Evaluate<string[]>(Exchange()).Should().Equal("3", "2", "1");
        XPath2("string-join(/orders/order/@id, '|')").Evaluate<string>(Exchange()).Should().Be("A-1|B-2|C-3");
        XPath2("empty(/orders/missing)").Evaluate<bool>(Exchange()).Should().BeTrue();
    }

    [Fact]
    public void Aggregates_beyond_sum()
    {
        XPath2("avg(/orders/order/total)").Evaluate<double>(Exchange()).Should().Be(14d);
        XPath2("max(/orders/order/total)").Evaluate<double>(Exchange()).Should().Be(25d);
    }

    [Fact]
    public void A_conditional_inside_the_expression()
    {
        XPath2("if (count(/orders/order) > 2) then 'many' else 'few'")
            .Evaluate<string>(Exchange()).Should().Be("many");
    }

    [Fact]
    public void For_return_over_nodes()
    {
        XPath2("for $o in /orders/order[number(total) > 8] return string($o/@id)")
            .Evaluate<string[]>(Exchange()).Should().Equal("A-1", "B-2");
    }

    [Fact]
    public void Quantified_expressions()
    {
        XPath2("some $o in /orders/order satisfies number($o/total) > 20").Evaluate<bool>(Exchange()).Should().BeTrue();
        XPath2("every $o in /orders/order satisfies number($o/total) > 20").Evaluate<bool>(Exchange()).Should().BeFalse();
    }

    [Fact]
    public void Typed_comparison_and_dates()
    {
        XPath2("xs:date('2020-01-01') < xs:date('2021-01-01')").Evaluate<bool>(Exchange()).Should().BeTrue();
        XPath2("/orders/order[number(total) gt 8]/@id").Evaluate<string[]>(Exchange()).Should().Equal("A-1", "B-2");
    }

    // ── The boundary, stated as a test rather than as a hope ──

    [Fact]
    public void Let_where_and_order_by_are_XQuery_and_are_refused()
    {
        // These are FLWOR clauses of XQuery, not of XPath 2.0. The engine rejects them at compile
        // time, which is the right moment — a route carrying one fails when it is built.
        foreach (var xquery in new[]
                 {
                     "let $x := 5 return $x * 2",
                     "for $o in /orders/order where number($o/total) > 8 return $o",
                     "for $o in /orders/order order by number($o/total) return $o",
                 })
        {
            var act = () => XPath2(xquery);
            act.Should().Throw<ArgumentException>()
                .WithMessage("*Invalid XPath 2.0 expression*", $"'{xquery}' is XQuery, not XPath 2.0");
        }
    }

    [Fact]
    public void A_malformed_expression_fails_when_the_route_is_built()
    {
        var act = () => XPath2("for $x in");
        act.Should().Throw<ArgumentException>().WithMessage("*Invalid XPath 2.0 expression*");
    }

    // ── The same surface as the 1.0 expression ──

    [Fact]
    public void A_condition_asks_whether_anything_matched()
    {
        ((IPredicateExpression)XPath2("/orders/order[@id='B-2']")).Matches(Exchange()).Should().BeTrue();
        ((IPredicateExpression)XPath2("/orders/order[@id='Z-9']")).Matches(Exchange()).Should().BeFalse();
    }

    [Fact]
    public void A_matched_node_holding_a_falsy_value_is_still_a_match()
    {
        var exchange = Exchange("<order><discount>0</discount></order>");

        ((IPredicateExpression)XPath2("/order/discount")).Matches(exchange).Should().BeTrue(
            "the same rule the XPath 1.0 expression follows: a match is a match whatever it holds");
    }

    [Fact]
    public void It_can_read_a_source_other_than_the_body()
    {
        var exchange = Exchange("<other/>", ("doc", Orders));

        XPath2("string-join(/orders/order/@id, '|')").From(new HeaderExpression("doc"))
            .Evaluate<string>(exchange).Should().Be("A-1|B-2|C-3");
    }

    [Fact]
    public void It_takes_bound_parameters()
    {
        var exchange = Exchange(Orders, ("wanted", "B-2"));

        XPath2("/orders/order[@id=$id]/total")
            .WithParameters(("id", new HeaderExpression("wanted")))
            .Evaluate<string>(exchange).Should().Be("25");
    }

    [Fact]
    public void A_bound_value_carrying_XPath_syntax_stays_a_value()
    {
        var exchange = Exchange(Orders, ("wanted", "B-2' or '1'='1"));

        XPath2("/orders/order[@id=$id]/total")
            .WithParameters(("id", new HeaderExpression("wanted")))
            .Evaluate<string>(exchange).Should().BeNull();
    }

    [Fact]
    public void It_binds_namespaces()
    {
        var exchange = Exchange("<o:orders xmlns:o='urn:acme'><o:order id='X'/></o:orders>");

        XPath2("string(/a:orders/a:order/@id)", ("a", "urn:acme"))
            .Evaluate<string>(exchange).Should().Be("X");
    }

    [Fact]
    public void It_trims_when_asked()
    {
        var exchange = Exchange("<order><name> John </name></order>");

        XPath2("/order/name").Evaluate<string>(exchange).Should().Be(" John ");
        XPath2("/order/name").Trimmed().Evaluate<string>(exchange).Should().Be("John");
    }

    [Fact]
    public void The_typed_form_converts_the_result()
    {
        XPath2<int>("count(/orders/order)").Evaluate<object>(Exchange()).Should().Be(3);
    }

    [Fact]
    public void There_is_no_template_form_and_it_says_so()
    {
        var act = () => XPath2("count(/orders/order)").ToTemplateString();

        act.Should().Throw<NotSupportedException>().WithMessage("*no xpath2() function*");
    }

    // ── Concurrency ──

    [Fact]
    public void One_expression_is_safe_to_evaluate_concurrently()
    {
        // A route evaluates the same expression on many in-flight messages at once, and the
        // underlying engine's compiled form mutates shared slots when the expression binds range
        // variables (for / some / every) — 400k parallel evaluations of a bare shared instance
        // corrupt deterministically (IndexOutOfRangeException), and its Clone() shares the same
        // slots. So this is a semantics test for our wrapper: same expression object, eight
        // threads, every answer must belong to its own message.
        var expression = XPath2("string-join(for $x in /orders/order[number(total) > $min] return string($x/@id), ',')")
            .WithParameters(("min", new HeaderExpression("min")));

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var answers = new Dictionary<int, string>
        {
            [0] = "A-1,B-2,C-3",
            [8] = "A-1,B-2",
            [20] = "B-2",
        };

        Parallel.ForEach(
            Enumerable.Range(0, 60_000),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i =>
            {
                var min = (i % 3) switch { 0 => 0, 1 => 8, _ => 20 };
                try
                {
                    var result = expression.Evaluate<string>(Exchange(Orders, ("min", min)));
                    if (result != answers[min] && failures.Count < 5)
                        failures.Add($"min={min}: '{result}'");
                }
                catch (Exception ex)
                {
                    if (failures.Count < 5)
                        failures.Add($"min={min}: {ex.GetType().Name}");
                }
            });

        failures.Should().BeEmpty();
    }

    // ── In a route ──

    [Fact]
    public async Task It_works_wherever_a_route_takes_an_expression()
    {
        var seen = new List<object?>();

        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct://xp2-route")
            .Filter(XPath2("some $o in /orders/order satisfies number($o/total) > 20"))
                .SetHeader("ids", XPath2("string-join(/orders/order[number(total) > 8]/@id, ',')"))
                .Process(e => seen.Add(e.In.Headers["ids"]))
            .EndFilter());

        await context.Start();
        var producer = context.GetEndpoint("direct://xp2-route").CreateProducer();
        await producer.Start();

        await producer.Process(Exchange());
        await producer.Process(Exchange("<orders><order id='S-1'><total>1</total></order></orders>"));

        seen.Should().Equal("A-1,B-2");
    }
}

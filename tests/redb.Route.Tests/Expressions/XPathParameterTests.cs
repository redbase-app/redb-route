using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using XPathExpr = redb.Route.Expressions.XPathExpression;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// A value from the message takes part in an XPath query by being <em>bound</em> to a
/// <c>$name</c> variable, never by being pasted into the query text.
/// <para>
/// Concatenation is what Apache Camel offers as <c>allowSimple</c>, and it is the reason XPath
/// injection has an OWASP entry and a CodeQL query of its own. Binding is the settled answer
/// everywhere else: JAXP's <c>XPathVariableResolver</c>, XQuery's <c>declare variable $x
/// external</c>, .NET's <c>XsltArgumentList</c>, SQL prepared statements.
/// </para>
/// </summary>
public class XPathParameterTests
{
    private const string Orders =
        """
        <orders>
          <order id="A-1"><total>10</total></order>
          <order id="B-2"><total>20</total></order>
        </orders>
        """;

    private static IExchange Exchange(params (string Key, object? Value)[] headers)
    {
        var exchange = new Exchange(new Message(Orders));
        foreach (var (key, value) in headers)
            exchange.In.Headers[key] = value;
        return exchange;
    }

    [Fact]
    public void A_bound_value_selects_the_matching_node()
    {
        var total = new XPathExpr("/orders/order[@id=$id]/total")
            .WithParameters(("id", new HeaderExpression("wanted")))
            .Evaluate<string>(Exchange(("wanted", "B-2")));

        total.Should().Be("20");
    }

    [Fact]
    public void The_same_expression_answers_differently_for_different_messages()
    {
        var expression = new XPathExpr("/orders/order[@id=$id]/total")
            .WithParameters(("id", new HeaderExpression("wanted")));

        expression.Evaluate<string>(Exchange(("wanted", "A-1"))).Should().Be("10");
        expression.Evaluate<string>(Exchange(("wanted", "B-2"))).Should().Be("20");
    }

    [Fact]
    public void A_number_is_bound_as_a_number()
    {
        var id = new XPathExpr("/orders/order[total > $min]/@id")
            .WithParameters(("min", new HeaderExpression("min")))
            .Evaluate<string>(Exchange(("min", 15)));

        id.Should().Be("B-2", "an int must reach XPath as a number, not as the text '15'");
    }

    [Fact]
    public void A_decimal_is_bound_invariantly()
    {
        // A comma decimal separator would make the same route match different documents on
        // different machines, which is the class of bug the template engine already ruled out.
        var id = new XPathExpr("/orders/order[total > $min]/@id")
            .WithParameters(("min", new HeaderExpression("min")))
            .Evaluate<string>(Exchange(("min", 15.5m)));

        id.Should().Be("B-2");
    }

    [Fact]
    public void Several_parameters_bind_together()
    {
        var id = new XPathExpr("/orders/order[total > $min and total < $max]/@id")
            .WithParameters(("min", new HeaderExpression("min")), ("max", new HeaderExpression("max")))
            .Evaluate<string>(Exchange(("min", 5), ("max", 15)));

        id.Should().Be("A-1");
    }

    [Fact]
    public void Bindings_added_in_two_calls_are_both_kept()
    {
        var id = new XPathExpr("/orders/order[total > $min and total < $max]/@id")
            .WithParameters(("min", new HeaderExpression("min")))
            .WithParameters(("max", new HeaderExpression("max")))
            .Evaluate<string>(Exchange(("min", 5), ("max", 15)));

        id.Should().Be("A-1");
    }

    // ── The point of the exercise ──

    [Fact]
    public void A_value_carrying_XPath_syntax_stays_a_value()
    {
        var expression = new XPathExpr("/orders/order[@id=$id]/total")
            .WithParameters(("id", new HeaderExpression("wanted")));

        var hostile = expression.Evaluate<string>(Exchange(("wanted", "B-2' or '1'='1")));

        hostile.Should().BeNull(
            "the value is compared, not parsed: no order has that literal id, so nothing matches");
    }

    [Fact]
    public void A_parameterised_expression_refuses_the_template_form_instead_of_dropping_its_bindings()
    {
        // Review finding: the first cut serialised to ${xpath(path)} — the constant path was the
        // point being made, but the serialised form had lost its bindings, so evaluating it would
        // fail on the unbound $id. The same refusal the result type already gets applies here.
        var expression = new XPathExpr("/orders/order[@id=$id]/total")
            .WithParameters(("id", new HeaderExpression("wanted")));

        var act = () => expression.ToTemplateString();

        act.Should().Throw<NotSupportedException>().WithMessage("*bindings would be dropped*");
    }

    [Fact]
    public void A_sourced_expression_refuses_the_template_form_instead_of_silently_reading_the_body()
    {
        var expression = new XPathExpr("/orders/order[1]/total").From(new HeaderExpression("doc"));

        var act = () => expression.ToTemplateString();

        act.Should().Throw<NotSupportedException>().WithMessage("*read the body instead*");
    }

    // ── Composition and refusals ──

    [Fact]
    public void Parameters_compose_with_a_source_and_a_result_type()
    {
        var exchange = new Exchange(new Message("<other/>"));
        exchange.In.Headers["doc"] = Orders;
        exchange.In.Headers["wanted"] = "A-1";

        var value = new XPathExpr("/orders/order[@id=$id]/total", XPathResult.String)
            .From(new HeaderExpression("doc"))
            .WithParameters(("id", new HeaderExpression("wanted")))
            .Evaluate<object>(exchange);

        value.Should().Be("10");
    }

    [Fact]
    public void Parameters_work_in_a_condition()
    {
        var predicate = (IPredicateExpression)new XPathExpr("/orders/order[@id=$id]")
            .WithParameters(("id", new HeaderExpression("wanted")));

        predicate.Matches(Exchange(("wanted", "B-2"))).Should().BeTrue();
        predicate.Matches(Exchange(("wanted", "Z-9"))).Should().BeFalse();
    }

    [Fact]
    public void A_name_written_with_its_dollar_sign_is_refused()
    {
        var act = () => new XPathExpr("/orders/order[@id=$id]")
            .WithParameters(("$id", new HeaderExpression("wanted")));

        act.Should().Throw<ArgumentException>().WithMessage("*without the '$'*");
    }

    [Fact]
    public void A_prefixed_variable_does_not_borrow_an_unprefixed_binding()
    {
        // Review follow-up: resolution used to match on the local name alone, so $x:id quietly
        // resolved to the binding named "id". WithParameters can only bind local names — it rejects
        // even a leading '$' — so a prefixed variable can never have a binding, and the honest
        // answer is the engine's own loud "variable not defined", not a silent match.
        var act = () => new XPathExpr("/orders/order[@id=$x:id]/total")
            .WithNamespaces(("x", "urn:acme"))
            .WithParameters(("id", new HeaderExpression("wanted")))
            .Evaluate<string>(Exchange(("wanted", "B-2")));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void An_unbound_variable_in_the_path_fails_loudly()
    {
        var act = () => new XPathExpr("/orders/order[@id=$missing]/total")
            .WithParameters(("id", new HeaderExpression("wanted")))
            .Evaluate<string>(Exchange(("wanted", "B-2")));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task A_parameterised_expression_works_where_the_DSL_takes_an_expression()
    {
        var seen = new List<object?>();

        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct://xpath-params")
            .SetHeader("total", new XPathExpr("/orders/order[@id=$id]/total")
                .WithParameters(("id", new HeaderExpression("wanted"))))
            .Process(e => seen.Add(e.In.Headers["total"])));

        await context.Start();
        var producer = context.GetEndpoint("direct://xpath-params").CreateProducer();
        await producer.Start();

        await producer.Process(Exchange(("wanted", "A-1")));
        await producer.Process(Exchange(("wanted", "B-2")));

        seen.Should().Equal(10, 20);
    }
}

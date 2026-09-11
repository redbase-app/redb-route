using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Aggregation;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Processors;
using redb.Route.TestKit;
using XPathExpr = redb.Route.Expressions.XPathExpression;

namespace redb.Route.Tests.Definitions;

/// <summary>
/// Six EIPs took a correlation key, a recipient list, a next hop, an idempotency key or an endpoint
/// URI only as a delegate, so a route that wanted one out of the message body had to hand-write
/// <c>e =&gt; XPath("/order/id").Evaluate&lt;string&gt;(e)</c> at every call site. They now accept an
/// <see cref="IExpression"/> directly, the way Apache Camel does — which is also what makes a
/// language added later reach these EIPs without touching them again.
/// </summary>
public class ExpressionDrivenEipTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    private static string Order(string customer) => $"<order><customer>{customer}</customer></order>";

    // ── Aggregate ──

    [Fact]
    public async Task Aggregate_correlates_by_the_expression()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://eip-agg")
                .Aggregate(
                    new XPathExpr("/order/customer"),
                    AggregationStrategies.GroupedBody(),
                    agg => agg.In.Body is List<object?> { Count: >= 2 })
                    .To("mock://eip-agg-out")
                .EndAggregate());
        await ctx.Start();

        // Two for A and one for B: only A's group reaches its completion size, which is the whole
        // point — the key really came from the body rather than from the arrival order.
        await ctx.SendBody("direct://eip-agg", Order("A"));
        await ctx.SendBody("direct://eip-agg", Order("B"));
        await ctx.SendBody("direct://eip-agg", Order("A"));

        await ctx.Mock("mock://eip-agg-out").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
        ctx.Mock("mock://eip-agg-out").ReceivedExchanges[0].In.Body
            .Should().BeEquivalentTo(new List<object?> { Order("A"), Order("A") });
    }

    [Fact]
    public async Task An_expression_that_matched_nothing_says_which_EIP_needed_a_value()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://eip-agg-miss")
                .Aggregate(
                    new XPathExpr("/order/missing"),
                    AggregationStrategies.GroupedBody(),
                    agg => true)
                    .To("mock://eip-agg-miss-out")
                .EndAggregate());
        await ctx.Start();

        var act = () => ctx.SendBody("direct://eip-agg-miss", Order("A"));

        (await act.Should().ThrowAsync<Exception>())
            .Which.ToString().Should()
                .Contain("Aggregate: the expression produced no value")
                .And.Contain("A path that matched nothing yields nothing");
    }

    // ── RecipientList ──

    [Fact]
    public async Task RecipientList_reads_a_sequence_of_matches()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://eip-rl-seq")
                .RecipientList(new XPathExpr("/order/to/uri")));
        await ctx.Start();

        await ctx.SendBody("direct://eip-rl-seq",
            "<order><to><uri>mock://eip-rl-a</uri><uri>mock://eip-rl-b</uri></to></order>");

        await ctx.Mock("mock://eip-rl-a").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
        await ctx.Mock("mock://eip-rl-b").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task RecipientList_reads_one_delimited_string_too()
    {
        // Which shape arrives is a property of the message, not of the route, so both are read.
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://eip-rl-str")
                .RecipientList(new HeaderExpression("recipients")));
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://eip-rl-str", "payload",
            "recipients", "mock://eip-rl-c, mock://eip-rl-d");

        await ctx.Mock("mock://eip-rl-c").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
        await ctx.Mock("mock://eip-rl-d").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
    }

    // ── DynamicRouter ──

    [Fact]
    public async Task DynamicRouter_takes_the_next_hop_from_the_expression_and_stops_on_no_match()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://eip-dr").DynamicRouter(new XPathExpr("/order/next"));
            // The hop rewrites the body, so the next evaluation finds no hop and the routing ends.
            b.From("direct://eip-dr-hop").SetBody("<order/>").To("mock://eip-dr-out");
        });
        await ctx.Start();

        await ctx.SendBody("direct://eip-dr",
            "<order><next>direct://eip-dr-hop</next></order>");

        await ctx.Mock("mock://eip-dr-out").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
    }

    // ── IdempotentConsumer ──

    [Fact]
    public async Task IdempotentConsumer_keys_on_the_expression()
    {
        var repo = new InMemoryIdempotentRepository();
        var processed = 0;

        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://eip-ic")
                .IdempotentConsumer(repo, new JsonPathExpression("$.messageId"))
                    .Process(_ => processed++)
                .EndIdempotentConsumer());
        await ctx.Start();

        await ctx.SendBody("direct://eip-ic", """{"messageId":"m-1","n":1}""");
        await ctx.SendBody("direct://eip-ic", """{"messageId":"m-1","n":2}""");
        await ctx.SendBody("direct://eip-ic", """{"messageId":"m-2","n":3}""");

        processed.Should().Be(2, "the second message repeats an id that the first one already claimed");
    }

    // ── Enrich / PollEnrich ──

    [Fact]
    public async Task Enrich_takes_the_endpoint_from_the_expression()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://eip-enrich")
                .Enrich(new XPathExpr("/order/service"),
                    (original, enriched) =>
                    {
                        original.In.Body = enriched.In.Body;
                        return original;
                    })
                .To("mock://eip-enrich-out");
            b.From("direct://eip-enrich-svc").SetBody("enriched");
        });
        await ctx.Start();

        await ctx.SendBody("direct://eip-enrich",
            "<order><service>direct://eip-enrich-svc</service></order>");

        await ctx.Mock("mock://eip-enrich-out").ExpectBodies("enriched").AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task PollEnrich_takes_the_endpoint_from_the_expression()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://eip-poll")
                .PollEnrich(new HeaderExpression("source"), TimeSpan.FromSeconds(5))
                .To("mock://eip-poll-out");
            b.From("direct://eip-poll-src").SetBody("polled");
        });
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://eip-poll", "original", "source", "direct://eip-poll-src");

        await ctx.Mock("mock://eip-poll-out").ExpectBodies("polled").AssertIsSatisfiedAsync(Wait);
    }

    // ── The expression form and the lambda form agree ──

    [Fact]
    public async Task The_expression_form_routes_exactly_like_the_equivalent_lambda()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://eip-parity-expr").RecipientList(new XPathExpr("/order/to/uri"));
            b.From("direct://eip-parity-lambda").RecipientList(
                e => new XPathExpr("/order/to/uri").Evaluate<string[]>(e));
        });
        await ctx.Start();

        const string body = "<order><to><uri>mock://eip-parity-1</uri><uri>mock://eip-parity-2</uri></to></order>";
        await ctx.SendBody("direct://eip-parity-expr", body);
        await ctx.SendBody("direct://eip-parity-lambda", body);

        await ctx.Mock("mock://eip-parity-1").ExpectMessageCount(2).AssertIsSatisfiedAsync(Wait);
        await ctx.Mock("mock://eip-parity-2").ExpectMessageCount(2).AssertIsSatisfiedAsync(Wait);
    }
}

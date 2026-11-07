using redb.Route.Abstractions;
using redb.Route.Aggregation;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Aggregation;

/// <summary>
/// The strategy library: each strategy in unit form (null-first as Split calls it, and seeded as
/// Multicast / Aggregate call it) and end-to-end on Aggregate, Multicast, Split and Enrich.
/// </summary>
public class AggregationStrategiesTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    private static IExchange Ex(object? body, params (string Key, object? Value)[] headers)
    {
        var e = Exchange.Create(new Message(body), null);
        foreach (var (k, v) in headers) e.In.Headers[k] = v;
        return e;
    }

    // ── Unit: null-first (Split) and seeded (Multicast / Aggregate) ─────────────

    [Fact]
    public void GroupedBody_NullFirst_AndSeeded()
    {
        var s = AggregationStrategies.GroupedBody();

        var fromNull = s(null!, Ex("a"));
        s(fromNull, Ex("b"));
        fromNull.In.Body.Should().BeEquivalentTo(new List<object?> { "a", "b" });

        var seeded = s(Ex("x"), Ex("y"));
        s(seeded, Ex("z"));
        seeded.In.Body.Should().BeEquivalentTo(new List<object?> { "x", "y", "z" });
    }

    [Fact]
    public void GroupedBodyTyped_And_TypeMismatch()
    {
        var s = AggregationStrategies.GroupedBody<int>();

        var acc = s(Ex(1), Ex(2));
        acc.In.Body.Should().BeEquivalentTo(new List<int> { 1, 2 });

        var act = () => s(acc, Ex("three"));
        act.Should().Throw<InvalidOperationException>().WithMessage("GroupedBody<Int32>*");
    }

    [Fact]
    public void GroupedExchange_UseLatest_UseOriginal()
    {
        var a = Ex("a"); var b = Ex("b");

        AggregationStrategies.GroupedExchange()(a, b).In.Body.Should().BeEquivalentTo(new List<IExchange> { a, b });
        AggregationStrategies.UseLatest()(Ex("old"), Ex("new")).In.Body.Should().Be("new");
        AggregationStrategies.UseOriginal()(Ex("old"), Ex("new")).In.Body.Should().Be("old");
        AggregationStrategies.UseOriginal()(null!, Ex("only")).In.Body.Should().Be("only");
    }

    [Fact]
    public void Concat_MergeHeaders_IntoHeader_IntoProperty()
    {
        var concat = AggregationStrategies.Concat(", ");
        var acc = concat(null!, Ex("a"));
        concat(acc, Ex("b"));
        concat(acc, Ex(3));
        acc.In.Body.Should().Be("a, b, 3");

        var merged = AggregationStrategies.MergeHeaders()(Ex("keep", ("h1", 1)), Ex("drop", ("h1", 2), ("h2", 3)));
        merged.In.Body.Should().Be("keep");
        merged.In.Headers["h1"].Should().Be(2, "the later header wins");
        merged.In.Headers["h2"].Should().Be(3);

        var enriched = AggregationStrategies.IntoHeader("customer")(Ex("order"), Ex("Acme"));
        enriched.In.Body.Should().Be("order");
        enriched.In.Headers["customer"].Should().Be("Acme");

        var withProp = AggregationStrategies.IntoProperty("lookup")(Ex("order"), Ex(42));
        withProp.Properties["lookup"].Should().Be(42);
    }

    [Fact]
    public void Sum_Max_Min_OverExpression_NullFirst_AndSeeded()
    {
        var sum = AggregationStrategies.Sum("header.amount");
        var acc = sum(null!, Ex("o1", ("amount", 10)));
        sum(acc, Ex("o2", ("amount", 2.5m)));
        sum(acc, Ex("o3", ("amount", "7.5")));
        acc.In.Body.Should().Be(20m);

        var seededMax = AggregationStrategies.Max("header.amount")(Ex("o1", ("amount", 3)), Ex("o2", ("amount", 9)));
        seededMax.In.Body.Should().Be(9m);

        var min = AggregationStrategies.Min("body.price");
        var minAcc = min(Ex(new { price = 5 }), Ex(new { price = 2 }));
        min(minAcc, Ex(new { price = 8 }));
        minAcc.In.Body.Should().Be(2m);

        var bad = () => AggregationStrategies.Sum("header.missing")(null!, Ex("x"));
        bad.Should().Throw<InvalidOperationException>().WithMessage("*evaluated to null*");
    }

    [Theory]
    [InlineData("groupedBody")]
    [InlineData("GroupedExchange")]
    [InlineData("useLatest")]
    [InlineData("useOriginal")]
    [InlineData("mergeHeaders")]
    [InlineData("concat:;")]
    [InlineData("intoHeader:customer")]
    [InlineData("intoProperty:lookup")]
    [InlineData("sum:header.amount")]
    [InlineData("max:header.amount")]
    [InlineData("min:header.amount")]
    public void ByName_ResolvesEveryDocumentedName(string name)
        => AggregationStrategies.ByName(name).Should().NotBeNull();

    [Theory]
    [InlineData("nope")]
    [InlineData("intoHeader")]
    [InlineData("sum:")]
    public void ByName_RejectsUnknownOrIncomplete(string name)
    {
        var act = () => AggregationStrategies.ByName(name);
        act.Should().Throw<ArgumentException>();
    }

    // ── End-to-end ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Aggregate_WithGroupedBody_CompletesByCount()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://agg")
                .Aggregate(e => "all", AggregationStrategies.GroupedBody(), agg => agg.In.Body is List<object?> { Count: >= 3 })
                    .To("mock://agg-out")
                .EndAggregate());
        await ctx.Start();

        foreach (var body in new[] { "a", "b", "c" })
            await ctx.SendBody("direct://agg", body);

        await ctx.Mock("mock://agg-out").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
        ctx.Mock("mock://agg-out").ReceivedExchanges[0].In.Body.Should().BeEquivalentTo(new List<object?> { "a", "b", "c" });
    }

    [Fact]
    public async Task Aggregate_WithSum_CompletesByTotal()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://sum")
                .Aggregate(e => "all", AggregationStrategies.Sum("header.amount"), agg => agg.In.Body is decimal d && d >= 100)
                    .To("mock://sum-out")
                .EndAggregate());
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://sum", "o1", "amount", 40);
        await ctx.SendBodyAndHeader("direct://sum", "o2", "amount", 70);

        await ctx.Mock("mock://sum-out").ExpectBodies(110m).AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task Multicast_WithConcat_AndWithoutStrategy()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://a").SetBody("A");
            b.From("direct://b").SetBody("B");
            b.From("direct://mc").Multicast().AggregationStrategy(AggregationStrategies.Concat("+")).To("direct://a").To("direct://b").EndMulticast().To("mock://mc-out");
            b.From("direct://mc-plain").Multicast().To("direct://a").To("direct://b").EndMulticast().To("mock://mc-plain-out");
        });
        await ctx.Start();

        await ctx.SendBody("direct://mc", "orig");
        await ctx.SendBody("direct://mc-plain", "orig");

        await ctx.Mock("mock://mc-out").ExpectBodies("A+B").AssertIsSatisfiedAsync(Wait);
        // Pinned: without a strategy the original body stays; the targets' results are not merged back.
        await ctx.Mock("mock://mc-plain-out").ExpectBodies("orig").AssertIsSatisfiedAsync(Wait);
    }

    /// <summary>Code review 2026-09-01 (В7): bodies are cloned by reference, so a strategy must never append to a list it did not create.</summary>
    [Fact]
    public async Task Multicast_GroupedBody_DoesNotMutateAnIncomingList()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://keep").SetHeader("seen", true);          // keeps the incoming list as its body
            b.From("direct://replace").SetBody("B");
            b.From("direct://mc-list").Multicast().AggregationStrategy(AggregationStrategies.GroupedBody())
                .To("direct://keep").To("direct://replace").EndMulticast().To("mock://mc-list-out");
        });
        await ctx.Start();

        var incoming = new List<object?> { "x", "y" };
        await ctx.SendBody("direct://mc-list", incoming);
        await ctx.Mock("mock://mc-list-out").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);

        incoming.Should().Equal("x", "y");   // the caller's list is shared by every branch clone and must stay untouched
        var grouped = (List<object?>)ctx.Mock("mock://mc-list-out").ReceivedExchanges[0].In.Body!;
        grouped.Should().HaveCount(2);
        grouped[0].Should().BeSameAs(incoming, "the first branch's body is the incoming list itself");
        grouped[1].Should().Be("B");
    }

    /// <summary>Code review 2026-09-01 (В8): the clone whose body the strategy handed to the original must not be disposed together with that body.</summary>
    [Fact]
    public async Task Multicast_UseLatest_DoesNotDisposeTheAdoptedStreamBody()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://stream-branch").Process(e => e.In.Body = new MemoryStream("reply"u8.ToArray()));
            b.From("direct://mc-stream").Multicast().AggregationStrategy(AggregationStrategies.UseLatest()).To("direct://stream-branch").EndMulticast();
        });
        await ctx.Start();
        using var template = new ProducerTemplate(ctx);
        template.Start();

        var exchange = Exchange.Create(new Message("in"), null);   // caller-owned: the template does not dispose it
        await template.SendAsync("direct://mc-stream", exchange);

        var body = exchange.In.Body.Should().BeOfType<MemoryStream>().Subject;
        body.CanRead.Should().BeTrue("the aggregated clone's body now belongs to the original exchange");
    }

    /// <summary>Code review 2026-09-01: a numeric body on the seeded first exchange is not the running total; it goes through the expression like every other.</summary>
    [Fact]
    public async Task Sum_OverExchangesWithNumericBodies_SumsTheExpression_NotTheBodies()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://sum-bodies")
                .Aggregate(_ => "all", AggregationStrategies.Sum("header.amount"), agg => agg.In.Body is decimal d && d >= 3)
                    .To("mock://sum-bodies-out")
                .EndAggregate());
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://sum-bodies", 100, "amount", 1);
        await ctx.SendBodyAndHeader("direct://sum-bodies", 200, "amount", 2);

        await ctx.Mock("mock://sum-bodies-out").ExpectBodies(3m).AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task Split_WithGroupedBody_CollectsProcessedFragments()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://split")
                .Split(e => (IEnumerable<object?>)e.In.Body!)
                    .AggregationStrategy(AggregationStrategies.GroupedBody())
                    .SetBody(e => ((string)e.In.Body!).ToUpperInvariant())
                .EndSplit()
                .To("mock://split-out"));
        await ctx.Start();

        await ctx.SendBody("direct://split", new object?[] { "a", "b" });

        await ctx.Mock("mock://split-out").ExpectMessageCount(1).AssertIsSatisfiedAsync(Wait);
        ctx.Mock("mock://split-out").ReceivedExchanges[0].In.Body.Should().BeEquivalentTo(new List<object?> { "A", "B" });
    }

    [Fact]
    public async Task Enrich_WithoutStrategy_IsUseLatest()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://enrich").Enrich("mock://svc").To("mock://enrich-out"));
        await ctx.Start();
        ctx.Mock("mock://svc").WheneverAny().SetBody("reply").SetHeader("from-svc", true);

        await ctx.SendBody("direct://enrich", "question");

        await ctx.Mock("mock://enrich-out").ExpectBodies("reply").ExpectHeader("from-svc", true).AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task Enrich_WithIntoHeader_KeepsBody()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://enrich-h").Enrich("mock://crm", AggregationStrategies.IntoHeader("customer")).To("mock://enrich-h-out"));
        await ctx.Start();
        ctx.Mock("mock://crm").WheneverAny().SetBody("Acme Ltd");

        await ctx.SendBody("direct://enrich-h", "order-1");

        await ctx.Mock("mock://enrich-h-out").ExpectBodies("order-1").ExpectHeader("customer", "Acme Ltd").AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task PollEnrich_WithoutStrategy_TakesThePolledMessage()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://poll").PollEnrich("direct://poll-source", TimeSpan.FromSeconds(5)).To("mock://poll-out");
            b.From("direct://poll-source").SetBody("polled").SetHeader("from-poll", true);
        });
        await ctx.Start();

        await ctx.SendBody("direct://poll", "original");

        await ctx.Mock("mock://poll-out").ExpectBodies("polled").ExpectHeader("from-poll", true).AssertIsSatisfiedAsync(Wait);
    }
}

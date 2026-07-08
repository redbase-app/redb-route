using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Reference;

/// <summary>
/// Reference / canonical DSL examples for the <c>Filter</c> Enterprise Integration Pattern.
///
/// These tests are designed to be readable end-to-end demonstrations of every
/// useful Filter shape supported by the REDB route DSL. They are intentionally
/// over-asserted so that both humans and LLM-driven tooling can use them as
/// authoritative documentation of the contract.
///
/// Forms covered:
///   1. Leaf <c>Filter(predicate)</c> — tail-consuming, no explicit close.
///   2. Leaf <c>Filter(predicate).EndFilter()</c> — explicit scope close.
///   3. Action-overload <c>Filter(predicate, b =&gt; ...)</c> — body closed, tail keeps flowing.
///   4. Nested Filter inside Choice/When.
///   5. Nested Filter inside Split (per-item filtering).
///   6. Nested Filter inside Loop.
///   7. Two sequential Filters (AND-chain).
///   8. Filter inside DoTry — catch sees only matched messages.
///   9. Enterprise scenario: high-value order routing with audit tail.
///  10. Route-level composite Filter DSL cascade — nested action-overload
///      filters form a gating chain (logical AND) with per-stage trace.
///  11. Route-level composite Filter DSL cascade — Camel-style chained form
///      using <c>Filter(…).Filter(…)…EndFilter().EndFilter()</c> without lambdas.
/// </summary>
public class DslFilterReferenceTests
{
    // ── 1. Leaf form ─────────────────────────────────────────────────────────

    /// <summary>
    /// Canonical leaf Filter: predicate gates the rest of the route.
    /// Non-matching exchanges stop after the Filter; matching exchanges flow through.
    /// </summary>
    [Fact]
    public async Task Leaf_Filter_TailConsumed_NonMatchingStops()
    {
        await using var context = new RouteContext();
        var passed = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://leaf-flt")
                .Filter(e => (int)e.In.Body! >= 10)
                .Process(e => passed.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://leaf-flt").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 5, 10, 7, 15, 20, 1 })
            await producer.Process(new Exchange(new Message(i)));

        passed.Should().Equal(10, 15, 20);
    }

    // ── 2. Explicit EndFilter() ──────────────────────────────────────────────

    /// <summary>
    /// Same behaviour as the leaf form, but explicitly closes the Filter scope
    /// with <c>EndFilter()</c>. Camel parity: <c>end()</c> on a filter pops the scope.
    /// </summary>
    [Fact]
    public async Task Leaf_Filter_WithEndFilter_ReopensRouteScope()
    {
        await using var context = new RouteContext();
        var inside = new List<int>();
        var afterEnd = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://flt-endflt")
                .Filter(e => (int)e.In.Body! % 2 == 0)
                    .Process(e => inside.Add((int)e.In.Body!))
                .EndFilter()
                .Process(e => afterEnd.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://flt-endflt").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 2, 3, 4 })
            await producer.Process(new Exchange(new Message(i)));

        inside.Should().Equal(2, 4);
        // EndFilter() pops the Filter scope and restores the parent (route) scope,
        // so steps appended after it run for every exchange — matched and not matched.
        afterEnd.Should().Equal(1, 2, 3, 4);
    }

    // ── 3. Action-overload (W4.4 explicit scope form) ────────────────────────

    /// <summary>
    /// <c>Filter(predicate, body)</c> closes the filtered body when the inner lambda returns —
    /// the route tail after the call runs for every exchange (matched and not matched).
    /// This is the recommended form for embedded filtering.
    /// </summary>
    [Fact]
    public async Task ActionOverload_Filter_BodyClosed_TailRunsForAll()
    {
        await using var context = new RouteContext();
        var inside = new List<int>();
        var tail = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://flt-action")
                .Filter(e => (int)e.In.Body! > 0, b => b
                    .Process(e => inside.Add((int)e.In.Body!)))
                .Process(e => tail.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://flt-action").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { -1, 0, 1, 2, -3, 5 })
            await producer.Process(new Exchange(new Message(i)));

        inside.Should().Equal(1, 2, 5);
        tail.Should().Equal(-1, 0, 1, 2, -3, 5);
    }

    // ── 4. Filter nested in Choice/When ──────────────────────────────────────

    /// <summary>
    /// Choice → When → Filter(action) → Process. Demonstrates that the When
    /// branch tail keeps flowing after the inner Filter body closes.
    /// </summary>
    [Fact]
    public async Task Filter_InsideChoiceWhen_NestedScope_BranchTailRuns()
    {
        await using var context = new RouteContext();
        var matched = new List<string>();
        var branchTail = new List<string>();

        context.AddRoutes(r =>
        {
            r.From("direct://choice-flt-nested")
                .Choice(c => c
                    .When(e => (string)e.In.Headers["type"]! == "order", b => b
                        .Filter(e => (decimal)e.In.Body! > 100m, fb => fb
                            .Process(e => matched.Add($"big:{e.In.Body}")))
                        .Process(e => branchTail.Add($"any:{e.In.Body}"))));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://choice-flt-nested").CreateProducer();
        await producer.Start();

        async Task Send(decimal v, string type) =>
            await producer.Process(new Exchange(new Message
            {
                Body = v,
                Headers = { ["type"] = type }
            }));

        await Send(50m, "order");
        await Send(150m, "order");
        await Send(500m, "order");
        await Send(999m, "invoice"); // skipped by When

        matched.Should().Equal("big:150", "big:500");
        branchTail.Should().Equal("any:50", "any:150", "any:500");
    }

    // ── 5. Filter nested in Split (per-item) ─────────────────────────────────

    /// <summary>
    /// Split → Filter(action) per element. The Filter body sees only matching
    /// items; the Split body tail sees every item.
    /// </summary>
    [Fact]
    public async Task Filter_InsideSplit_PerItem_BodyConditional_TailEveryItem()
    {
        await using var context = new RouteContext();
        var kept = new List<int>();
        var seen = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://split-flt")
                .Split(e => (IEnumerable<object?>)e.In.Body!, b => b
                    .Filter(e => (int)e.In.Body! % 3 == 0, fb => fb
                        .Process(e => kept.Add((int)e.In.Body!)))
                    .Process(e => seen.Add((int)e.In.Body!)));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://split-flt").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message(new object?[] { 1, 2, 3, 4, 6, 7, 9 })));

        kept.Should().Equal(3, 6, 9);
        seen.Should().Equal(1, 2, 3, 4, 6, 7, 9);
    }

    // ── 6. Filter nested in Loop ─────────────────────────────────────────────

    /// <summary>
    /// Loop(n) iterates n times; inner Filter body fires only on matching iterations.
    /// </summary>
    [Fact]
    public async Task Filter_InsideLoop_PerIteration_BodyConditional()
    {
        await using var context = new RouteContext();
        var counter = 0;
        var evens = 0;
        var totalIters = 0;

        context.AddRoutes(r =>
        {
            r.From("direct://loop-flt")
                .Loop(6, b => b
                    .Process(_ => counter++)
                    .Filter(_ => counter % 2 == 0, fb => fb
                        .Process(_ => evens++))
                    .Process(_ => totalIters++));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://loop-flt").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("go")));

        counter.Should().Be(6);
        evens.Should().Be(3);       // iterations 2,4,6
        totalIters.Should().Be(6);  // tail every iteration
    }

    // ── 7. Two sequential Filters (logical AND) ──────────────────────────────

    /// <summary>
    /// Two leaf Filters in sequence: the second predicate only sees messages
    /// that survived the first. Equivalent to a logical AND.
    /// </summary>
    [Fact]
    public async Task TwoSequentialFilters_AreLogicalAnd()
    {
        await using var context = new RouteContext();
        var passed = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://flt-and")
                .Filter(e => (int)e.In.Body! > 0)        // positives
                .Filter(e => (int)e.In.Body! % 2 == 0)    // evens
                .Process(e => passed.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://flt-and").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { -2, -1, 0, 1, 2, 3, 4, 5, 6 })
            await producer.Process(new Exchange(new Message(i)));

        passed.Should().Equal(2, 4, 6);
    }

    // ── 8. Filter inside DoTry ───────────────────────────────────────────────

    /// <summary>
    /// DoTry → Filter(action) → Process(throw). Catch fires only for messages
    /// that pass the filter — non-matching messages never reach the throwing step.
    /// </summary>
    [Fact]
    public async Task Filter_InsideDoTry_GatesExceptionsToMatchedMessages()
    {
        await using var context = new RouteContext();
        var caught = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://flt-try")
                .DoTry()
                    .Filter(e => (int)e.In.Body! >= 10, fb => fb
                        .Process(_ => throw new InvalidOperationException("boom")))
                .DoCatch<InvalidOperationException>()
                    .Process(e => caught.Add((int)e.In.Body!))
                .End();
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://flt-try").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 10, 5, 20, 8, 100 })
            await producer.Process(new Exchange(new Message(i)));

        caught.Should().Equal(10, 20, 100);
    }

    // ── 9. Enterprise scenario: high-value order routing ─────────────────────

    /// <summary>
    /// Realistic enterprise pipeline. Every order is audited; only high-value
    /// orders are routed to the priority channel. Demonstrates how Filter
    /// composes with SetHeader and Process to produce a traceable flow.
    /// </summary>
    [Fact]
    public async Task EnterpriseScenario_HighValueOrders_RoutedAndAudited()
    {
        await using var context = new RouteContext();
        var audit = new List<string>();
        var priority = new List<string>();

        context.AddRoutes(r =>
        {
            r.From("direct://orders-in")
                .SetHeader("audit.timestamp", _ => "T")
                .Process(e => audit.Add($"audit:{e.In.Body}"))
                .Filter(e => (decimal)e.In.Body! >= 1000m, fb => fb
                    .SetHeader("priority", "HIGH")
                    .Process(e => priority.Add($"prio:{e.In.Body}:{e.In.Headers["priority"]}")));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://orders-in").CreateProducer();
        await producer.Start();
        foreach (var amount in new[] { 50m, 1500m, 200m, 9999m, 999m })
            await producer.Process(new Exchange(new Message(amount)));

        audit.Should().Equal("audit:50", "audit:1500", "audit:200", "audit:9999", "audit:999");
        priority.Should().Equal("prio:1500:HIGH", "prio:9999:HIGH");
    }

    // ── 10. Route-level composite Filter DSL cascade ─────────────────────────

    /// <summary>
    /// Composite Filter DSL applied directly at the route level (no Choice wrapper).
    /// Each nested <c>Filter(predicate, fb =&gt; …)</c> is a gate; only exchanges that pass
    /// every gate reach the innermost <c>Process</c>. Per-stage Process steps record a
    /// trace so the cascade behaves as a logical AND with observable stop points.
    /// </summary>
    [Fact]
    public async Task RouteLevel_CompositeFilterDsl_CascadedGatesActAsAnd()
    {
        await using var context = new RouteContext();
        var stage = new List<string>();
        var accepted = new List<(string cur, decimal amt, string country)>();

        context.AddRoutes(r =>
        {
            r.From("direct://route-composite-filter")
                .Process(e => stage.Add($"in:{e.In.Headers["currency"]}/{e.In.Body}/{e.In.Headers["country"]}"))
                .Filter(e => (string)e.In.Headers["currency"]! is "EUR" or "USD", g1 => g1
                    .Process(e => stage.Add($"currency-ok:{e.In.Body}"))
                    .Filter(e => (decimal)e.In.Body! > 0m, g2 => g2
                        .Process(e => stage.Add($"amount-ok:{e.In.Body}"))
                        .Filter(e => (string)e.In.Headers["country"]! is "DE" or "FR" or "US", g3 => g3
                            .Process(e => stage.Add($"country-ok:{e.In.Body}"))
                            .SetHeader("approved", "true")
                            .Transform(e => Math.Round((decimal)e.In.Body! * 0.97m, 2))
                            .Process(e => accepted.Add((
                                (string)e.In.Headers["currency"]!,
                                (decimal)e.In.Body!,
                                (string)e.In.Headers["country"]!))))));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://route-composite-filter").CreateProducer();
        await producer.Start();

        async Task Send(string currency, decimal amount, string country) =>
            await producer.Process(new Exchange(new Message
            {
                Body = amount,
                Headers = { ["currency"] = currency, ["country"] = country }
            }));

        await Send("EUR", 100m, "DE");   // passes all 3 gates
        await Send("GBP", 100m, "DE");   // stops at currency gate
        await Send("USD", -5m, "US");    // stops at amount gate
        await Send("USD", 200m, "RU");   // stops at country gate
        await Send("USD", 50m, "FR");    // passes all 3 gates

        accepted.Should().Equal(
            ("EUR", 97.00m, "DE"),
            ("USD", 48.50m, "FR"));

        stage.Should().Equal(
            // EUR/100/DE — all gates pass
            "in:EUR/100/DE", "currency-ok:100", "amount-ok:100", "country-ok:100",
            // GBP/100/DE — currency gate stops the cascade
            "in:GBP/100/DE",
            // USD/-5/US — amount gate stops
            "in:USD/-5/US", "currency-ok:-5",
            // USD/200/RU — country gate stops
            "in:USD/200/RU", "currency-ok:200", "amount-ok:200",
            // USD/50/FR — all gates pass
            "in:USD/50/FR", "currency-ok:50", "amount-ok:50", "country-ok:50");
    }
    // ── 11. Route-level composite Filter DSL cascade — chained (Camel-style) ──────

    /// <summary>
    /// Same gating cascade as form #10, but using the chained Camel-style DSL:
    /// <c>.Filter(pred).Filter(pred2)…EndFilter().EndFilter()</c>. Each nested
    /// <c>Filter</c> attaches to the previous one as a child (because
    /// <c>FilterDefinition : RouteDefinition</c> inherits the scope opener), and
    /// every <c>EndFilter()</c> pops one level back to the parent scope. The tail
    /// after the final <c>EndFilter()</c> runs for every exchange.
    /// </summary>
    [Fact]
    public async Task RouteLevel_CompositeFilterDsl_ChainedCascade_CamelStyle()
    {
        await using var context = new RouteContext();
        var stage = new List<string>();
        var accepted = new List<(string cur, decimal amt, string country)>();
        var tail = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://route-composite-filter-chained")
                .Process(e => stage.Add($"in:{e.In.Headers["currency"]}/{e.In.Body}/{e.In.Headers["country"]}"))
                .Filter(e => (string)e.In.Headers["currency"]! is "EUR" or "USD")
                    .Process(e => stage.Add($"currency-ok:{e.In.Body}"))
                    .Filter(e => (decimal)e.In.Body! > 0m)
                        .Process(e => stage.Add($"amount-ok:{e.In.Body}"))
                        .Filter(e => (string)e.In.Headers["country"]! is "DE" or "FR" or "US")
                            .Process(e => stage.Add($"country-ok:{e.In.Body}"))
                            .SetHeader("approved", "true")
                            .Transform(e => Math.Round((decimal)e.In.Body! * 0.97m, 2))
                            .Process(e => accepted.Add((
                                (string)e.In.Headers["currency"]!,
                                (decimal)e.In.Body!,
                                (string)e.In.Headers["country"]!)))
                        .EndFilter()  // closes country gate
                    .EndFilter()      // closes amount gate
                .EndFilter()          // closes currency gate — back to route scope
                .Process(e => tail.Add(1));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://route-composite-filter-chained").CreateProducer();
        await producer.Start();

        async Task Send(string currency, decimal amount, string country) =>
            await producer.Process(new Exchange(new Message
            {
                Body = amount,
                Headers = { ["currency"] = currency, ["country"] = country }
            }));

        await Send("EUR", 100m, "DE");
        await Send("GBP", 100m, "DE");
        await Send("USD", -5m, "US");
        await Send("USD", 200m, "RU");
        await Send("USD", 50m, "FR");

        accepted.Should().Equal(
            ("EUR", 97.00m, "DE"),
            ("USD", 48.50m, "FR"));

        // Tail runs for every exchange after EndFilter() pops back to route scope.
        tail.Should().HaveCount(5);

        stage.Should().Equal(
            "in:EUR/100/DE", "currency-ok:100", "amount-ok:100", "country-ok:100",
            "in:GBP/100/DE",
            "in:USD/-5/US", "currency-ok:-5",
            "in:USD/200/RU", "currency-ok:200", "amount-ok:200",
            "in:USD/50/FR", "currency-ok:50", "amount-ok:50", "country-ok:50");
    }}

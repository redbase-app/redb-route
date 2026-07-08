using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Reference;

/// <summary>
/// Reference / canonical DSL examples for the <c>Choice</c> content-based router EIP.
///
/// Every branch is a real <b>multi-step pipeline</b> — <c>WhenDefinition</c>,
/// <c>OtherwiseDefinition</c> and the inner <c>FilterDefinition</c> each own their
/// own <c>Outputs</c> list, and <c>ChoiceProcessor</c> compiles those lists with
/// <c>BuildPipeline</c> into a real <c>PipelineProcessor</c>. The tests below
/// deliberately chain SetHeader → SetProperty → Transform → Process inside the
/// branches so that the pipeline semantics (in particular: header/body/property
/// mutation being visible to the next step in the same branch) are exercised
/// end-to-end.
///
/// Forms covered:
///   1. Two-way Choice with multi-step branches and Otherwise.
///   2. Multi-way Choice with multi-step pipelines per branch.
///   3. Choice without Otherwise — non-matching exchanges fall through untouched.
///   4. Nested Choice inside a When branch with shared pre-step in the outer branch.
///   5. Choice nested-lambda form with multi-step branches.
///   6. EndChoice() restores route scope; tail observes branch mutations.
///   7. Filter-inside-When with its own multi-step pipeline.
///   8. Composite Filter DSL inside When — cascaded Filters as logical AND, plus a
///      nested inner Filter forming a 3-predicate gate; per-gate side-effects prove
///      each Filter has its own real pipeline.
///   9. Composite Filter DSL inside When — Camel-style chained form using
///      <c>Filter(…).Filter(…)…EndFilter().EndFilter()</c> without lambdas.
///  10. Fully chained Camel-style outer Choice with multi-step When branches and
///      a chained Filter cascade nested inside the Otherwise branch — zero lambdas.
///  11. Enterprise scenario: priority + size classifier with audit + reject pipelines.
/// </summary>
public class DslChoiceReferenceTests
{
    // ── 1. Two-way Choice with multi-step branches ───────────────────────────

    /// <summary>
    /// Each branch is a 4-step pipeline (SetHeader → SetProperty → Transform → Process).
    /// The terminal Process reads the header set by SetHeader and the property set by
    /// SetProperty earlier in the same branch, proving the branch is a real pipeline.
    /// </summary>
    [Fact]
    public async Task TwoWay_Choice_MultiStepBranches_PipelineExecutesInOrder()
    {
        await using var context = new RouteContext();
        var observed = new List<(string tag, int original, int transformed)>();

        context.AddRoutes(r =>
        {
            r.From("direct://choice-2way-multi")
                .Choice()
                    .When(e => (int)e.In.Body! > 0)
                        .SetHeader("branch", "pos")
                        .Process(e => e.Properties["original"] = e.In.Body)
                        .Transform(e => (int)e.In.Body! * 10)
                        .Process(e => observed.Add((
                            (string)e.In.Headers["branch"]!,
                            (int)e.Properties["original"]!,
                            (int)e.In.Body!)))
                    .Otherwise()
                        .SetHeader("branch", "neg")
                        .Process(e => e.Properties["original"] = e.In.Body)
                        .Transform(e => -(int)e.In.Body!)
                        .Process(e => observed.Add((
                            (string)e.In.Headers["branch"]!,
                            (int)e.Properties["original"]!,
                            (int)e.In.Body!)))
                .EndChoice();
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://choice-2way-multi").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 5, -3, 0, 7 })
            await producer.Process(new Exchange(new Message(i)));

        observed.Should().Equal(
            ("pos", 5, 50),
            ("neg", -3, 3),
            ("neg", 0, 0),
            ("pos", 7, 70));
    }

    // ── 2. Multi-way Choice — first match wins, multi-step pipelines ─────────

    /// <summary>
    /// Each bucket branch is a real pipeline that stamps the bucket header,
    /// captures the original value as a property, transforms the body, and
    /// finally reads all three of those side-effects in the terminal Process.
    /// </summary>
    [Fact]
    public async Task MultiWay_Choice_FirstMatchWins_EachBranchIsPipeline()
    {
        await using var context = new RouteContext();
        var sink = new List<(string bucket, int original, string body)>();

        context.AddRoutes(r =>
        {
            r.From("direct://choice-buckets-multi")
                .Choice()
                    .When(e => (int)e.In.Body! < 10)
                        .SetHeader("bucket", "small")
                        .Process(e => e.Properties["original"] = e.In.Body)
                        .Transform(e => $"S:{e.In.Body}")
                        .Process(e => sink.Add((
                            (string)e.In.Headers["bucket"]!,
                            (int)e.Properties["original"]!,
                            (string)e.In.Body!)))
                    .When(e => (int)e.In.Body! < 100)
                        .SetHeader("bucket", "medium")
                        .Process(e => e.Properties["original"] = e.In.Body)
                        .Transform(e => $"M:{e.In.Body}")
                        .Process(e => sink.Add((
                            (string)e.In.Headers["bucket"]!,
                            (int)e.Properties["original"]!,
                            (string)e.In.Body!)))
                    .When(e => (int)e.In.Body! < 1000)
                        .SetHeader("bucket", "large")
                        .Process(e => e.Properties["original"] = e.In.Body)
                        .Transform(e => $"L:{e.In.Body}")
                        .Process(e => sink.Add((
                            (string)e.In.Headers["bucket"]!,
                            (int)e.Properties["original"]!,
                            (string)e.In.Body!)))
                    .Otherwise()
                        .SetHeader("bucket", "huge")
                        .Process(e => e.Properties["original"] = e.In.Body)
                        .Transform(e => $"H:{e.In.Body}")
                        .Process(e => sink.Add((
                            (string)e.In.Headers["bucket"]!,
                            (int)e.Properties["original"]!,
                            (string)e.In.Body!)))
                .EndChoice();
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://choice-buckets-multi").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 50, 500, 9999 })
            await producer.Process(new Exchange(new Message(i)));

        sink.Should().Equal(
            ("small", 1, "S:1"),
            ("medium", 50, "M:50"),
            ("large", 500, "L:500"),
            ("huge", 9999, "H:9999"));
    }

    // ── 3. Choice without Otherwise — fall-through ───────────────────────────

    /// <summary>
    /// The matched branch is a multi-step pipeline that doubles the body and
    /// stamps a header. Non-matching exchanges skip the Choice entirely and
    /// reach the tail with their body and headers unchanged.
    /// </summary>
    [Fact]
    public async Task Choice_WithoutOtherwise_NonMatching_FallsThroughToTail()
    {
        await using var context = new RouteContext();
        var matchedSeen = new List<(string tag, int doubled)>();
        var tail = new List<(int body, string? tag)>();

        context.AddRoutes(r =>
        {
            r.From("direct://choice-noelse-multi")
                .Choice()
                    .When(e => (int)e.In.Body! % 2 == 0)
                        .SetHeader("matched", "even")
                        .Transform(e => (int)e.In.Body! * 2)
                        .Process(e => matchedSeen.Add((
                            (string)e.In.Headers["matched"]!,
                            (int)e.In.Body!)))
                .EndChoice()
                .Process(e => tail.Add((
                    (int)e.In.Body!,
                    e.In.Headers.TryGetValue("matched", out var v) ? (string?)v : null)));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://choice-noelse-multi").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 2, 3, 4 })
            await producer.Process(new Exchange(new Message(i)));

        matchedSeen.Should().Equal(("even", 4), ("even", 8));
        tail.Should().Equal(
            (1, null),
            (4, "even"),
            (3, null),
            (8, "even"));
    }

    // ── 4. Nested Choice inside a When branch with shared pre-step ───────────

    /// <summary>
    /// Outer "order" branch has a shared pre-step (audit header + receivedAt
    /// property) that runs BEFORE the inner Choice splits by size. Each inner
    /// branch is itself a multi-step pipeline that reads what the outer pre-step
    /// stamped.
    /// </summary>
    [Fact]
    public async Task NestedChoice_InsideWhen_SharedPreStep_RunsBeforeInnerSplit()
    {
        await using var context = new RouteContext();
        var smallOrder = new List<(string audit, string receivedAt, decimal amt)>();
        var bigOrder = new List<(string audit, decimal amt, decimal vat)>();
        var invoiceLog = new List<(string kind, decimal amt)>();
        var unknown = new List<string>();

        context.AddRoutes(r =>
        {
            r.From("direct://nested-choice-multi")
                .Choice(outer => outer
                    .When(e => (string)e.In.Headers["kind"]! == "order", branch => branch
                        // Shared pre-step for every order, regardless of size.
                        .SetHeader("audit", "order-received")
                        .SetProperty("receivedAt", _ => "2026-01-01T00:00:00Z")
                        .Choice(inner => inner
                            .When(e => (decimal)e.In.Body! < 100m, b => b
                                .SetHeader("tier", "small")
                                .Process(e => smallOrder.Add((
                                    (string)e.In.Headers["audit"]!,
                                    (string)e.Properties["receivedAt"]!,
                                    (decimal)e.In.Body!))))
                            .Otherwise(b => b
                                .SetHeader("tier", "big")
                                .SetProperty("vat", e => (decimal)e.In.Body! * 0.2m)
                                .Process(e => bigOrder.Add((
                                    (string)e.In.Headers["audit"]!,
                                    (decimal)e.In.Body!,
                                    (decimal)e.Properties["vat"]!))))))
                    .When(e => (string)e.In.Headers["kind"]! == "invoice", branch => branch
                        .SetHeader("audit", "invoice-received")
                        .Process(e => invoiceLog.Add(("invoice", (decimal)e.In.Body!))))
                    .Otherwise(branch => branch
                        .SetHeader("audit", "rejected")
                        .Transform(e => $"?[{e.In.Headers["kind"]}]:{e.In.Body}")
                        .Process(e => unknown.Add((string)e.In.Body!))));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://nested-choice-multi").CreateProducer();
        await producer.Start();

        async Task Send(decimal v, string kind) =>
            await producer.Process(new Exchange(new Message
            {
                Body = v,
                Headers = { ["kind"] = kind }
            }));

        await Send(50m, "order");
        await Send(500m, "order");
        await Send(7m, "invoice");
        await Send(999m, "weird");

        smallOrder.Should().Equal(("order-received", "2026-01-01T00:00:00Z", 50m));
        bigOrder.Should().Equal(("order-received", 500m, 100m));
        invoiceLog.Should().Equal(("invoice", 7m));
        unknown.Should().Equal("?[weird]:999");
    }

    // ── 5. Choice nested-lambda form with multi-step branches ────────────────

    /// <summary>
    /// Demonstrates the lambda form. Each branch lambda is a multi-step pipeline:
    /// SetHeader → Transform → Process.
    /// </summary>
    [Fact]
    public async Task ChoiceLambdaForm_MultiStepBranches_TailRunsForAll()
    {
        await using var context = new RouteContext();
        var sink = new List<(string parity, int squared)>();
        var seen = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://choice-lambda-multi")
                .Choice(c => c
                    .When(e => (int)e.In.Body! % 2 == 0, b => b
                        .SetHeader("parity", "even")
                        .Transform(e => (int)e.In.Body! * (int)e.In.Body!)
                        .Process(e => sink.Add((
                            (string)e.In.Headers["parity"]!,
                            (int)e.In.Body!))))
                    .Otherwise(b => b
                        .SetHeader("parity", "odd")
                        .Transform(e => (int)e.In.Body! * (int)e.In.Body!)
                        .Process(e => sink.Add((
                            (string)e.In.Headers["parity"]!,
                            (int)e.In.Body!)))))
                .Process(e => seen.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://choice-lambda-multi").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 2, 3, 4 })
            await producer.Process(new Exchange(new Message(i)));

        sink.Should().Equal(
            ("odd", 1),
            ("even", 4),
            ("odd", 9),
            ("even", 16));
        seen.Should().Equal(1, 4, 9, 16);
    }

    // ── 6. EndChoice() restores route scope ──────────────────────────────────

    /// <summary>
    /// Multi-step branches mutate header and body; the tail after
    /// <c>EndChoice()</c> observes both mutations, confirming the branches
    /// are real pipelines AND that scope is properly restored at route level.
    /// </summary>
    [Fact]
    public async Task EndChoice_RestoresRouteScope_TailObservesBranchMutations()
    {
        await using var context = new RouteContext();
        var tail = new List<string>();

        context.AddRoutes(r =>
        {
            r.From("direct://choice-endchoice-multi")
                .Choice()
                    .When(e => (int)e.In.Body! > 0)
                        .SetHeader("sign", "+")
                        .Transform(e => $"+{e.In.Body}")
                    .Otherwise()
                        .SetHeader("sign", "-")
                        .Transform(e => $"{e.In.Body}")
                .EndChoice()
                .Process(e => tail.Add($"{e.In.Headers["sign"]}|{e.In.Body}"));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://choice-endchoice-multi").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 5, -1, 0, 3 })
            await producer.Process(new Exchange(new Message(i)));

        tail.Should().Equal("+|+5", "-|-1", "-|0", "+|+3");
    }

    // ── 7. Filter-inside-When with its own multi-step pipeline ───────────────

    /// <summary>
    /// Inner Filter has its own pipeline: SetHeader → Transform → Process.
    /// Confirms FilterDefinition.Outputs is compiled as a real PipelineProcessor
    /// inside a When branch.
    /// </summary>
    [Fact]
    public async Task Choice_When_WithInnerFilterPipeline_GatesAndTransforms()
    {
        await using var context = new RouteContext();
        var processed = new List<(string mark, decimal final)>();

        context.AddRoutes(r =>
        {
            r.From("direct://choice-flt-multi")
                .Choice(c => c
                    .When(e => (string)e.In.Headers["type"]! == "order", b => b
                        .Filter(e => (decimal)e.In.Body! != 0m, fb => fb
                            .SetHeader("mark", "non-zero")
                            .Transform(e => (decimal)e.In.Body! * 1.2m)
                            .Process(e => processed.Add((
                                (string)e.In.Headers["mark"]!,
                                (decimal)e.In.Body!))))));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://choice-flt-multi").CreateProducer();
        await producer.Start();

        async Task Send(decimal v, string type) =>
            await producer.Process(new Exchange(new Message
            {
                Body = v,
                Headers = { ["type"] = type }
            }));

        await Send(0m, "order");        // gated by Filter
        await Send(10m, "order");       // 10 * 1.2 = 12
        await Send(0m, "ping");         // gated by When
        await Send(-5m, "order");       // -5 * 1.2 = -6
        await Send(999m, "invoice");    // gated by When

        processed.Should().Equal(("non-zero", 12m), ("non-zero", -6m));
    }

    // ── 8. Composite Filter DSL inside When ──────────────────────────────────

    /// <summary>
    /// Inside the "payment" When branch we build a compound gate out of three
    /// chained Filters (logical AND): currency must be EUR/USD, amount must be
    /// positive, and the country must be on the allow-list. Each Filter has
    /// its own non-trivial pipeline that stamps a "gate-N-passed" property so
    /// that the terminal Process can reconstruct exactly how far each exchange
    /// got. Exchanges that fail a gate keep partial properties up to that gate
    /// but never reach the terminal Process.
    ///
    /// Demonstrates that Filter DSL is fully composable inside a Choice branch
    /// and each Filter compiles its Outputs into a real PipelineProcessor.
    /// </summary>
    [Fact]
    public async Task Choice_When_CompositeFilterDsl_CascadedGatesActAsAnd()
    {
        await using var context = new RouteContext();
        var allowedCountries = new HashSet<string> { "DE", "FR", "US" };
        var accepted = new List<(decimal amount, string currency, string country, string trace)>();
        var rejectedAtGate = new Dictionary<string, int>();

        context.AddRoutes(r =>
        {
            r.From("direct://choice-composite-filter")
                // Pre-Choice: always initialize a trace list so the tail observer
                // can inspect how far each exchange got through the gates.
                .SetProperty("trace", _ => new List<string>())
                .Choice(c => c
                    .When(e => (string)e.In.Headers["kind"]! == "payment", b => b
                        // Gate 1: currency whitelist.
                        .Filter(
                            e => (string)e.In.Headers["currency"]! is "EUR" or "USD",
                            g1 => g1
                                .Process(e => ((List<string>)e.Properties["trace"]!).Add("currency"))
                                // Gate 2: positive amount (nested inside gate 1).
                                .Filter(
                                    e => (decimal)e.In.Body! > 0m,
                                    g2 => g2
                                        .Process(e => ((List<string>)e.Properties["trace"]!).Add("amount"))
                                        // Gate 3: country allow-list (nested inside gate 2).
                                        .Filter(
                                            e => allowedCountries.Contains((string)e.In.Headers["country"]!),
                                            g3 => g3
                                                .Process(e => ((List<string>)e.Properties["trace"]!).Add("country"))
                                                .SetHeader("approved", "true")
                                                .Transform(e => (decimal)e.In.Body! * 0.97m) // fee
                                                .Process(e => accepted.Add((
                                                    (decimal)e.In.Body!,
                                                    (string)e.In.Headers["currency"]!,
                                                    (string)e.In.Headers["country"]!,
                                                    string.Join(">", (List<string>)e.Properties["trace"]!)))))))))
                // Tail observer: runs for every exchange after Choice returns.
                .Process(e =>
                {
                    if (!e.In.Headers.TryGetValue("kind", out var k) || (string)k! != "payment") return;
                    var trace = (List<string>)e.Properties["trace"]!;
                    string reason = trace.Count switch
                    {
                        0 => "currency",
                        1 => "amount",
                        2 => "country",
                        _ => "passed"
                    };
                    if (reason != "passed")
                        rejectedAtGate[reason] = rejectedAtGate.TryGetValue(reason, out var n) ? n + 1 : 1;
                });
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://choice-composite-filter").CreateProducer();
        await producer.Start();

        async Task Send(decimal amount, string currency, string country, string kind = "payment")
            => await producer.Process(new Exchange(new Message
            {
                Body = amount,
                Headers =
                {
                    ["kind"] = kind,
                    ["currency"] = currency,
                    ["country"] = country
                }
            }));

        await Send(100m, "EUR", "DE");      // all gates pass → 97
        await Send(50m, "RUB", "DE");       // gate-1 fails (currency)
        await Send(-10m, "USD", "US");      // gate-2 fails (amount)
        await Send(20m, "EUR", "BR");       // gate-3 fails (country)
        await Send(200m, "USD", "FR");      // all gates pass → 194
        await Send(0m, "EUR", "DE");        // gate-2 fails (amount = 0)

        accepted.Should().Equal(
            (97m, "EUR", "DE", "currency>amount>country"),
            (194m, "USD", "FR", "currency>amount>country"));

        rejectedAtGate.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["currency"] = 1,
            ["amount"] = 2,
            ["country"] = 1,
        });
    }

    // ── 9. Composite Filter DSL inside When — chained (Camel-style) ───────────────

    /// <summary>
    /// Same 3-gate cascade as form #8, but using chained DSL inside the When
    /// branch: <c>.Filter(p1).Process(…).Filter(p2).Process(…).Filter(p3)…EndFilter().EndFilter().EndFilter()</c>.
    /// Because <c>FilterDefinition : RouteDefinition</c> (and <c>WhenDefinition : RouteDefinition</c>),
    /// every nested <c>Filter</c> attaches to its parent's <c>Outputs</c>, and each
    /// <c>EndFilter()</c> pops one scope level. The outer Choice is opened in lambda
    /// form so the chained Filter cascade lives entirely inside the When branch.
    /// </summary>
    [Fact]
    public async Task Choice_When_CompositeFilterDsl_ChainedCascade_CamelStyle()
    {
        await using var context = new RouteContext();
        var stage = new List<string>();
        var accepted = new List<(decimal amount, string currency, string country)>();

        context.AddRoutes(r =>
        {
            r.From("direct://choice-composite-filter-chained")
                .Process(e => stage.Add($"in:{e.In.Headers["kind"]}/{e.In.Headers["currency"]}/{e.In.Body}/{e.In.Headers["country"]}"))
                .Choice(c => c
                    .When(e => (string)e.In.Headers["kind"]! == "payment", b => b
                        .Filter(e => (string)e.In.Headers["currency"]! is "EUR" or "USD")
                            .Process(e => stage.Add($"currency-ok:{e.In.Body}"))
                            .Filter(e => (decimal)e.In.Body! > 0m)
                                .Process(e => stage.Add($"amount-ok:{e.In.Body}"))
                                .Filter(e => (string)e.In.Headers["country"]! is "DE" or "FR" or "US")
                                    .Process(e => stage.Add($"country-ok:{e.In.Body}"))
                                    .SetHeader("approved", "true")
                                    .Transform(e => Math.Round((decimal)e.In.Body! * 0.97m, 2))
                                    .Process(e => accepted.Add((
                                        (decimal)e.In.Body!,
                                        (string)e.In.Headers["currency"]!,
                                        (string)e.In.Headers["country"]!)))
                                .EndFilter()  // closes country gate
                            .EndFilter()      // closes amount gate
                        .EndFilter()));        // closes currency gate — back to When body
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://choice-composite-filter-chained").CreateProducer();
        await producer.Start();

        async Task Send(decimal amount, string currency, string country, string kind = "payment")
            => await producer.Process(new Exchange(new Message
            {
                Body = amount,
                Headers =
                {
                    ["kind"] = kind,
                    ["currency"] = currency,
                    ["country"] = country
                }
            }));

        await Send(100m, "EUR", "DE");      // all gates pass
        await Send(50m, "RUB", "DE");       // currency gate stops
        await Send(-10m, "USD", "US");      // amount gate stops
        await Send(20m, "EUR", "BR");       // country gate stops
        await Send(200m, "USD", "FR");      // all gates pass
        await Send(0m, "EUR", "DE", "refund"); // When gate stops — not a payment

        accepted.Should().Equal(
            (97.00m, "EUR", "DE"),
            (194.00m, "USD", "FR"));

        stage.Should().Equal(
            "in:payment/EUR/100/DE", "currency-ok:100", "amount-ok:100", "country-ok:100",
            "in:payment/RUB/50/DE",
            "in:payment/USD/-10/US", "currency-ok:-10",
            "in:payment/EUR/20/BR", "currency-ok:20", "amount-ok:20",
            "in:payment/USD/200/FR", "currency-ok:200", "amount-ok:200", "country-ok:200",
            "in:refund/EUR/0/DE");
    }

    // ── 10. Fully chained Camel-style Choice + chained Filter cascade in Otherwise ──

    /// <summary>
    /// Pure chained DSL, zero lambdas. Outer <c>Choice()</c> has three When branches,
    /// each a real multi-step pipeline. The <c>Otherwise()</c> branch hosts a chained
    /// 3-gate Filter cascade (currency → amount → country). Because <c>Otherwise()</c>
    /// is the last branch and the cascade is the last thing in the route, no explicit
    /// <c>EndChoice()</c> is required — each scope is opened and closed inline.
    /// </summary>
    [Fact]
    public async Task FullyChained_Choice_WithChainedFilterCascadeInOtherwise_CamelStyle()
    {
        await using var context = new RouteContext();
        var stage = new List<string>();
        var vipQueue = new List<(string queue, decimal amt)>();
        var bulkQueue = new List<(string queue, decimal amt)>();
        var smallQueue = new List<(string queue, decimal amt)>();
        var accepted = new List<(decimal amt, string currency, string country)>();

        context.AddRoutes(r =>
        {
            r.From("direct://choice-fully-chained")
                .Process(e => stage.Add($"in:{e.In.Headers["kind"]}/{e.In.Headers["currency"]}/{e.In.Body}/{e.In.Headers["country"]}"))
                .Choice()
                    .When(e => (string)e.In.Headers["kind"]! == "vip")
                        .SetHeader("queue", "vip")
                        .Transform(e => (decimal)e.In.Body! * 1.10m)
                        .Process(e => vipQueue.Add((
                            (string)e.In.Headers["queue"]!,
                            (decimal)e.In.Body!)))
                    .When(e => (string)e.In.Headers["kind"]! == "bulk")
                        .SetHeader("queue", "bulk")
                        .Transform(e => (decimal)e.In.Body! * 0.90m)
                        .Process(e => bulkQueue.Add((
                            (string)e.In.Headers["queue"]!,
                            (decimal)e.In.Body!)))
                    .When(e => (string)e.In.Headers["kind"]! == "small")
                        .SetHeader("queue", "small")
                        .Transform(e => (decimal)e.In.Body! * 1.00m)
                        .Process(e => smallQueue.Add((
                            (string)e.In.Headers["queue"]!,
                            (decimal)e.In.Body!)))
                    .Otherwise()
                        // Chained Filter cascade inside the last branch.
                        .Filter(e => (string)e.In.Headers["currency"]! is "EUR" or "USD")
                            .Process(e => stage.Add($"currency-ok:{e.In.Body}"))
                            .Filter(e => (decimal)e.In.Body! > 0m)
                                .Process(e => stage.Add($"amount-ok:{e.In.Body}"))
                                .Filter(e => (string)e.In.Headers["country"]! is "DE" or "FR" or "US")
                                    .Process(e => stage.Add($"country-ok:{e.In.Body}"))
                                    .SetHeader("approved", "true")
                                    .Transform(e => Math.Round((decimal)e.In.Body! * 0.97m, 2))
                                    .Process(e => accepted.Add((
                                        (decimal)e.In.Body!,
                                        (string)e.In.Headers["currency"]!,
                                        (string)e.In.Headers["country"]!)))
                                .EndFilter()   // closes country gate
                            .EndFilter()       // closes amount gate
                        .EndFilter();          // closes currency gate (and ends route chain)
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://choice-fully-chained").CreateProducer();
        await producer.Start();

        async Task Send(decimal amount, string kind, string currency = "EUR", string country = "DE")
            => await producer.Process(new Exchange(new Message
            {
                Body = amount,
                Headers =
                {
                    ["kind"] = kind,
                    ["currency"] = currency,
                    ["country"] = country,
                }
            }));

        await Send(1000m, "vip");                       // vip queue
        await Send(500m, "bulk");                       // bulk queue
        await Send(50m, "small");                       // small queue
        await Send(100m, "payment", "EUR", "DE");       // otherwise → all gates pass
        await Send(50m, "payment", "RUB", "DE");        // otherwise → currency gate stops
        await Send(-10m, "payment", "USD", "US");       // otherwise → amount gate stops
        await Send(20m, "payment", "EUR", "BR");        // otherwise → country gate stops
        await Send(200m, "payment", "USD", "FR");       // otherwise → all gates pass

        vipQueue.Should().Equal(("vip", 1100m));
        bulkQueue.Should().Equal(("bulk", 450m));
        smallQueue.Should().Equal(("small", 50m));

        accepted.Should().Equal(
            (97.00m, "EUR", "DE"),
            (194.00m, "USD", "FR"));

        stage.Should().Equal(
            "in:vip/EUR/1000/DE",
            "in:bulk/EUR/500/DE",
            "in:small/EUR/50/DE",
            "in:payment/EUR/100/DE", "currency-ok:100", "amount-ok:100", "country-ok:100",
            "in:payment/RUB/50/DE",
            "in:payment/USD/-10/US", "currency-ok:-10",
            "in:payment/EUR/20/BR", "currency-ok:20", "amount-ok:20",
            "in:payment/USD/200/FR", "currency-ok:200", "amount-ok:200", "country-ok:200");
    }

    // ── 11. Enterprise scenario: priority + size classifier ────────────────────

    /// <summary>
    /// Realistic order-classifier pipeline:
    ///   • Pre-Choice normalization (lower-case priority header).
    ///   • Outer Choice routes by priority. VIP wins regardless of size.
    ///   • Normal orders share a "compute VAT" pre-step then cascade into a
    ///     nested Choice by amount, which stamps the queue and dispatches.
    ///   • Unknown priority is rejected through a multi-step audit pipeline:
    ///     stamp reject-reason header, transform body to an audit envelope,
    ///     publish to the reject sink.
    /// Every branch is a real multi-step pipeline.
    /// </summary>
    [Fact]
    public async Task EnterpriseScenario_OrderClassifier_BranchesArePipelines()
    {
        await using var context = new RouteContext();
        var vipQueue = new List<(string queue, decimal amt, decimal vat)>();
        var smallQueue = new List<(string queue, decimal amt, decimal vat)>();
        var bulkQueue = new List<(string queue, decimal amt, decimal vat)>();
        var rejectQueue = new List<string>();

        context.AddRoutes(r =>
        {
            r.From("direct://orders-classify-multi")
                .Process(e =>
                {
                    var p = (string?)(e.In.Headers.TryGetValue("priority", out var v) ? v : null);
                    e.In.Headers["priority"] = (p ?? "normal").ToLowerInvariant();
                })
                .Choice(outer => outer
                    .When(e => (string)e.In.Headers["priority"]! == "vip", b => b
                        .SetHeader("queue", "vip")
                        .SetProperty("vat", e => (decimal)e.In.Body! * 0.2m)
                        .Process(e => vipQueue.Add((
                            (string)e.In.Headers["queue"]!,
                            (decimal)e.In.Body!,
                            (decimal)e.Properties["vat"]!))))
                    .When(e => (string)e.In.Headers["priority"]! == "normal", b => b
                        // Pre-step shared by all "normal" sub-branches.
                        .SetProperty("vat", e => (decimal)e.In.Body! * 0.2m)
                        .Choice(inner => inner
                            .When(e => (decimal)e.In.Body! < 100m, sb => sb
                                .SetHeader("queue", "small")
                                .Process(e => smallQueue.Add((
                                    (string)e.In.Headers["queue"]!,
                                    (decimal)e.In.Body!,
                                    (decimal)e.Properties["vat"]!))))
                            .Otherwise(sb => sb
                                .SetHeader("queue", "bulk")
                                .Process(e => bulkQueue.Add((
                                    (string)e.In.Headers["queue"]!,
                                    (decimal)e.In.Body!,
                                    (decimal)e.Properties["vat"]!))))))
                    .Otherwise(b => b
                        .SetHeader("queue", "reject")
                        .SetHeader("reject-reason", "unknown-priority")
                        .Transform(e => $"R[{e.In.Headers["priority"]}|{e.In.Headers["reject-reason"]}]:{e.In.Body}")
                        .Process(e => rejectQueue.Add((string)e.In.Body!))));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://orders-classify-multi").CreateProducer();
        await producer.Start();

        async Task Send(decimal amount, string? priority)
        {
            var msg = new Message { Body = amount };
            if (priority is not null) msg.Headers["priority"] = priority;
            await producer.Process(new Exchange(msg));
        }

        await Send(50m, "VIP");
        await Send(50m, "normal");
        await Send(50m, null);
        await Send(500m, "normal");
        await Send(10m, "fraud");
        await Send(9999m, "vip");

        vipQueue.Should().Equal(
            ("vip", 50m, 10m),
            ("vip", 9999m, 1999.8m));
        smallQueue.Should().Equal(
            ("small", 50m, 10m),
            ("small", 50m, 10m));
        bulkQueue.Should().Equal(("bulk", 500m, 100m));
        rejectQueue.Should().Equal("R[fraud|unknown-priority]:10");
    }
}

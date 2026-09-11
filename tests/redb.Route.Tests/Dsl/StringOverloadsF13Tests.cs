using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Aggregation;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;
using redb.Route.Processors.LoadBalancer;

namespace redb.Route.Tests.Dsl;

/// <summary>
/// Route-XML Ф1.3: string overloads on the remaining lambda-only EIPs. Each test mirrors the
/// behaviour of the lambda form through a compiled route; a malformed expression fails at build
/// (the StringExpression compiles at declaration).
/// </summary>
public class StringOverloadsF13Tests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<IProducer> StartAndProducer(string fromUri)
    {
        await _context.Start();
        var producer = _context.GetEndpoint(fromUri).CreateProducer();
        await producer.Start();
        return producer;
    }

    private static IExchange Msg(object? body, params (string Name, object? Value)[] headers)
    {
        var exchange = new Exchange(new Message(body));
        foreach (var (name, value) in headers) exchange.In.Headers[name] = value;
        return exchange;
    }

    // ── RecipientList(string) ────────────────────────────────────────────────

    [Fact]
    public async Task RecipientList_FromDelimitedHeader_RoutesToEveryUri()
    {
        var seen = new List<string>();
        _context.AddRoutes(r =>
        {
            r.From("direct://f13-rcpt-in").RecipientList("${header.targets}");
            r.From("direct://f13-rcpt-a").Process(_ => seen.Add("a"));
            r.From("direct://f13-rcpt-b").Process(_ => seen.Add("b"));
        });
        var producer = await StartAndProducer("direct://f13-rcpt-in");

        await producer.Process(Msg("x", ("targets", "direct://f13-rcpt-a, direct://f13-rcpt-b")));

        seen.Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public async Task RecipientList_FromCollectionHeader_RoutesToEveryUri()
    {
        var seen = new List<string>();
        _context.AddRoutes(r =>
        {
            r.From("direct://f13-rcpt2-in").RecipientList("${header.targets}");
            r.From("direct://f13-rcpt2-a").Process(_ => seen.Add("a"));
            r.From("direct://f13-rcpt2-b").Process(_ => seen.Add("b"));
        });
        var producer = await StartAndProducer("direct://f13-rcpt2-in");

        await producer.Process(Msg("x",
            ("targets", new List<string> { "direct://f13-rcpt2-a", "direct://f13-rcpt2-b" })));

        seen.Should().BeEquivalentTo(["a", "b"]);
    }

    // ── DynamicRouter(string) ────────────────────────────────────────────────

    [Fact]
    public async Task DynamicRouter_PureExpressionOverSteppedProperty_VisitsHopsInOrderAndStops()
    {
        var visited = new List<string>();
        _context.AddRoutes(r =>
        {
            // The XML idiom from examples/eip.route.xml: the router expression is pure, the hops
            // advance the step property themselves.
            r.From("direct://f13-dr-in")
                .SetProperty("step", 0)
                .DynamicRouter("property.step == 0 ? 'direct://f13-dr-a' : (property.step == 1 ? 'direct://f13-dr-b' : null)");
            r.From("direct://f13-dr-a").Process(e => { visited.Add("a"); e.Properties["step"] = 1; });
            r.From("direct://f13-dr-b").Process(e => { visited.Add("b"); e.Properties["step"] = 2; });
        });
        var producer = await StartAndProducer("direct://f13-dr-in");

        await producer.Process(Msg("x"));

        visited.Should().Equal("a", "b");
    }

    // ── Resequence(string) ───────────────────────────────────────────────────

    [Fact]
    public async Task Resequence_ByHeaderExpression_DeliversInKeyOrder()
    {
        var order = new List<object?>();
        _context.AddRoutes(r =>
        {
            r.From("direct://f13-rseq-in")
                .Resequence("header.seq", batchSize: 3)
                .Process(e => order.Add(e.In.Body));
        });
        var producer = await StartAndProducer("direct://f13-rseq-in");

        await producer.Process(Msg("third", ("seq", 3)));
        await producer.Process(Msg("first", ("seq", 1)));
        await producer.Process(Msg("second", ("seq", 2)));

        order.Should().Equal("first", "second", "third");
    }

    // ── Debounce(string) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Debounce_ByKeyExpression_ForwardsOnlyTheLastAfterQuiet()
    {
        var received = new List<object?>();
        _context.AddRoutes(r =>
        {
            r.From("direct://f13-deb-in")
                .Debounce("header.device", TimeSpan.FromMilliseconds(100))
                .Process(e => received.Add(e.In.Body));
        });
        var producer = await StartAndProducer("direct://f13-deb-in");

        await producer.Process(Msg("v1", ("device", "d1")));
        await producer.Process(Msg("v2", ("device", "d1")));
        await producer.Process(Msg("v3", ("device", "d1")));

        await WaitUntil(() => received.Count == 1, TimeSpan.FromSeconds(3));
        received.Should().Equal("v3");
    }

    // ── IdempotentConsumer(string, string) ───────────────────────────────────

    [Fact]
    public async Task IdempotentConsumer_KeyExpressionAndRegistryName_SkipsDuplicates()
    {
        var processed = new List<object?>();
        _context.AddToRegistry("idempotent:f13-idemp-repo", new InMemoryIdempotentRepository());
        _context.AddRoutes(r =>
        {
            r.From("direct://f13-idemp-in")
                .IdempotentConsumer("${header.messageId}", "f13-idemp-repo")
                .Process(e => processed.Add(e.In.Body));
        });
        var producer = await StartAndProducer("direct://f13-idemp-in");

        await producer.Process(Msg("first", ("messageId", "m-1")));
        await producer.Process(Msg("dup", ("messageId", "m-1")));
        await producer.Process(Msg("second", ("messageId", "m-2")));

        processed.Should().Equal("first", "second");
    }

    // ── Aggregate(string, ...) ───────────────────────────────────────────────

    [Fact]
    public async Task Aggregate_CorrelationExpressionAndCompletionSize_GroupsBodies()
    {
        var batches = new List<List<object?>>();
        _context.AddRoutes(r =>
        {
            r.From("direct://f13-agg-in")
                .Aggregate("${header.batch}", AggregationStrategies.GroupedBody(), completionSize: 3)
                .Process(e => batches.Add(new List<object?>((List<object?>)e.In.Body!)));
        });
        var producer = await StartAndProducer("direct://f13-agg-in");

        await producer.Process(Msg("a", ("batch", "b1")));
        await producer.Process(Msg("b", ("batch", "b1")));
        await producer.Process(Msg("c", ("batch", "b1")));

        batches.Should().ContainSingle().Which.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task Aggregate_CompletionCondition_CompletesWhenTheConditionHolds()
    {
        var results = new List<object?>();
        _context.AddRoutes(r =>
        {
            r.From("direct://f13-aggc-in")
                .Aggregate("${header.batch}", AggregationStrategies.Concat("+"),
                    completionCondition: "contains(body, '+')")
                .Process(e => results.Add(e.In.Body));
        });
        var producer = await StartAndProducer("direct://f13-aggc-in");

        await producer.Process(Msg("a", ("batch", "b1")));
        await producer.Process(Msg("b", ("batch", "b1")));

        results.Should().ContainSingle().Which.Should().Be("a+b");
    }

    [Fact]
    public async Task Aggregate_CompletionTimeout_FlushesTheGroupWithWhatItHas()
    {
        var batches = new List<List<object?>>();
        _context.AddRoutes(r =>
        {
            r.From("direct://f13-aggt-in")
                .Aggregate("${header.batch}", AggregationStrategies.GroupedBody(),
                    completionSize: 100, completionTimeout: TimeSpan.FromMilliseconds(150))
                .Process(e => batches.Add(new List<object?>((List<object?>)e.In.Body!)));
        });
        var producer = await StartAndProducer("direct://f13-aggt-in");

        await producer.Process(Msg("a", ("batch", "b1")));
        await producer.Process(Msg("b", ("batch", "b1")));

        await WaitUntil(() => batches.Count == 1, TimeSpan.FromSeconds(5));
        batches.Should().ContainSingle().Which.Should().Equal("a", "b");
    }

    [Fact]
    public async Task Aggregate_CompletionTimeout_DoesNotFlushAfterTheContextIsDisposed()
    {
        // Review Ф1: the scan timer must die with the context. Before the fix nothing disposed
        // processors at all (the resequencer's DisposeAsync was dead code), so a pending group
        // was flushed into a stopped pipeline by a background timer that lived forever.
        var flushed = 0;
        var context = new RouteContext();
        context.AddRoutes(r =>
            r.From("direct://f13-aggd-in")
                .Aggregate("${header.batch}", AggregationStrategies.GroupedBody(),
                    completionSize: 100, completionTimeout: TimeSpan.FromMilliseconds(200))
                .Process(_ => Interlocked.Increment(ref flushed)));
        await context.Start();
        var producer = context.GetEndpoint("direct://f13-aggd-in").CreateProducer();
        await producer.Start();
        await producer.Process(Msg("a", ("batch", "b1"))); // the group stays pending

        await context.DisposeAsync();
        await Task.Delay(600); // well past the inactivity timeout

        flushed.Should().Be(0, "a disposed context must not flush pending groups from a background timer");
    }

    [Fact]
    public async Task Aggregate_WithoutAnyCompletionCriterion_IsRefusedWhenTheBuilderRuns()
    {
        // Review Ф1: the first version of this test discarded an un-awaited ThrowAsync and was
        // green no matter what. Builder lambdas are deferred, so the declaration error surfaces
        // at Start() — and the assertion must actually await it.
        _context.AddRoutes(r =>
            r.From("direct://f13-aggx-in")
                .Aggregate("${header.batch}", AggregationStrategies.GroupedBody()));

        var act = () => _context.Start();

        (await act.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*at least one completion criterion*");
    }

    // ── OfType(Type) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OfType_NonGeneric_BuildsTheSameTypedSectionAsTheGenericForm()
    {
        // OfType<T> is a typed-view section, not a guard: the body converts to T and typed
        // helpers run only for matching bodies. The non-generic form's contract is parity of
        // construction — the same closed OfTypeDefinition<T> the generic verb creates.
        var seen = new List<object?>();
        _context.AddRoutes(r =>
        {
            var section = r.From("direct://f13-oftype-in").OfType(typeof(int));
            section.Should().BeOfType<OfTypeDefinition<int>>();
            section.Process(e => seen.Add(e.In.Body));
        });
        var producer = await StartAndProducer("direct://f13-oftype-in");

        await producer.Process(Msg("42")); // ConvertBody inside the section: "42" becomes int 42

        seen.Should().ContainSingle().Which.Should().Be(42);
    }

    // ── RoutePolicy(string) ──────────────────────────────────────────────────

    private sealed class RecordingPolicy : IRoutePolicy
    {
        public int Started;
        public Task OnStart(IRouteContext context, CompiledRoute route, CancellationToken ct)
        {
            Interlocked.Increment(ref Started);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RoutePolicy_ByRegistryName_ResolvesAtCompile()
    {
        var policy = new RecordingPolicy();
        _context.AddToRegistry("f13-policy", policy);
        _context.AddRoutes(r =>
            r.From("direct://f13-pol-in").RoutePolicy("#f13-policy").Process(_ => { }));

        await _context.Start();

        policy.Started.Should().Be(1);
    }

    [Fact]
    public async Task RoutePolicy_MissingRegistryName_FailsStartNamingTheRoute()
    {
        _context.AddRoutes(r =>
            r.From("direct://f13-polx-in").RouteId("f13-polx").RoutePolicy("absent-policy").Process(_ => { }));

        var act = () => _context.Start();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*f13-polx*absent-policy*not in the context registry*");
    }

    // ── UseSticky(string) / Normalizer.When(string) ──────────────────────────

    [Fact]
    public void UseSticky_KeyExpression_SelectsTheStickyStrategy()
    {
        var lb = new LoadBalancerDefinition();
        lb.UseSticky("header.customerId");

        lb.Strategy.Should().BeOfType<StickyStrategy>();
    }

    [Fact]
    public async Task NormalizerWhen_ConditionString_TransformsMatchingBodies()
    {
        _context.AddRoutes(r =>
            r.From("direct://f13-norm-in")
                .Normalize(n => n
                    .When("header.kind == 'greeting'", e => $"hello:{e.In.Body}")
                    .Otherwise(e => $"other:{e.In.Body}"))
                .Process(_ => { }));
        var producer = await StartAndProducer("direct://f13-norm-in");

        var greeting = Msg("bob", ("kind", "greeting"));
        var other = Msg("bob", ("kind", "misc"));
        await producer.Process(greeting);
        await producer.Process(other);

        greeting.In.Body.Should().Be("hello:bob");
        other.In.Body.Should().Be("other:bob");
    }

    // ── Fail-fast: a malformed expression fails while the route is built ─────

    [Fact]
    public async Task MalformedKeyExpression_FailsAtBuild_NotOnTheFirstMessage()
    {
        _context.AddRoutes(r =>
            r.From("direct://f13-bad-in").Debounce("header.a >", TimeSpan.FromMilliseconds(50)));

        var act = () => _context.Start();

        await act.Should().ThrowAsync<Exception>("a malformed expression must fail Start(), not the first message");
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        condition().Should().BeTrue($"condition not reached within {timeout}");
    }
}

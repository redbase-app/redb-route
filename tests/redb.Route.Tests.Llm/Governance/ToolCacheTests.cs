using System.Security.Claims;
using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.Governance;

/// <summary>
/// Tool-result cache (wave 3). Two layers, one question — "do I already have an output for this exact
/// input?": <c>Memoize</c> is answered by a run-scoped in-process memo, <c>Persist</c> by the
/// configured store. A hit is a skipped invocation reported to observers, never a silent optimisation.
/// <para>
/// The last two tests are locks on the boundaries: a memo must not survive its run, and a mutating
/// tool must not be cacheable at all (a hit would suppress the side effect it stands for).
/// </para>
/// </summary>
[Trait("Category", "Governance")]
public sealed class ToolCacheTests
{
    private const string Schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""";
    private const string Input = """{"q":"x"}""";
    private const string Reply = """{"answer":"42"}""";

    [Fact]
    public async Task CachingMemoize_TwoIdenticalCalls_SecondIsServedFromCache()
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("lookup", Input, "tu_1")
            .EnqueueToolUse("lookup", Input, "tu_2")
            .EnqueueText("done");
        var spy = new ToolInvocationSpy();

        var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, Reply,
            caching: ToolCachingPolicy.Memoize);

        await using var host = LiveLlmHost.Build(spy)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Call the tool twice.");

        tool.CapturedInputs.Should().HaveCount(1,
            "Memoize answers the second identical call from the run-scoped cache instead of dispatching it");
        spy.ForTool("lookup").Should().Contain(i => i.Skipped && i.SkipReason == ToolSkipReasons.CacheHit,
            "a hit is reported as a skipped invocation, not as an invisible optimisation");
    }

    [Fact]
    public async Task CachingNone_TwoIdenticalCalls_BothDispatch()
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("lookup", Input, "tu_1")
            .EnqueueToolUse("lookup", Input, "tu_2")
            .EnqueueText("done");

        var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, """{"answer":"42"}""");

        await using var host = LiveLlmHost.Build()
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Call the tool twice.");

        tool.CapturedInputs.Should().HaveCount(2,
            "with caching off every call must reach the tool — the cache must not invent memoisation");
    }

    [Fact]
    public async Task CachingPersist_SurvivesNewEngineInstance()
    {
        var store = new SpyToolCacheStore();

        var toolInFirstHost = new EchoToolRoute("lookup", "Look up a fact.", Schema, Reply,
            caching: ToolCachingPolicy.Persist);
        var firstRun = new FakeProvider()
            .EnqueueToolUse("lookup", Input, "tu_1")
            .EnqueueText("done");

        await using (var host = LiveLlmHost.Build(toolCache: store)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = firstRun }))
        {
            await host.StartAsync(toolInFirstHost, r => r.From("direct:agent")
                .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
                .To("mock:done"));

            await host.SendAsync("direct:agent", "Call the tool.");
        }

        toolInFirstHost.CapturedInputs.Should().HaveCount(1);
        store.Sets.Should().HaveCount(1, "Persist writes the output to the store");

        // A second host means a second engine instance: only the store carries state across.
        var toolInSecondHost = new EchoToolRoute("lookup", "Look up a fact.", Schema, Reply,
            caching: ToolCachingPolicy.Persist);
        var secondRun = new FakeProvider()
            .EnqueueToolUse("lookup", Input, "tu_2")
            .EnqueueText("done");

        await using (var host = LiveLlmHost.Build(toolCache: store)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = secondRun }))
        {
            await host.StartAsync(toolInSecondHost, r => r.From("direct:agent")
                .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
                .To("mock:done"));

            await host.SendAsync("direct:agent", "Call the tool.");
        }

        toolInSecondHost.CapturedInputs.Should().BeEmpty(
            "the persisted entry answers the identical call from a new engine instance");
    }

    [Fact]
    public async Task Cache_TwoTenantsSameInput_TwoEntries()
    {
        var store = new SpyToolCacheStore();
        var provider = new FakeProvider()
            .EnqueueToolUse("lookup", Input, "tu_1")
            .EnqueueText("done")
            .EnqueueToolUse("lookup", Input, "tu_2")
            .EnqueueText("done")
            .EnqueueToolUse("lookup", Input, "tu_3")
            .EnqueueText("done");

        var routes = new TenantScopedToolRoutes();

        await using var host = LiveLlmHost.Build(toolCache: store)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        host.ToolRegistry.Register(new TenantScopedToolDescriptor());
        await host.StartAsync(routes, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools(TenantScopedToolDescriptor.ToolName).MaxIterations(4).AsUri())
            .To("mock:done"));

        await SendAsTenant(host, "a");
        await SendAsTenant(host, "b");
        await SendAsTenant(host, "a");

        routes.CountFor("a").Should().Be(1,
            "the identical call from the same tenant is answered from its own entry");
        routes.CountFor("b").Should().Be(1,
            "tenant b sends the same input, but its resolved address differs — the cache must never serve one tenant's output to another");
        store.Sets.Should().HaveCount(2, "one entry per resolved address");
        store.Sets.Should().OnlyHaveUniqueItems("the two entries carry different keys");
    }

    [Fact]
    public async Task Memoize_EntriesDoNotOutliveTheRun()
    {
        var store = new SpyToolCacheStore();
        var provider = new FakeProvider()
            .EnqueueToolUse("lookup", Input, "tu_1")
            .EnqueueText("done")
            .EnqueueToolUse("lookup", Input, "tu_2")
            .EnqueueText("done");

        var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, Reply,
            caching: ToolCachingPolicy.Memoize);

        await using var host = LiveLlmHost.Build(toolCache: store)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Call the tool.");
        await host.SendAsync("direct:agent", "Call the tool.");

        tool.CapturedInputs.Should().HaveCount(2,
            "the memo belongs to one run — a later run must dispatch the identical call again");
        store.Sets.Should().BeEmpty("Memoize is served in process: it never reaches the store");
        store.Gets.Should().BeEmpty("and the store is never read for it either");
    }

    [Fact]
    public async Task MutatingTool_WithCaching_IsRejectedAtBuild()
    {
        var tool = new EchoToolRoute("lookup", "Send an invoice.", Schema, Reply,
            sideEffect: ToolSideEffect.Mutating, caching: ToolCachingPolicy.Persist);

        await using var host = LiveLlmHost.Build()
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub" });

        var thrown = await Record.ExceptionAsync(() => host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
            .To("mock:done")));

        thrown.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("Caching",
                "a cache hit would suppress the side effect, so the route must not build at all");
    }

    private static Task<IExchange> SendAsTenant(LiveLlmHost host, string tenant)
        => host.SendAsync("direct:agent", "Call the tool.",
            new Dictionary<string, object?> { [TenantScopedToolDescriptor.TenantHeader] = tenant });

    /// <summary>
    /// A cached entry belongs to the policy the tool had when the entry was written. Tightening the
    /// policy (here: adding a required claim) must not be answered by that entry — otherwise data
    /// fetched while the tool was open to everyone keeps flowing after it was closed down.
    /// </summary>
    [Fact]
    public async Task CachingPolicyChange_DoesNotServeAnEntryWrittenUnderTheOldPolicy()
    {
        var store = new SpyToolCacheStore();

        var lax = new EchoToolRoute("lookup", "Look up a fact.", Schema, Reply,
            caching: ToolCachingPolicy.Persist);
        var firstRun = new FakeProvider()
            .EnqueueToolUse("lookup", Input, "tu_1")
            .EnqueueText("done");

        await using (var host = LiveLlmHost.Build(toolCache: store)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = firstRun }))
        {
            await host.StartAsync(lax, r => r.From("direct:agent")
                .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
                .To("mock:done"));

            await host.SendAsync("direct:agent", "Call the tool.");
        }

        lax.CapturedInputs.Should().HaveCount(1);
        store.Sets.Should().HaveCount(1, "the entry was written while the tool had no requirement");

        var stricter = new EchoToolRoute("lookup", "Look up a fact.", Schema, Reply,
            caching: ToolCachingPolicy.Persist, requiredClaims: ["orders:read"]);
        var secondRun = new FakeProvider()
            .EnqueueToolUse("lookup", Input, "tu_2")
            .EnqueueText("done");
        var spy = new ToolInvocationSpy();
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("scope", "orders:read")], authenticationType: "test"));

        await using (var host = LiveLlmHost.Build(spy,
                claimsSource: new ExchangePrincipalClaimsSource(), toolCache: store)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = secondRun }))
        {
            await host.StartAsync(stricter, r => r.From("direct:agent")
                .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
                .To("mock:done"));

            await host.SendAsync("direct:agent", "Call the tool.",
                configure: ex => ExchangePrincipal.Set(ex, principal));
        }

        stricter.CapturedInputs.Should().HaveCount(1,
            "a stricter policy is a different cache entry, not a hit on the laxer one");
        spy.ForTool("lookup").Should().Contain(i => !i.Skipped);
        store.Sets.Should().HaveCount(2, "the new policy writes its own entry");
    }

    /// <summary>
    /// The tool route sees the caller's principal, so "same input" is not "same answer": an entry
    /// fetched for one caller must not be served to another, even when both hold the same rights.
    /// </summary>
    [Fact]
    public async Task Cache_PersistedEntry_IsNotServedAcrossCallers()
    {
        var store = new SpyToolCacheStore();

        async Task<int> RunAsAsync(string subject)
        {
            var provider = new FakeProvider()
                .EnqueueToolUse("lookup", Input, "tu_1")
                .EnqueueText("done");
            var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, Reply,
                caching: ToolCachingPolicy.Persist);

            await using var host = LiveLlmHost.Build(toolCache: store)
                .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

            await host.StartAsync(tool, r => r.From("direct:agent")
                .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
                .To("mock:done"));

            var principal = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, subject)], authenticationType: "test"));

            await host.SendAsync("direct:agent", "Call the tool.",
                configure: ex => ExchangePrincipal.Set(ex, principal));

            return tool.CapturedInputs.Count;
        }

        (await RunAsAsync("alice")).Should().Be(1);
        (await RunAsAsync("bob")).Should().Be(1,
            "another caller asking the same question is a different question — the entry belongs to alice");
        store.Sets.Should().HaveCount(2, "one entry per caller");
    }

    /// <summary>
    /// Headers the route opted into propagating (<c>.PropagateToolHeaders(...)</c>) reach the tool, so
    /// they are part of what it answers.
    /// </summary>
    [Fact]
    public async Task Cache_PersistedEntry_IsNotServedAcrossPropagatedHeaderValues()
    {
        var store = new SpyToolCacheStore();

        async Task<int> RunWithTenantAsync(string tenant)
        {
            var provider = new FakeProvider()
                .EnqueueToolUse("lookup", Input, "tu_1")
                .EnqueueText("done");
            var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, Reply,
                caching: ToolCachingPolicy.Persist);

            await using var host = LiveLlmHost.Build(toolCache: store)
                .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

            await host.StartAsync(tool, r => r.From("direct:agent")
                .To(LlmDsl.Factory("demo").PropagateToolHeaders("x-tenant-id")
                    .Tools("lookup").MaxIterations(4).AsUri())
                .To("mock:done"));

            await host.SendAsync("direct:agent", "Call the tool.",
                new Dictionary<string, object?> { ["x-tenant-id"] = tenant });

            return tool.CapturedInputs.Count;
        }

        (await RunWithTenantAsync("acme")).Should().Be(1);
        (await RunWithTenantAsync("globex")).Should().Be(1,
            "the header the route chose to pass on is part of the tool's input");
        store.Sets.Should().HaveCount(2, "one entry per propagated header value");
    }

    /// <summary>
    /// An unavailable cache costs a hit, never the run: the tool still executes, the model still gets a
    /// normal result, and the failure is logged rather than thrown at the model.
    /// </summary>
    [Fact]
    public async Task CacheStoreFailure_DoesNotFailTheRun()
    {
        var provider = new FakeProvider()
            .EnqueueToolUse("lookup", Input, "tu_1")
            .EnqueueText("done");
        var spy = new ToolInvocationSpy();
        var tool = new EchoToolRoute("lookup", "Look up a fact.", Schema, Reply,
            caching: ToolCachingPolicy.Persist);

        await using var host = LiveLlmHost.Build(spy, toolCache: new ThrowingToolCacheStore())
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools("lookup").MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Call the tool.");

        tool.CapturedInputs.Should().HaveCount(1, "a broken cache must not stop the tool from running");
        spy.ForTool("lookup").Should().OnlyContain(i => !i.Skipped && i.Exception == null,
            "the model receives a normal tool result, not a cache error");
    }

    /// <summary>
    /// The read-only rule is enforced where every descriptor path converges — the registry — so
    /// <c>LlmTool.Define(...).Build()</c>, the <c>[ExposeAsLlmTool]</c> attribute and MCP discovery are
    /// covered as well, not just the route DSL.
    /// </summary>
    [Fact]
    public void MutatingTool_WithCaching_IsRejectedByRegistration()
    {
        var descriptor = LlmTool.Define("send_invoice")
            .EndpointUri("direct:send-invoice")
            .Description("Send an invoice.")
            .Input(Schema)
            .SideEffect(ToolSideEffect.Mutating)
            .Caching(ToolCachingPolicy.Persist)
            .Build();

        var act = () => new ToolDescriptorRegistry().Register(descriptor);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Caching",
                "a cache hit would suppress the side effect — enforced for every registration path");
    }
}

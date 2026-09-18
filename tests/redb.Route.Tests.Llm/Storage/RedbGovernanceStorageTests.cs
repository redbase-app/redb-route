using redb.Core.Models.Entities;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Storage.Redb;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.Route.RedbCore;
using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>
/// redb-integration level of the phase-15 acceptance (plan §7.1): the same claims the unit tests prove
/// against doubles, checked against a real database through the real stores — <c>Persist</c> reaches
/// <see cref="ToolCacheProps"/>, a cross-run budget reaches <see cref="CostBudgetProps"/>, and audit
/// reasons reach <see cref="ToolAuditProps"/>.
/// <para>
/// Rows are scoped to this test by unique payloads / conversation ids / tool names: the collection
/// fixture shares one database with the rest of the Storage suite and cleans up once per run.
/// </para>
/// </summary>
[Collection("StoragePro")]
public sealed class RedbGovernanceStorageTests
{
    private const string Schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}""";
    private const string Input = """{"q":"x"}""";

    private readonly StorageProFixture _fx;

    public RedbGovernanceStorageTests(StorageProFixture fx) => _fx = fx;

    // Rows are located with server-side predicates, the way the stores themselves query: the collection
    // fixture shares one database with the rest of the Storage suite, so pulling a table whole would read
    // rows this test never touched — and grow slower with every run.
    //
    // All three helpers use the OBJECT-side filter (WhereRedb): it narrows _objects and touches nothing
    // else. A props-typed predicate (Where) is server-side too, but it resolves through the values table,
    // where the condition sits above GROUP BY unless the PVT prefilter cuts the scan first — see
    // docs/PVT_PREFILTER_PLAN.md. The fixture turns that prefilter on for every Pro provider, so on this
    // path a mixed chain would get its value-side work narrowed too; the base-only form stays the cheapest,
    // which is why these helpers use it.
    // The tool name rides a base field on purpose: for the audit row it is the row name, for the cache the
    // key inside value_string, which keeps the cheap path available to dashboards too.
    private Task<List<RedbObject<ToolCacheProps>>> ReadCacheRowsAsync(string toolName)
        => _fx.Redb.Query<ToolCacheProps>()
            .WhereRedb(o => o.ValueString != null && o.ValueString.Contains(toolName))
            .ToListAsync();

    private async Task<CostBudgetProps> ReadBudgetAsync(string conversationId)
    {
        var key = RedbUniqueKey.Normalize(conversationId);
        var row = await _fx.Redb.Query<CostBudgetProps>()
            .WhereRedb(o => o.ValueUnique == key)
            .FirstOrDefaultAsync();
        row.Should().NotBeNull("the enforcer records usage per conversation");
        return row!.Props;
    }

    private async Task<IReadOnlyList<ToolAuditProps>> ReadAuditAsync(string toolName)
        => await _fx.Redb.Query<ToolAuditProps>()
            // One object-side predicate only: it narrows _objects and touches nothing else. A props-typed
            // predicate would resolve through the values table, and with the PVT prefilter off (the shipped
            // default — Pro/PostgreSQL only) nothing cuts the set before that work. See the block comment.
            .WhereRedb(o => o.Name != null && o.Name.Contains(toolName))
            .Select(a => a.Props)
            .ToListAsync();

    [Fact]
    public async Task PersistCachingTool_WritesItsOutputIntoTheCacheScheme()
    {
        // The cache stores the DISPATCHED output, which the engine serializes as a JSON string — the row is
        // found by its unique tool name and then checked for the marker the reply carried.
        var toolName = $"persist_{Guid.NewGuid():N}"[..20];
        var marker = Guid.NewGuid().ToString("N");
        var provider = new FakeProvider()
            .EnqueueToolUse(toolName, Input, "tu_1")
            .EnqueueText("done");
        var tool = new EchoToolRoute(toolName, "Look up a fact.", Schema, $$"""{"answer":"{{marker}}"}""",
            caching: ToolCachingPolicy.Persist);

        await using var host = LiveLlmHost.Build(
                toolCache: new RedbToolResultCache(_fx.RouteContext), redb: _fx.Redb)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools(toolName).MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Call the tool.");

        tool.CapturedInputs.Should().HaveCount(1);
        var rows = await ReadCacheRowsAsync(toolName);
        rows.Should().ContainSingle("a Persist tool's output lands in ToolCacheProps");
        rows[0].Props.OutputJson.Should().Contain(marker,
            "the stored value is the output the tool actually produced");
    }

    [Fact]
    public async Task MemoizeCachingTool_WritesNothingIntoTheCacheScheme()
    {
        var toolName = $"memo_{Guid.NewGuid():N}"[..20];
        var reply = $"{{\"answer\":\"{Guid.NewGuid():N}\"}}";
        var provider = new FakeProvider()
            .EnqueueToolUse(toolName, Input, "tu_1")
            .EnqueueToolUse(toolName, Input, "tu_2")
            .EnqueueText("done");
        var tool = new EchoToolRoute(toolName, "Look up a fact.", Schema, reply,
            caching: ToolCachingPolicy.Memoize);

        await using var host = LiveLlmHost.Build(
                toolCache: new RedbToolResultCache(_fx.RouteContext), redb: _fx.Redb)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools(toolName).MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Call the tool twice.");

        tool.CapturedInputs.Should().HaveCount(1, "the second call is answered from the run-scoped memo");
        (await ReadCacheRowsAsync(toolName)).Should().BeEmpty(
            "Memoize is served in process: the cache scheme stays untouched (decisions #5-C / #11-B)");
    }

    [Fact]
    public async Task Budget_AccumulatesInCostBudgetProps_AndStopsTheNextRun()
    {
        var conversationId = $"budget-{Guid.NewGuid():N}";
        var enforcer = new StoreBudgetEnforcer(new RedbCostBudgetStore(_fx.RouteContext));
        var headers = new Dictionary<string, object?> { [LlmHeaders.ConversationId] = conversationId };

        // Run 1: one iteration costs 500 input tokens against a 100-token ceiling. The post-check stops
        // the loop — after the usage has been recorded.
        var firstProvider = new FakeProvider().EnqueueText("first answer", tokensIn: 500, tokensOut: 5);

        await using (var host = LiveLlmHost.Build(budget: enforcer, redb: _fx.Redb)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = firstProvider }))
        {
            await host.StartAsync(r => r.From("direct:agent")
                .To(LlmDsl.Factory("demo").ConversationFromHeader().Budget(inputTokens: 100).AsUri())
                .To("mock:done"));

            await host.SendAsync("direct:agent", "hello", headers);
        }

        firstProvider.CallCount.Should().Be(1);
        (await ReadBudgetAsync(conversationId)).InputTokens.Should().Be(500,
            "the run's usage is accumulated per conversation in CostBudgetProps");

        // Run 2, same conversation, nothing scripted: a provider call would be visible in CallCount.
        var secondProvider = new FakeProvider();

        await using (var host = LiveLlmHost.Build(budget: enforcer, redb: _fx.Redb)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = secondProvider }))
        {
            await host.StartAsync(r => r.From("direct:agent")
                .To(LlmDsl.Factory("demo").ConversationFromHeader().Budget(inputTokens: 100).AsUri())
                .To("mock:done"));

            await host.SendAsync("direct:agent", "hello again", headers);
        }

        secondProvider.CallCount.Should().Be(0,
            "the ceiling is checked against the conversation's accumulated usage, so the run stops before spending");
        (await ReadBudgetAsync(conversationId)).InputTokens.Should().Be(500,
            "a run stopped by the pre-check adds nothing");
    }

    [Fact]
    public async Task AuditRow_RecordsTheClaimsDenial()
    {
        var toolName = $"denied_{Guid.NewGuid():N}"[..20];
        var provider = new FakeProvider()
            .EnqueueToolUse(toolName, Input, "tu_1")
            .EnqueueText("done");
        var tool = new EchoToolRoute(toolName, "Look up a fact.", Schema, """{"answer":"42"}""",
            requiredClaims: ["orders:read"]);

        // The exchange carries no principal, so the claims requirement cannot be verified — and the
        // denial must be visible in the audit rows, not only in the run's result.
        await using var host = LiveLlmHost.Build(
                observer: new RedbAuditObserver(_fx.RouteContext),
                claimsSource: new ExchangePrincipalClaimsSource(),
                redb: _fx.Redb)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools(toolName).MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Call the tool.");

        tool.CapturedInputs.Should().BeEmpty();
        var rows = await ReadAuditAsync(toolName);
        rows.Should().ContainSingle("a denied call is an auditable event");
        rows[0].Outcome.Should().Be("skipped");
        rows[0].SkipReason.Should().StartWith(ToolSkipReasons.ClaimsMissing,
            "the audit row carries the machine-readable reason, including which claims were missing");
    }

    [Fact]
    public async Task AuditRows_RecordASuccessAndACacheHit()
    {
        var toolName = $"cached_{Guid.NewGuid():N}"[..20];
        var provider = new FakeProvider()
            .EnqueueToolUse(toolName, Input, "tu_1")
            .EnqueueToolUse(toolName, Input, "tu_2")
            .EnqueueText("done");
        var tool = new EchoToolRoute(toolName, "Look up a fact.", Schema, """{"answer":"42"}""",
            caching: ToolCachingPolicy.Memoize);

        await using var host = LiveLlmHost.Build(
                observer: new RedbAuditObserver(_fx.RouteContext), redb: _fx.Redb)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").Tools(toolName).MaxIterations(4).AsUri())
            .To("mock:done"));

        await host.SendAsync("direct:agent", "Call the tool twice.");

        var rows = await ReadAuditAsync(toolName);
        rows.Should().HaveCount(2, "both calls are audited: the executed one and the cache hit");
        rows.Should().Contain(r => r.Outcome == "success");
        rows.Should().Contain(r => r.Outcome == "skipped"
            && r.SkipReason == ToolSkipReasons.CacheHit);
    }

    /// <summary>
    /// The third skip reason, audited. The idempotency store itself is a double here (the redb-backed one
    /// needs an <c>IIdempotentRepository</c> the fixture does not register), but the row under test — the
    /// one the audit observer writes — is real.
    /// </summary>
    [Fact]
    public async Task AuditRow_RecordsAnIdempotencyHit()
    {
        var toolName = $"mut_{Guid.NewGuid():N}"[..20];
        var conversationId = $"idem-{Guid.NewGuid():N}";
        var provider = new FakeProvider()
            .EnqueueToolUse(toolName, Input, "tu_replay")
            .EnqueueText("done")
            .EnqueueToolUse(toolName, Input, "tu_replay")
            .EnqueueText("done");
        var tool = new EchoToolRoute(toolName, "Mutate something.", Schema, """{"ok":true}""",
            sideEffect: ToolSideEffect.Mutating);

        await using var host = LiveLlmHost.Build(
                observer: new RedbAuditObserver(_fx.RouteContext),
                idempotency: new SpyIdempotencyStore(),
                redb: _fx.Redb)
            .AddFactory("demo", new LlmConnectionFactory { Provider = "stub", PrebuiltProvider = provider });

        await host.StartAsync(tool, r => r.From("direct:agent")
            .To(LlmDsl.Factory("demo").ConversationFromHeader().Tools(toolName).MaxIterations(4).AsUri())
            .To("mock:done"));

        var headers = new Dictionary<string, object?> { [LlmHeaders.ConversationId] = conversationId };
        await host.SendAsync("direct:agent", "Mutate.", headers);
        await host.SendAsync("direct:agent", "Mutate.", headers);

        var rows = await ReadAuditAsync(toolName);
        rows.Should().Contain(r => r.Outcome == "skipped" && r.SkipReason == ToolSkipReasons.IdempotencyHit,
            "the replay the idempotency store answered is audited under its own reason");
    }
}

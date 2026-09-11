using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Providers;
using redb.Route.Llm.Storage.Redb;
using redb.Route.Llm.Storage.Redb.Schemas;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>
/// Race barriers of the redb-backed LLM stores: every business key also rides in
/// <c>_objects._value_unique</c>, so concurrent creators of one key converge on a
/// single row instead of splitting it (STORAGE_UNIQUE_KEYS_PLAN.md).
/// <para>
/// Deterministic tests replay the TOCTOU window synthetically: a "winner" row is
/// pre-created holding the unique key under a DIFFERENT <c>value_string</c>, so the
/// store's lookup misses it and the insert hits the unique index — exactly what the
/// loser of a real race sees. Parallel tests then drive real races. All keys here
/// are short, so the stored unique key equals the raw key verbatim.
/// </para>
/// </summary>
[Collection("StoragePro")]
public sealed class UniqueRaceBarrierTests
{
    private readonly StorageProFixture _fx;

    public UniqueRaceBarrierTests(StorageProFixture fx) => _fx = fx;

    // ── helpers ─────────────────────────────────────────────────────

    /// <summary>All rows of the scheme reachable by the key — via value_string or the unique key.</summary>
    private async Task<List<long>> KeyRowIdsAsync<TProps>(string key) where TProps : class, new()
    {
        var byString = await _fx.Redb.Query<TProps>().WhereRedb(o => o.ValueString == key).ToListAsync();
        var byUnique = await _fx.Redb.Query<TProps>().WhereRedb(o => o.ValueUnique == key).ToListAsync();
        return byString.Select(o => o.id).Concat(byUnique.Select(o => o.id)).Distinct().ToList();
    }

    /// <summary>
    /// Runs an action against a store wired to its own DI scope — parallel callers must
    /// not share one <see cref="IRedbService"/> (one connection per context).
    /// </summary>
    private async Task WithScopedContextAsync(Func<IRouteContext, Task> action)
    {
        using var scope = _fx.ScopeFactory.CreateScope();
        var ctx = new RouteContext();
        ctx.SetServiceProvider(scope.ServiceProvider);
        ctx.AddService(typeof(IRedbService), scope.ServiceProvider.GetRequiredService<IRedbService>());
        await action(ctx);
    }

    private static ConversationMessageMeta Meta(int iter) => new()
    {
        CreatedAtUtc = DateTime.UtcNow,
        Iteration = iter,
        Usage = new LlmUsage(10, 5)
    };

    // ── deterministic TOCTOU replays ────────────────────────────────

    [Fact]
    public async Task ConversationRoot_CreateRace_LoserAdoptsWinnerRoot()
    {
        var convId = $"race-conv-{Guid.NewGuid():N}";
        var winner = new RedbObject<ConversationProps>
        {
            value_string = $"winner-{convId}",
            ValueUnique = convId,
            Props = new ConversationProps { Status = "active", StartedAtUtc = DateTimeOffset.UtcNow, LastActivityAtUtc = DateTimeOffset.UtcNow }
        };
        var winnerId = await _fx.Redb.SaveAsync(winner);

        var store = new RedbConversationStore(_fx.RouteContext);
        await store.AppendAsync(convId, null, LlmMessage.User("hi"), Meta(0));

        (await KeyRowIdsAsync<ConversationProps>(convId)).Should().Equal([winnerId],
            "the loser of the root-creation race must adopt the winner's root, not mint a second one");

        var messages = await _fx.Redb.Query<MessageProps>()
            .WhereRedb(o => o.ValueLong == winnerId)
            .ToListAsync();
        messages.Should().ContainSingle("the appended message must land under the winner's root");
    }

    [Fact]
    public async Task CostBudget_FirstWriteRace_IncrementsWinnerRow()
    {
        var convId = $"race-budget-{Guid.NewGuid():N}";
        var winner = new RedbObject<CostBudgetProps>
        {
            value_string = $"winner-{convId}",
            ValueUnique = convId,
            Props = new CostBudgetProps { InputTokens = 1, OutputTokens = 1, CostUsd = 0.001m, UpdatedAtUtc = DateTimeOffset.UtcNow }
        };
        var winnerId = await _fx.Redb.SaveAsync(winner);

        var store = new RedbCostBudgetStore(_fx.RouteContext);
        var updated = await store.AddAsync(convId, new AgentUsage(100, 50, 0.01m));

        updated.InputTokens.Should().Be(101, "the loser must increment the winner's row, not start a second budget");
        updated.OutputTokens.Should().Be(51);
        updated.CostUsd.Should().Be(0.011m);

        (await KeyRowIdsAsync<CostBudgetProps>(convId)).Should().Equal([winnerId]);
    }

    [Fact]
    public async Task PromptTemplate_RegisterRace_FirstWins()
    {
        var name = $"race-tpl-{Guid.NewGuid():N}";
        var key = $"{name}@1.0";
        var winner = new RedbObject<PromptTemplateProps>
        {
            value_string = $"winner-{key}",
            ValueUnique = key,
            Props = new PromptTemplateProps { Name = name, Version = "1.0", Body = "WINNER", CreatedAtUtc = DateTimeOffset.UtcNow }
        };
        var winnerId = await _fx.Redb.SaveAsync(winner);

        var store = new RedbPromptTemplateRegistry(_fx.RouteContext);
        await store.SetAsync(new PromptTemplate { Name = name, Version = "1.0", Body = "LOSER" });

        var ids = await KeyRowIdsAsync<PromptTemplateProps>(key);
        ids.Should().Equal([winnerId], "a version is immutable provenance — the loser's body is dropped");

        var row = await _fx.Redb.LoadAsync<PromptTemplateProps>(winnerId);
        row!.Props.Body.Should().Be("WINNER");
    }

    [Fact]
    public async Task ToolCache_SetRace_WritesOntoWinnerRow()
    {
        var key = $"race-cache-{Guid.NewGuid():N}";
        var winner = new RedbObject<ToolCacheProps>
        {
            value_string = $"winner-{key}",
            ValueUnique = key,
            Props = new ToolCacheProps { OutputJson = """{"v":"old"}""", CreatedAtUtc = DateTimeOffset.UtcNow }
        };
        var winnerId = await _fx.Redb.SaveAsync(winner);

        var store = new RedbToolResultCache(_fx.RouteContext);
        await store.SetAsync(key, """{"v":"new"}""");

        (await KeyRowIdsAsync<ToolCacheProps>(key)).Should().Equal([winnerId]);

        var row = await _fx.Redb.LoadAsync<ToolCacheProps>(winnerId);
        row!.Props.OutputJson.Should().Be("""{"v":"new"}""", "a cache Set is last-writer-wins onto the single row");
    }

    [Fact]
    public async Task Knowledge_UpsertRace_WritesOntoWinnerRow()
    {
        var chunkId = $"race-chunk-{Guid.NewGuid():N}";
        var winner = new RedbObject<KnowledgeChunkProps>
        {
            value_string = $"winner-{chunkId}",
            ValueUnique = chunkId,
            note = """{"text":"old"}""",
            Props = new KnowledgeChunkProps()
        };
        var winnerId = await _fx.Redb.SaveAsync(winner);

        var store = new RedbKnowledgeStore(_fx.RouteContext);
        await store.UpsertAsync(new KnowledgeChunk { Id = chunkId, Text = "new text", Embedding = new[] { 1f, 0f } });

        (await KeyRowIdsAsync<KnowledgeChunkProps>(chunkId)).Should().Equal([winnerId],
            "an upsert is last-writer-wins onto the winner's row, never a duplicate chunk");

        var row = await _fx.Redb.LoadAsync<KnowledgeChunkProps>(winnerId);
        row!.note.Should().Contain("new text");
    }

    [Fact]
    public async Task Knowledge_UpsertManyRace_RetriesOntoWinnerRow()
    {
        var freshId = $"race-bulk-a-{Guid.NewGuid():N}";
        var collidedId = $"race-bulk-b-{Guid.NewGuid():N}";
        var winner = new RedbObject<KnowledgeChunkProps>
        {
            value_string = $"winner-{collidedId}",
            ValueUnique = collidedId,
            note = """{"text":"old"}""",
            Props = new KnowledgeChunkProps()
        };
        var winnerId = await _fx.Redb.SaveAsync(winner);

        var store = new RedbKnowledgeStore(_fx.RouteContext);
        await store.UpsertManyAsync(
        [
            new KnowledgeChunk { Id = freshId, Text = "fresh", Embedding = new[] { 1f, 0f } },
            new KnowledgeChunk { Id = collidedId, Text = "collided", Embedding = new[] { 0f, 1f } }
        ]);

        (await KeyRowIdsAsync<KnowledgeChunkProps>(freshId)).Should().ContainSingle();
        (await KeyRowIdsAsync<KnowledgeChunkProps>(collidedId)).Should().Equal([winnerId],
            "the bulk retry must resolve the collided id onto the winner's row");

        var row = await _fx.Redb.LoadAsync<KnowledgeChunkProps>(winnerId);
        row!.note.Should().Contain("collided");
    }

    [Fact]
    public async Task Batch_RegisterRace_WritesOntoWinnerRow()
    {
        var batchId = $"race-batch-{Guid.NewGuid():N}";
        var winner = new RedbObject<LlmBatchProps>
        {
            value_string = $"winner-{batchId}",
            ValueUnique = batchId,
            Props = new LlmBatchProps { ProviderId = "anthropic", Status = "submitted", SubmittedAtUtc = DateTimeOffset.UtcNow }
        };
        var winnerId = await _fx.Redb.SaveAsync(winner);

        var store = new RedbBatchStore(_fx.RouteContext);
        await store.RegisterAsync(new BatchJobRecord { BatchId = batchId, ProviderId = "anthropic", Status = "running" });

        (await KeyRowIdsAsync<LlmBatchProps>(batchId)).Should().Equal([winnerId]);

        var row = await _fx.Redb.LoadAsync<LlmBatchProps>(winnerId);
        row!.Props.Status.Should().Be("running", "the loser re-registers onto the winner's row");
    }

    [Fact]
    public async Task EvalRun_SaveRace_WritesOntoWinnerRow()
    {
        var runId = $"race-eval-{Guid.NewGuid():N}";
        var winner = new RedbObject<EvalRunProps>
        {
            value_string = $"winner-{runId}",
            ValueUnique = runId,
            Props = new EvalRunProps { RunId = runId, Scenario = "s", AgentFingerprint = "old", CreatedAtUtc = DateTimeOffset.UtcNow }
        };
        var winnerId = await _fx.Redb.SaveAsync(winner);

        var store = new RedbEvalRunStore(_fx.RouteContext);
        await store.SaveAsync(new EvalRunRecord { RunId = runId, Scenario = "s", AgentFingerprint = "new" });

        (await KeyRowIdsAsync<EvalRunProps>(runId)).Should().Equal([winnerId]);

        var row = await _fx.Redb.LoadAsync<EvalRunProps>(winnerId);
        row!.Props.AgentFingerprint.Should().Be("new");
    }

    [Fact]
    public async Task ToolIdempotency_CompleteRace_WritesOntoWinnerRow()
    {
        var convId = $"race-idem-{Guid.NewGuid():N}";
        const string toolUseId = "tu-1";
        var key = $"llm-tool:{convId}:{toolUseId}";
        var winner = new RedbObject<ToolIdempotencyProps>
        {
            value_string = $"winner-{key}",
            ValueUnique = key,
            Props = new ToolIdempotencyProps { OutputJson = """{"v":"old"}""", CreatedAtUtc = DateTimeOffset.UtcNow }
        };
        var winnerId = await _fx.Redb.SaveAsync(winner);

        var store = new RedbToolIdempotencyStore(Substitute.For<IIdempotentRepository>(), _fx.RouteContext);
        await store.CompleteAsync(convId, toolUseId, """{"v":"new"}""");

        (await KeyRowIdsAsync<ToolIdempotencyProps>(key)).Should().Equal([winnerId]);

        var row = await _fx.Redb.LoadAsync<ToolIdempotencyProps>(winnerId);
        row!.Props.OutputJson.Should().Be("""{"v":"new"}""");
    }

    [Fact]
    public async Task Approval_DuplicateRecord_FirstWins()
    {
        var approvalId = $"race-appr-{Guid.NewGuid():N}";
        var request = new ApprovalRequest
        {
            ConversationId = $"c-{Guid.NewGuid():N}",
            ToolUseId = "tu-1",
            InputJson = """{"id":42}""",
            Exchange = Substitute.For<IExchange>(),
            Tool = new LlmToolCapability { Name = "delete_user", Description = "test tool", InputSchema = "{}" }
        };

        var store = new RedbApprovalStore(_fx.RouteContext);
        await store.RecordAsync(request, ApprovalDecision.Approve(approvalId));
        await store.RecordAsync(request, new ApprovalDecision { Approved = false, ApprovalId = approvalId, Reason = "dup" });

        (await KeyRowIdsAsync<ApprovalProps>(approvalId)).Should().ContainSingle(
            "one approval id resolves to exactly one recorded decision — first wins");

        var found = await store.FindAsync(approvalId);
        found!.Approved.Should().BeTrue("the first recorded decision stands");
    }

    // ── real parallel races ─────────────────────────────────────────

    [Fact]
    public async Task ConversationRoot_ParallelAppends_ConvergeOnSingleRoot()
    {
        var convId = $"race-par-conv-{Guid.NewGuid():N}";

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => WithScopedContextAsync(async ctx =>
        {
            var store = new RedbConversationStore(ctx);
            await store.AppendAsync(convId, null, LlmMessage.User($"m{i}"), Meta(i));
        })));

        var roots = await _fx.Redb.Query<ConversationProps>()
            .WhereRedb(o => o.ValueString == convId)
            .ToListAsync();
        roots.Should().ContainSingle("all concurrent appends must converge on one conversation root");

        var messages = await _fx.Redb.Query<MessageProps>()
            .WhereRedb(o => o.ValueLong == roots[0].id)
            .ToListAsync();
        messages.Should().HaveCount(8, "no message may land under a split-brain root");

        var store = new RedbConversationStore(_fx.RouteContext);
        (await store.LoadTreeAsync(convId)).Should().HaveCount(8);
    }

    [Fact]
    public async Task CostBudget_ParallelFirstWrites_SingleRowFullSum()
    {
        var convId = $"race-par-budget-{Guid.NewGuid():N}";

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => WithScopedContextAsync(async ctx =>
        {
            var store = new RedbCostBudgetStore(ctx);
            await store.AddAsync(convId, new AgentUsage(10, 5, 0.001m));
        })));

        (await KeyRowIdsAsync<CostBudgetProps>(convId)).Should().ContainSingle(
            "concurrent first writes must not split the budget across rows");

        var usage = await new RedbCostBudgetStore(_fx.RouteContext).GetUsageAsync(convId);
        usage.InputTokens.Should().Be(80, "every increment must survive — no lost updates");
        usage.OutputTokens.Should().Be(40);
        usage.CostUsd.Should().Be(0.008m);
    }

    [Fact]
    public async Task ToolCache_ParallelSets_SingleRow()
    {
        var key = $"race-par-cache-{Guid.NewGuid():N}";

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => WithScopedContextAsync(async ctx =>
        {
            var store = new RedbToolResultCache(ctx);
            await store.SetAsync(key, $$"""{"v":{{i}}}""");
        })));

        (await KeyRowIdsAsync<ToolCacheProps>(key)).Should().ContainSingle();

        var value = await new RedbToolResultCache(_fx.RouteContext).GetAsync(key);
        value.Should().MatchRegex("""^\{"v":[0-7]\}$""", "the surviving value is one writer's payload, intact");
    }

    // ── review wave: duplicate ids inside ONE bulk call ─────────────

    [Fact]
    public async Task Knowledge_UpsertMany_DuplicateIdsInOneCall_LastWins()
    {
        var chunkId = $"dup-chunk-{Guid.NewGuid():N}";

        var store = new RedbKnowledgeStore(_fx.RouteContext);
        await store.UpsertManyAsync(
        [
            new KnowledgeChunk { Id = chunkId, Text = "first", Embedding = new[] { 1f, 0f } },
            new KnowledgeChunk { Id = chunkId, Text = "second", Embedding = new[] { 0f, 1f } }
        ]);

        var rows = await _fx.Redb.Query<KnowledgeChunkProps>()
            .WhereRedb(o => o.ValueString == chunkId)
            .ToListAsync();
        rows.Should().ContainSingle("a duplicated id within one call must collapse to one row, not crash the batch");
        rows[0].note.Should().Contain("second", "the last entry wins deterministically");
    }

    [Fact]
    public async Task Batch_RegisterMany_DuplicateIdsInOneCall_LastWins()
    {
        var batchId = $"dup-batch-{Guid.NewGuid():N}";

        var store = new RedbBatchStore(_fx.RouteContext);
        await store.RegisterManyAsync(
        [
            new BatchJobRecord { BatchId = batchId, ProviderId = "anthropic", Status = "submitted" },
            new BatchJobRecord { BatchId = batchId, ProviderId = "anthropic", Status = "running" }
        ]);

        var rows = await _fx.Redb.Query<LlmBatchProps>()
            .WhereRedb(o => o.ValueString == batchId)
            .ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Props.Status.Should().Be("running", "the last entry wins deterministically");
    }

    [Fact]
    public async Task EvalRun_SaveMany_DuplicateIdsInOneCall_LastWins()
    {
        var runId = $"dup-eval-{Guid.NewGuid():N}";

        var store = new RedbEvalRunStore(_fx.RouteContext);
        await store.SaveManyAsync(
        [
            new EvalRunRecord { RunId = runId, Scenario = "s", AgentFingerprint = "first" },
            new EvalRunRecord { RunId = runId, Scenario = "s", AgentFingerprint = "second" }
        ]);

        var rows = await _fx.Redb.Query<EvalRunProps>()
            .WhereRedb(o => o.ValueString == runId)
            .ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Props.AgentFingerprint.Should().Be("second", "the last entry wins deterministically");
    }

    // ── review wave: barrier recovery inside a caller-owned transaction ──

    [Fact]
    public async Task ToolCache_SetRace_InsideCallerTransaction_RecoversAndTransactionSurvives()
    {
        // Pins the contract closed by the core after docs/BUG_REDB_CORE_CACHE_AND_AMBIENT_TX.md
        // п.2: a save under a caller's transaction runs under a SAVEPOINT, so the loser's
        // unique violation no longer aborts that transaction (25P02 on PostgreSQL before
        // the core fix) — the catch resolves the winner and later writes in the SAME
        // transaction still commit. This is what makes the barrier legal in .Transacted()
        // routes.
        var key = $"race-tx-{Guid.NewGuid():N}";
        var sentinelKey = $"race-tx-sentinel-{Guid.NewGuid():N}";
        var winner = new RedbObject<ToolCacheProps>
        {
            value_string = $"winner-{key}",
            ValueUnique = key,
            Props = new ToolCacheProps { OutputJson = """{"v":"old"}""", CreatedAtUtc = DateTimeOffset.UtcNow }
        };
        var winnerId = await _fx.Redb.SaveAsync(winner);

        await _fx.Redb.Context.ExecuteAtomicAsync(async () =>
        {
            var store = new RedbToolResultCache(_fx.RouteContext);
            await store.SetAsync(key, """{"v":"new"}""");

            // The transaction must still be alive after the recovered violation.
            await _fx.Redb.SaveAsync(new RedbObject<ToolCacheProps>
            {
                value_string = sentinelKey,
                ValueUnique = sentinelKey,
                Props = new ToolCacheProps { OutputJson = """{"v":"sentinel"}""", CreatedAtUtc = DateTimeOffset.UtcNow }
            });
        });

        (await KeyRowIdsAsync<ToolCacheProps>(key)).Should().Equal([winnerId]);
        var row = await _fx.Redb.LoadAsync<ToolCacheProps>(winnerId);
        row!.Props.OutputJson.Should().Be("""{"v":"new"}""", "the loser recovered onto the winner inside the caller's transaction");

        (await KeyRowIdsAsync<ToolCacheProps>(sentinelKey)).Should().ContainSingle(
            "a write issued after the recovered violation must have committed — the transaction survived");
    }

    // ── review wave: terminal status survives a raced re-register ──

    [Fact]
    public async Task Batch_RegisterRace_DoesNotDowngradeTerminalStatus()
    {
        var batchId = $"race-term-{Guid.NewGuid():N}";
        var completedAt = DateTimeOffset.UtcNow;
        var winner = new RedbObject<LlmBatchProps>
        {
            value_string = $"winner-{batchId}",
            ValueUnique = batchId,
            Props = new LlmBatchProps
            {
                ProviderId = "anthropic",
                Status = "completed",
                SubmittedAtUtc = completedAt.AddMinutes(-1),
                CompletedAtUtc = completedAt
            }
        };
        var winnerId = await _fx.Redb.SaveAsync(winner);

        // A redelivered submit races the webhook's completion and loses the insert:
        // its re-register must NOT drag the finished batch back to "submitted".
        var store = new RedbBatchStore(_fx.RouteContext);
        await store.RegisterAsync(new BatchJobRecord { BatchId = batchId, ProviderId = "anthropic", Status = "submitted" });

        var row = await _fx.Redb.LoadAsync<LlmBatchProps>(winnerId);
        row!.Props.Status.Should().Be("completed", "a terminal status is never regressed by a raced re-register");
        row.Props.CompletedAtUtc.Should().NotBeNull();
    }
}

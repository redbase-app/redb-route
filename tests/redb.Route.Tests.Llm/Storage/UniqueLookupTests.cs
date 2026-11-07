using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Core.Models.Entities;
using redb.Route.Abstractions;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Providers;
using redb.Route.Llm.Storage.Redb;
using redb.Route.Llm.Storage.Redb.Schemas;
using redb.Route.RedbCore;

namespace redb.Route.Tests.Llm.Storage;

/// <summary>
/// Lookups read the unique key, not <c>value_string</c> (STORAGE_UNIQUE_KEYS_PLAN.md, Ф4 in its
/// no-migration form, owner decision 2026-09-10). Level A made every write carry
/// <c>ValueUnique = RedbUniqueKey.Normalize(key)</c>; the reads still searched
/// <c>value_string</c> — an unindexed scan on MSSQL, and a different column than the one the
/// barrier converges on.
/// <para>
/// Two behaviors per store, both through the public API. <b>A:</b> a row whose unique key matches
/// but whose <c>value_string</c> diverged (what the loser of a race adopts) must be FOUND — red on
/// the old lookups. <b>B:</b> a legacy row carrying the key only in <c>value_string</c>, no unique
/// value, is deliberately INVISIBLE — the owner declined every form of migration, including lazy
/// adoption, so this pins the accepted consequence rather than an accident. The conversation store
/// has only B: its create-barrier converges the A case to the same outcome either way.
/// </para>
/// </summary>
[Collection("StoragePro")]
public sealed class UniqueLookupTests
{
    private readonly StorageProFixture _fx;

    public UniqueLookupTests(StorageProFixture fx) => _fx = fx;

    // ── Approval ──

    [Fact]
    public async Task Approval_IsFoundByItsUniqueKey_WhenValueStringDiverged()
    {
        var id = $"ul-appr-{Guid.NewGuid():N}";
        await _fx.Redb.SaveAsync(new RedbObject<ApprovalProps>
        {
            value_string = $"diverged-{id}",
            ValueUnique = id,
            Props = new ApprovalProps { ToolName = "t", InputJson = "{}", DecidedAtUtc = DateTimeOffset.UtcNow }
        });

        var found = await new RedbApprovalStore(_fx.RouteContext).FindAsync(id);

        found.Should().NotBeNull("the unique key is the truth the write barrier converges on");
    }

    [Fact]
    public async Task Approval_LegacyRowWithoutUniqueKey_IsInvisible()
    {
        var id = $"ul-appr-legacy-{Guid.NewGuid():N}";
        await _fx.Redb.SaveAsync(new RedbObject<ApprovalProps>
        {
            value_string = id,
            Props = new ApprovalProps { ToolName = "t", InputJson = "{}", DecidedAtUtc = DateTimeOffset.UtcNow }
        });

        var found = await new RedbApprovalStore(_fx.RouteContext).FindAsync(id);

        found.Should().BeNull("no migration in any form is the owner's decision, this pins it");
    }

    // ── Batch ──

    [Fact]
    public async Task Batch_IsFoundByItsUniqueKey_WhenValueStringDiverged()
    {
        var id = $"ul-batch-{Guid.NewGuid():N}";
        await _fx.Redb.SaveAsync(new RedbObject<LlmBatchProps>
        {
            value_string = $"diverged-{id}",
            ValueUnique = id,
            Props = new LlmBatchProps { ProviderId = "anthropic", Status = "submitted", SubmittedAtUtc = DateTimeOffset.UtcNow }
        });

        var found = await new RedbBatchStore(_fx.RouteContext).GetAsync(id);

        found.Should().NotBeNull();
    }

    [Fact]
    public async Task Batch_LegacyRowWithoutUniqueKey_IsInvisible()
    {
        var id = $"ul-batch-legacy-{Guid.NewGuid():N}";
        await _fx.Redb.SaveAsync(new RedbObject<LlmBatchProps>
        {
            value_string = id,
            Props = new LlmBatchProps { ProviderId = "anthropic", Status = "submitted", SubmittedAtUtc = DateTimeOffset.UtcNow }
        });

        (await new RedbBatchStore(_fx.RouteContext).GetAsync(id)).Should().BeNull();
    }

    // ── EvalRun ──

    [Fact]
    public async Task EvalRun_IsFoundByItsUniqueKey_WhenValueStringDiverged()
    {
        var id = $"ul-eval-{Guid.NewGuid():N}";
        await _fx.Redb.SaveAsync(new RedbObject<EvalRunProps>
        {
            value_string = $"diverged-{id}",
            ValueUnique = id,
            Props = new EvalRunProps { RunId = id, Scenario = "s", AgentFingerprint = "fp", CreatedAtUtc = DateTimeOffset.UtcNow }
        });

        (await new RedbEvalRunStore(_fx.RouteContext).GetAsync(id)).Should().NotBeNull();
    }

    [Fact]
    public async Task EvalRun_LegacyRowWithoutUniqueKey_IsInvisible()
    {
        var id = $"ul-eval-legacy-{Guid.NewGuid():N}";
        await _fx.Redb.SaveAsync(new RedbObject<EvalRunProps>
        {
            value_string = id,
            Props = new EvalRunProps { RunId = id, Scenario = "s", AgentFingerprint = "fp", CreatedAtUtc = DateTimeOffset.UtcNow }
        });

        (await new RedbEvalRunStore(_fx.RouteContext).GetAsync(id)).Should().BeNull();
    }

    // ── Prompt templates ──

    [Fact]
    public async Task Template_IsFoundByItsUniqueKey_WhenValueStringDiverged()
    {
        var name = $"ul-tpl-{Guid.NewGuid():N}";
        var key = $"{name}@1.0";
        await _fx.Redb.SaveAsync(new RedbObject<PromptTemplateProps>
        {
            value_string = $"diverged-{key}",
            ValueUnique = key,
            Props = new PromptTemplateProps { Name = name, Version = "1.0", Body = "B", CreatedAtUtc = DateTimeOffset.UtcNow }
        });

        var found = await new RedbPromptTemplateRegistry(_fx.RouteContext).GetAsync(name, "1.0");

        found.Should().NotBeNull();
    }

    [Fact]
    public async Task Template_LegacyRowWithoutUniqueKey_IsInvisible()
    {
        var name = $"ul-tpl-legacy-{Guid.NewGuid():N}";
        var key = $"{name}@1.0";
        await _fx.Redb.SaveAsync(new RedbObject<PromptTemplateProps>
        {
            value_string = key,
            Props = new PromptTemplateProps { Name = name, Version = "1.0", Body = "B", CreatedAtUtc = DateTimeOffset.UtcNow }
        });

        (await new RedbPromptTemplateRegistry(_fx.RouteContext).GetAsync(name, "1.0")).Should().BeNull();
    }

    // ── Tool result cache ──

    [Fact]
    public async Task Cache_IsFoundByItsUniqueKey_WhenValueStringDiverged()
    {
        var key = $"ul-cache-{Guid.NewGuid():N}";
        await _fx.Redb.SaveAsync(new RedbObject<ToolCacheProps>
        {
            value_string = $"diverged-{key}",
            ValueUnique = key,
            Props = new ToolCacheProps { OutputJson = """{"v":"x"}""", CreatedAtUtc = DateTimeOffset.UtcNow }
        });

        var hit = await new RedbToolResultCache(_fx.RouteContext).GetAsync(key);

        hit.Should().Be("""{"v":"x"}""");
    }

    [Fact]
    public async Task Cache_LegacyRowWithoutUniqueKey_IsInvisible()
    {
        var key = $"ul-cache-legacy-{Guid.NewGuid():N}";
        await _fx.Redb.SaveAsync(new RedbObject<ToolCacheProps>
        {
            value_string = key,
            Props = new ToolCacheProps { OutputJson = """{"v":"x"}""", CreatedAtUtc = DateTimeOffset.UtcNow }
        });

        (await new RedbToolResultCache(_fx.RouteContext).GetAsync(key)).Should().BeNull();
    }

    [Fact]
    public async Task Cache_LookupNormalizesTheKey_LikeTheWriteDoes()
    {
        // A key over the storage limit is truncated+hashed by RedbUniqueKey on write; the lookup
        // must run the same normalization or long keys written by the store become unfindable.
        var longKey = $"ul-cache-long-{Guid.NewGuid():N}-" + new string('x', 600);
        await _fx.Redb.SaveAsync(new RedbObject<ToolCacheProps>
        {
            value_string = "diverged-long",
            ValueUnique = RedbUniqueKey.Normalize(longKey),
            Props = new ToolCacheProps { OutputJson = """{"v":"long"}""", CreatedAtUtc = DateTimeOffset.UtcNow }
        });

        var hit = await new RedbToolResultCache(_fx.RouteContext).GetAsync(longKey);

        hit.Should().Be("""{"v":"long"}""");
    }

    // ── Knowledge ──

    [Fact]
    public async Task Knowledge_DeleteReachesTheRow_ByItsUniqueKey()
    {
        var chunkId = $"ul-know-{Guid.NewGuid():N}";
        await _fx.Redb.SaveAsync(new RedbObject<KnowledgeChunkProps>
        {
            value_string = $"diverged-{chunkId}",
            ValueUnique = chunkId,
            note = """{"text":"old"}"""
        });

        await new RedbKnowledgeStore(_fx.RouteContext).DeleteAsync(chunkId);

        var left = await _fx.Redb.Query<KnowledgeChunkProps>()
            .WhereRedb(o => o.ValueUnique == chunkId).ToListAsync();
        left.Should().BeEmpty("delete must find the row the write barrier owns");
    }

    [Fact]
    public async Task Knowledge_LegacyRowWithoutUniqueKey_IsInvisibleToDelete()
    {
        var chunkId = $"ul-know-legacy-{Guid.NewGuid():N}";
        await _fx.Redb.SaveAsync(new RedbObject<KnowledgeChunkProps>
        {
            value_string = chunkId,
            note = """{"text":"old"}"""
        });

        await new RedbKnowledgeStore(_fx.RouteContext).DeleteAsync(chunkId);

        var left = await _fx.Redb.Query<KnowledgeChunkProps>()
            .WhereRedb(o => o.ValueString == chunkId).ToListAsync();
        left.Should().HaveCount(1, "the legacy row stays as it lay — no migration, no adoption");
    }

    // ── Cost budget ──

    [Fact]
    public async Task Budget_IsReadByItsUniqueKey_WhenValueStringDiverged()
    {
        var convId = $"ul-budget-{Guid.NewGuid():N}";
        await _fx.Redb.SaveAsync(new RedbObject<CostBudgetProps>
        {
            value_string = $"diverged-{convId}",
            ValueUnique = convId,
            Props = new CostBudgetProps { InputTokens = 7, OutputTokens = 3, CostUsd = 0.01m, UpdatedAtUtc = DateTimeOffset.UtcNow }
        });

        var usage = await new RedbCostBudgetStore(_fx.RouteContext).GetUsageAsync(convId);

        usage.InputTokens.Should().Be(7);
        usage.OutputTokens.Should().Be(3);
    }

    [Fact]
    public async Task Budget_LegacyRowWithoutUniqueKey_ReadsAsZero()
    {
        var convId = $"ul-budget-legacy-{Guid.NewGuid():N}";
        await _fx.Redb.SaveAsync(new RedbObject<CostBudgetProps>
        {
            value_string = convId,
            Props = new CostBudgetProps { InputTokens = 9, OutputTokens = 9, CostUsd = 0.09m, UpdatedAtUtc = DateTimeOffset.UtcNow }
        });

        var usage = await new RedbCostBudgetStore(_fx.RouteContext).GetUsageAsync(convId);

        usage.InputTokens.Should().Be(0, "legacy budget rows are accepted as lost — owner decision");
    }

    // ── Tool idempotency ──

    [Fact]
    public async Task Idempotency_CompletedRowIsAHit_ByItsUniqueKey()
    {
        var convId = $"ul-idem-{Guid.NewGuid():N}";
        const string toolUseId = "tu-1";
        var key = $"llm-tool:{convId}:{toolUseId}";
        await _fx.Redb.SaveAsync(new RedbObject<ToolIdempotencyProps>
        {
            value_string = $"diverged-{key}",
            ValueUnique = key,
            Props = new ToolIdempotencyProps { OutputJson = """{"v":"done"}""", CreatedAtUtc = DateTimeOffset.UtcNow }
        });

        var reservation = await new RedbToolIdempotencyStore(Substitute.For<IIdempotentRepository>(), _fx.RouteContext).TryReserveAsync(convId, toolUseId);

        reservation.IsNew.Should().BeFalse("the completed row owns the unique key and must be seen");
    }

    [Fact]
    public async Task Idempotency_LegacyCompletedRow_IsInvisible()
    {
        var convId = $"ul-idem-legacy-{Guid.NewGuid():N}";
        const string toolUseId = "tu-1";
        var key = $"llm-tool:{convId}:{toolUseId}";
        await _fx.Redb.SaveAsync(new RedbObject<ToolIdempotencyProps>
        {
            value_string = key,
            Props = new ToolIdempotencyProps { OutputJson = """{"v":"done"}""", CreatedAtUtc = DateTimeOffset.UtcNow }
        });

        var repo = Substitute.For<IIdempotentRepository>();
        repo.Add(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var reservation = await new RedbToolIdempotencyStore(repo, _fx.RouteContext).TryReserveAsync(convId, toolUseId);

        reservation.IsNew.Should().BeTrue("the legacy row has no unique key and is deliberately unseen");
    }

    // ── Conversation root (B only: the create-barrier converges the A case either way) ──

    [Fact]
    public async Task ConversationRoot_LegacyRowWithoutUniqueKey_GetsAFreshRootBesideIt()
    {
        var convId = $"ul-conv-legacy-{Guid.NewGuid():N}";
        await _fx.Redb.SaveAsync(new RedbObject<ConversationProps>
        {
            value_string = convId,
            Props = new ConversationProps { Status = "active", StartedAtUtc = DateTimeOffset.UtcNow, LastActivityAtUtc = DateTimeOffset.UtcNow }
        });

        await new RedbConversationStore(_fx.RouteContext)
            .AppendAsync(convId, null, LlmMessage.User("hi"), new ConversationMessageMeta
            {
                CreatedAtUtc = DateTime.UtcNow,
                Iteration = 0,
                Usage = new LlmUsage(1, 1)
            });

        var byUnique = await _fx.Redb.Query<ConversationProps>()
            .WhereRedb(o => o.ValueUnique == convId).ToListAsync();
        var byString = await _fx.Redb.Query<ConversationProps>()
            .WhereRedb(o => o.ValueString == convId).ToListAsync();

        byUnique.Should().HaveCount(1, "the append minted a proper unique-keyed root");
        // Two rows carry the raw key in value_string now: the untouched legacy root and the fresh
        // root (which writes both columns). The legacy one is recognisable by its empty unique.
        byString.Should().HaveCount(2, "the legacy root stays as it lay, unadopted, beside the fresh one");
        var legacy = byString.Single(o => string.IsNullOrEmpty(o.value_unique));
        byUnique[0].id.Should().NotBe(legacy.id,
            "no lazy adoption: the legacy root is not the one the store now works under");
    }

    // ── Key-case collation: a real, live cross-provider divergence, pinned ──

    [Fact]
    public async Task KeyCase_CharacterizesTheProviderCollation()
    {
        // Checked live on the stend: MSSQL's _value_unique is SQL_Latin1_General_CP1_CI_AS, so
        // keys differing only by case are ONE key there — they find each other and collide on the
        // unique index. Postgres compares byte-wise, so they are TWO keys. Neither side is wrong;
        // what would be wrong is not knowing. This test documents the divergence per provider so a
        // schema change on either side (a COLLATE on the column, say) shows up here first.
        var stem = $"ul-case-{Guid.NewGuid():N}";
        var cache = new RedbToolResultCache(_fx.RouteContext);

        await cache.SetAsync(stem + "-KEY", """{"v":"upper"}""");
        var crossRead = await cache.GetAsync(stem + "-key");
        await cache.SetAsync(stem + "-key", """{"v":"lower"}""");
        var upperAfter = await cache.GetAsync(stem + "-KEY");

        if (_fx.Provider == "mssql")
        {
            crossRead.Should().Be("""{"v":"upper"}""", "CI collation: the lower-case probe finds the upper-case row");
            upperAfter.Should().Be("""{"v":"lower"}""", "CI collation: the second set lands on the same single row");
        }
        else
        {
            crossRead.Should().BeNull("byte-wise comparison: case-differing keys are distinct");
            upperAfter.Should().Be("""{"v":"upper"}""", "the lower-case set created its own row and left this one alone");
        }
    }
}

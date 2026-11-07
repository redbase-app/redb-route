using Microsoft.Extensions.DependencyInjection;
using redb.Core;
using redb.Core.Models.Contracts;
using redb.Core.Models.Entities;
using redb.Route.RedbCore.Models;
using redb.Route.RedbCore.Repositories;

namespace redb.Route.Tests.Core.Integration;

/// <summary>
/// Integration tests for <see cref="RedbIdempotentRepository"/> running against a real database.
/// Subclassed per provider (Postgres, MsSql).
/// Each test instance uses a unique prefix so multiple TFM runners can execute in parallel
/// without interfering (no global cleanup required).
/// </summary>
public abstract class RedbIdempotentRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly IRedbService _redb;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Unique prefix for this test instance — isolates data across parallel runs.</summary>
    private readonly string _id = Guid.NewGuid().ToString("N")[..8];

    protected RedbIdempotentRepositoryIntegrationTests(IRedbService redb, IServiceScopeFactory scopeFactory)
    {
        _redb = redb;
        _scopeFactory = scopeFactory;
    }

    private string P(string name) => $"{_id}-{name}";

    private RedbIdempotentRepository CreateRepository(string processor = "test-proc", TimeSpan? ttl = null)
    {
        var options = new RedbIdempotentOptions
        {
            ProcessorName = P(processor),
            Ttl = ttl
        };
        return new RedbIdempotentRepository(_scopeFactory, options);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Clean up only entries created by this test instance (matching our unique prefix).
        // StartsWith is supported by redb Pro LINQ provider → translates to LIKE 'prefix%'.
        try
        {
            var all = await _redb.Query<IdempotentEntryProps>()
                .Where(e => e.ProcessorName!.StartsWith(_id))
                .ToListAsync();
            if (all.Count > 0)
                await _redb.DeleteAsync((IEnumerable<IRedbObject>)all);
        }
        catch
        {
            // Parallel TFM runs may cause deadlocks during cleanup — safe to ignore
        }
    }

    // ─── Add ───────────────────────────────────────────────

    [Fact]
    public async Task Add_NewKey_ReturnsTrue()
    {
        var repo = CreateRepository();

        var result = await repo.Add("msg-1");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Add_DuplicateKey_ReturnsFalse()
    {
        var repo = CreateRepository();

        await repo.Add("msg-dup");
        var result = await repo.Add("msg-dup");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Add_SameKeyDifferentProcessor_ReturnsTrue()
    {
        var repo1 = CreateRepository("proc-A");
        var repo2 = CreateRepository("proc-B");

        await repo1.Add("shared-key");
        var result = await repo2.Add("shared-key");

        result.Should().BeTrue();
    }

    // ─── Contains ──────────────────────────────────────────

    [Fact]
    public async Task Contains_AfterAdd_ReturnsTrue()
    {
        var repo = CreateRepository();

        await repo.Add("msg-exists");

        (await repo.Contains("msg-exists")).Should().BeTrue();
    }

    [Fact]
    public async Task Contains_NonExistentKey_ReturnsFalse()
    {
        var repo = CreateRepository();

        (await repo.Contains("never-added")).Should().BeFalse();
    }

    // ─── Confirm ──────────────────────────────────────────

    [Fact]
    public async Task Confirm_SetsConfirmedFlag()
    {
        var repo = CreateRepository();

        await repo.Add("msg-confirm");
        await repo.Confirm("msg-confirm");

        // Verify via direct query — match by processor prefix to isolate
        var procName = P("test-proc");
        var items = await _redb.Query<IdempotentEntryProps>()
            .Where(e => e.MessageKey == "msg-confirm" && e.ProcessorName == procName)
            .ToListAsync();

        items.Should().ContainSingle()
            .Which.Props.Confirmed.Should().BeTrue();
    }

    // ─── Remove ──────────────────────────────────────────

    [Fact]
    public async Task Remove_DeletesEntry()
    {
        var repo = CreateRepository();

        await repo.Add("msg-remove");
        await repo.Remove("msg-remove");

        (await repo.Contains("msg-remove")).Should().BeFalse();
    }

    [Fact]
    public async Task Remove_NonExistentKey_DoesNotThrow()
    {
        var repo = CreateRepository();

        var act = () => repo.Remove("ghost");

        await act.Should().NotThrowAsync();
    }

    // ─── Clear ──────────────────────────────────────────

    [Fact]
    public async Task Clear_RemovesAllEntriesForProcessor()
    {
        var repo = CreateRepository("clear-proc");

        await repo.Add("c1");
        await repo.Add("c2");
        await repo.Add("c3");

        await repo.Clear();

        (await repo.Contains("c1")).Should().BeFalse();
        (await repo.Contains("c2")).Should().BeFalse();
        (await repo.Contains("c3")).Should().BeFalse();
    }

    [Fact]
    public async Task Clear_DoesNotAffectOtherProcessors()
    {
        var repo1 = CreateRepository("isolate-A");
        var repo2 = CreateRepository("isolate-B");

        await repo1.Add("x");
        await repo2.Add("y");

        await repo1.Clear();

        (await repo2.Contains("y")).Should().BeTrue();
    }

    // ─── Concurrency: one key, one winner ─────────────────

    [Fact]
    public async Task Add_ParallelSameKey_ExactlyOneWins_SingleRowStored()
    {
        var repo = CreateRepository("race-proc");
        // Prime the scheme outside the measured races so scheme sync doesn't add noise.
        await repo.Add("race-warmup");

        for (var i = 0; i < 10; i++)
        {
            var key = $"race-key-{i}";
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => repo.Add(key)));

            results.Count(r => r).Should().Be(1,
                $"exactly one of the concurrent Add('{key}') calls may claim the key");

            var entryName = RedbIdempotentRepository.ComposeEntryName(P("race-proc"), key);
            var rows = await _redb.Query<IdempotentEntryProps>()
                .WhereRedb(x => x.Name == entryName)
                .ToListAsync();
            rows.Should().ContainSingle(
                $"the unique index must let exactly one row through for '{key}'");
        }
    }

    [Fact]
    public async Task Add_UniqueKeyAlreadyCommittedByRaceWinner_ReturnsFalse()
    {
        // Deterministic replay of the TOCTOU window: the winner's row is already committed
        // with our unique key, but under a name the Add lookup does not see. The insert must
        // hit UIX__objects__scheme_unique and come back as a calm "duplicate" — no exception.
        var repo = CreateRepository("toctou-proc");
        await repo.Add("toctou-warmup");

        const string key = "toctou-key";
        var procName = P("toctou-proc");
        var entryName = RedbIdempotentRepository.ComposeEntryName(procName, key);

        var winner = new RedbObject<IdempotentEntryProps>
        {
            name = P("toctou-winner-row"),
            ValueUnique = entryName,
            Props = new IdempotentEntryProps
            {
                ProcessorName = procName,
                MessageKey = key,
                CreatedAt = DateTimeOffset.UtcNow,
                Confirmed = false
            }
        };
        await _redb.SaveAsync(winner);

        var result = await repo.Add(key);

        result.Should().BeFalse("the key is already claimed by the committed row");
    }

    // ─── Long client keys ─────────────────────────────────

    [Fact]
    public async Task Add_LongKey_NormalizedAndStillIdempotent()
    {
        var repo = CreateRepository("long-proc");

        var prefix = new string('k', 600);
        var key1 = prefix + "-first";
        var key2 = prefix + "-second";

        (await repo.Add(key1)).Should().BeTrue();
        (await repo.Add(key1)).Should().BeFalse("the same long key must be recognized as a duplicate");
        (await repo.Add(key2)).Should().BeTrue("a long key differing only past the readable prefix is distinct");

        (await repo.Contains(key1)).Should().BeTrue();
        await repo.Remove(key1);
        (await repo.Contains(key1)).Should().BeFalse();
        (await repo.Contains(key2)).Should().BeTrue();
    }

    // ─── TTL cleanup ───────────────────────────────────────

    [Fact]
    public async Task Add_WithTtl_CleansUpExpiredEntries()
    {
        // Create repo with short TTL
        var repo = CreateRepository("ttl-proc", ttl: TimeSpan.FromSeconds(2));

        // Add an entry that will become "old" after TTL elapses
        await repo.Add("old-msg");
        (await repo.Contains("old-msg")).Should().BeTrue();

        // Wait for TTL to expire
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Add a new key — this triggers cleanup of expired entries
        await repo.Add("new-msg");

        // Old entry should be cleaned up by TTL
        (await repo.Contains("old-msg")).Should().BeFalse();
        // New entry should exist
        (await repo.Contains("new-msg")).Should().BeTrue();
    }

    // ─── End-to-end workflow ──────────────────────────────

    [Fact]
    public async Task FullWorkflow_AddContainConfirmRemove()
    {
        var repo = CreateRepository("e2e-proc");

        // Add
        (await repo.Add("wf-1")).Should().BeTrue();

        // Contains
        (await repo.Contains("wf-1")).Should().BeTrue();

        // Duplicate
        (await repo.Add("wf-1")).Should().BeFalse();

        // Confirm
        await repo.Confirm("wf-1");
        var procName = P("e2e-proc");
        var items = await _redb.Query<IdempotentEntryProps>()
            .Where(e => e.MessageKey == "wf-1" && e.ProcessorName == procName)
            .ToListAsync();
        items.Should().ContainSingle().Which.Props.Confirmed.Should().BeTrue();

        // Remove
        await repo.Remove("wf-1");
        (await repo.Contains("wf-1")).Should().BeFalse();
    }

    [Fact]
    public async Task MultipleKeys_IndependentLifecycle()
    {
        var repo = CreateRepository("multi-proc");

        await repo.Add("k1");
        await repo.Add("k2");
        await repo.Add("k3");

        await repo.Remove("k2");

        (await repo.Contains("k1")).Should().BeTrue();
        (await repo.Contains("k2")).Should().BeFalse();
        (await repo.Contains("k3")).Should().BeTrue();
    }
}

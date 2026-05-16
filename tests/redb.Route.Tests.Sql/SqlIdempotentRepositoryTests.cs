using redb.Route.Sql.Connection;
using redb.Route.Sql.Repositories;

namespace redb.Route.Tests.Sql;

public class SqlIdempotentRepositoryTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    private SqlIdempotentRepository CreateRepo(string processorName = "test-route", bool createTable = true, TimeSpan? ttl = null)
    {
        var options = new SqlIdempotentOptions
        {
            ProcessorName = processorName,
            TableName = "redb_idempotent",
            CreateTable = createTable,
            Ttl = ttl
        };
        return new SqlIdempotentRepository(_db.CreateFactory(), options);
    }

    // ── Options defaults ────────────────────────────────────────────

    [Fact]
    public void SqlIdempotentOptions_Defaults()
    {
        var opts = new SqlIdempotentOptions();

        opts.DataSource.Should().BeEmpty();
        opts.TableName.Should().Be("redb_idempotent");
        opts.ProcessorName.Should().BeEmpty();
        opts.Ttl.Should().BeNull();
        opts.CreateTable.Should().BeTrue();
    }

    // ── Add ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Add_NewKey_ReturnsTrue()
    {
        var repo = CreateRepo();

        var result = await repo.Add("msg-1");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Add_DuplicateKey_ReturnsFalse()
    {
        var repo = CreateRepo();
        await repo.Add("msg-1");

        var result = await repo.Add("msg-1");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Add_DifferentKeys_AllReturnTrue()
    {
        var repo = CreateRepo();

        (await repo.Add("msg-1")).Should().BeTrue();
        (await repo.Add("msg-2")).Should().BeTrue();
        (await repo.Add("msg-3")).Should().BeTrue();
    }

    [Fact]
    public async Task Add_DifferentProcessors_IndependentKeys()
    {
        var repo1 = CreateRepo("route-1");
        var repo2 = CreateRepo("route-2");

        (await repo1.Add("msg-1")).Should().BeTrue();
        (await repo2.Add("msg-1")).Should().BeTrue(); // different processor → OK
    }

    // ── Contains ────────────────────────────────────────────────────

    [Fact]
    public async Task Contains_AfterAdd_ReturnsTrue()
    {
        var repo = CreateRepo();
        await repo.Add("msg-1");

        var result = await repo.Contains("msg-1");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_NonExistentKey_ReturnsFalse()
    {
        var repo = CreateRepo();

        var result = await repo.Contains("nonexistent");

        result.Should().BeFalse();
    }

    // ── Confirm ─────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_AfterAdd_NoError()
    {
        var repo = CreateRepo();
        await repo.Add("msg-1");

        await repo.Confirm("msg-1"); // should not throw

        (await repo.Contains("msg-1")).Should().BeTrue();
    }

    [Fact]
    public async Task Confirm_NonExistentKey_NoError()
    {
        var repo = CreateRepo();

        // Confirm on nonexistent key is a no-op
        await repo.Confirm("nonexistent");
    }

    // ── Remove ──────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_AfterAdd_KeyGone()
    {
        var repo = CreateRepo();
        await repo.Add("msg-1");

        await repo.Remove("msg-1");

        (await repo.Contains("msg-1")).Should().BeFalse();
    }

    [Fact]
    public async Task Remove_NonExistentKey_NoError()
    {
        var repo = CreateRepo();

        await repo.Remove("nonexistent"); // no throw
    }

    [Fact]
    public async Task Remove_AllowsReAdd()
    {
        var repo = CreateRepo();
        await repo.Add("msg-1");
        await repo.Remove("msg-1");

        var result = await repo.Add("msg-1");

        result.Should().BeTrue();
    }

    // ── Clear ───────────────────────────────────────────────────────

    [Fact]
    public async Task Clear_RemovesAllKeysForProcessor()
    {
        var repo = CreateRepo();
        await repo.Add("msg-1");
        await repo.Add("msg-2");
        await repo.Add("msg-3");

        await repo.Clear();

        (await repo.Contains("msg-1")).Should().BeFalse();
        (await repo.Contains("msg-2")).Should().BeFalse();
        (await repo.Contains("msg-3")).Should().BeFalse();
    }

    [Fact]
    public async Task Clear_DoesNotAffectOtherProcessors()
    {
        var repo1 = CreateRepo("route-1");
        var repo2 = CreateRepo("route-2");
        await repo1.Add("msg-1");
        await repo2.Add("msg-1");

        await repo1.Clear();

        (await repo1.Contains("msg-1")).Should().BeFalse();
        (await repo2.Contains("msg-1")).Should().BeTrue();
    }

    // ── Auto-create table ───────────────────────────────────────────

    [Fact]
    public async Task AutoCreateTable_CreatesOnFirstUse()
    {
        var repo = CreateRepo(createTable: true);

        // First call triggers table creation
        await repo.Add("msg-1");

        var rows = _db.Query("SELECT * FROM redb_idempotent");
        rows.Should().HaveCount(1);
    }

    // ── TTL cleanup ─────────────────────────────────────────────────

    [Fact]
    public async Task TtlCleanup_RemovesOldEntries()
    {
        // Create a repo with very short TTL
        var repo = CreateRepo(ttl: TimeSpan.FromMilliseconds(50));

        await repo.Add("old-msg");
        await Task.Delay(100); // Wait for TTL to expire

        // Next Add triggers cleanup
        await repo.Add("new-msg");

        (await repo.Contains("old-msg")).Should().BeFalse();
        (await repo.Contains("new-msg")).Should().BeTrue();
    }

    [Fact]
    public async Task TtlCleanup_PreservesRecentEntries()
    {
        var repo = CreateRepo(ttl: TimeSpan.FromSeconds(30));

        await repo.Add("recent-1");
        await repo.Add("recent-2");

        // Next Add triggers cleanup — nothing should be removed
        await repo.Add("recent-3");

        (await repo.Contains("recent-1")).Should().BeTrue();
        (await repo.Contains("recent-2")).Should().BeTrue();
        (await repo.Contains("recent-3")).Should().BeTrue();
    }

    [Fact]
    public async Task TtlCleanup_OnlyAffectsOwnProcessor()
    {
        var repoA = CreateRepo(processorName: "routeA", ttl: TimeSpan.FromMilliseconds(50));
        var repoB = CreateRepo(processorName: "routeB", ttl: TimeSpan.FromMilliseconds(50));

        await repoA.Add("shared-key");
        await repoB.Add("shared-key");

        await Task.Delay(100); // Both expire

        // repoA cleanup should not affect repoB
        await repoA.Add("trigger-cleanup-A");

        (await repoA.Contains("shared-key")).Should().BeFalse("routeA cleanup removed its own expired entry");
        (await repoB.Contains("shared-key")).Should().BeTrue("routeB's entry was not touched by routeA cleanup");
    }

    [Fact]
    public async Task TtlCleanup_NoTtl_NeverCleans()
    {
        var repo = CreateRepo(ttl: null); // No TTL

        await repo.Add("msg-1");
        await Task.Delay(50);
        await repo.Add("msg-2");

        // Without TTL, nothing is ever cleaned up
        (await repo.Contains("msg-1")).Should().BeTrue();
        (await repo.Contains("msg-2")).Should().BeTrue();
    }

    // ── Full lifecycle ──────────────────────────────────────────────

    [Fact]
    public async Task FullLifecycle_AddConfirmContains()
    {
        var repo = CreateRepo();

        // Step 1: Add (reserve)
        (await repo.Add("msg-1")).Should().BeTrue();
        (await repo.Contains("msg-1")).Should().BeTrue();

        // Step 2: Duplicate Add returns false
        (await repo.Add("msg-1")).Should().BeFalse();

        // Step 3: Confirm
        await repo.Confirm("msg-1");
        (await repo.Contains("msg-1")).Should().BeTrue();

        // Step 4: Remove
        await repo.Remove("msg-1");
        (await repo.Contains("msg-1")).Should().BeFalse();

        // Step 5: Re-add after remove
        (await repo.Add("msg-1")).Should().BeTrue();
    }

    // ── Constructor validation ──────────────────────────────────────

    [Fact]
    public void Constructor_NullFactory_Throws()
    {
        var act = () => new SqlIdempotentRepository(null!, new SqlIdempotentOptions());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        var act = () => new SqlIdempotentRepository(_db.CreateFactory(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Concurrent operations ───────────────────────────────────────

    [Fact]
    public async Task ConcurrentAdd_SameKey_OnlyOneSucceeds()
    {
        var repo = CreateRepo();
        var results = new System.Collections.Concurrent.ConcurrentBag<bool>();

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            Task.Run(async () =>
            {
                var result = await repo.Add("concurrent-key");
                results.Add(result);
            }));

        await Task.WhenAll(tasks);

        results.Count(r => r).Should().Be(1);
        results.Count(r => !r).Should().Be(9);
    }

    public void Dispose() => _db.Dispose();
}

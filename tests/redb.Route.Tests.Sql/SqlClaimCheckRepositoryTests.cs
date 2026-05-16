using System.Text;
using FluentAssertions;
using redb.Route.Sql.Repositories;

namespace redb.Route.Tests.Sql;

/// <summary>
/// Integration tests for <see cref="SqlClaimCheckRepository"/> using in-memory SQLite.
/// </summary>
public class SqlClaimCheckRepositoryTests : IDisposable
{
    private readonly SqliteTestHelper _db = new();

    private SqlClaimCheckRepository CreateRepo(TimeSpan? ttl = null, int cleanupInterval = 0)
    {
        var options = new SqlClaimCheckOptions
        {
            TableName = "redb_claim_check",
            DefaultTtl = ttl,
            CreateTable = true,
            CleanupInterval = cleanupInterval,
        };
        return new SqlClaimCheckRepository(_db.CreateFactory(), options);
    }

    // ── Options defaults ─────────────────────────────────────────

    [Fact]
    public void Options_Defaults()
    {
        var opts = new SqlClaimCheckOptions();
        opts.TableName.Should().Be("redb_claim_check");
        opts.DefaultTtl.Should().BeNull();
        opts.CreateTable.Should().BeTrue();
        opts.CleanupInterval.Should().Be(100);
    }

    // ── Store / Retrieve ─────────────────────────────────────────

    [Fact]
    public async Task Store_ReturnsUniqueKey()
    {
        var repo = CreateRepo();
        var data = Encoding.UTF8.GetBytes("payload");

        var key1 = await repo.Store(data);
        var key2 = await repo.Store(data);

        key1.Should().NotBeNullOrEmpty();
        key2.Should().NotBeNullOrEmpty();
        key1.Should().NotBe(key2);
    }

    [Fact]
    public async Task Store_And_Retrieve_Roundtrip()
    {
        var repo = CreateRepo();
        var data = Encoding.UTF8.GetBytes("test payload");

        var key = await repo.Store(data);
        var retrieved = await repo.Retrieve(key);

        retrieved.Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task Store_ExplicitKey_Roundtrip()
    {
        var repo = CreateRepo();
        var data = Encoding.UTF8.GetBytes("keyed data");

        await repo.Store("my-key", data);
        var retrieved = await repo.Retrieve("my-key");

        retrieved.Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task Store_ExplicitKey_Overwrites()
    {
        var repo = CreateRepo();

        await repo.Store("same", Encoding.UTF8.GetBytes("first"));
        await repo.Store("same", Encoding.UTF8.GetBytes("second"));

        var retrieved = await repo.Retrieve("same");
        Encoding.UTF8.GetString(retrieved!).Should().Be("second");
    }

    [Fact]
    public async Task Retrieve_Missing_ReturnsNull()
    {
        var repo = CreateRepo();
        var result = await repo.Retrieve("nonexistent");
        result.Should().BeNull();
    }

    // ── RetrieveAndRemove ────────────────────────────────────────

    [Fact]
    public async Task RetrieveAndRemove_RemovesEntry()
    {
        var repo = CreateRepo();
        var key = await repo.Store(Encoding.UTF8.GetBytes("temp"));

        var data = await repo.RetrieveAndRemove(key);
        data.Should().NotBeNull();

        (await repo.Retrieve(key)).Should().BeNull();
    }

    [Fact]
    public async Task RetrieveAndRemove_Missing_ReturnsNull()
    {
        var repo = CreateRepo();
        var result = await repo.RetrieveAndRemove("missing");
        result.Should().BeNull();
    }

    // ── Remove ───────────────────────────────────────────────────

    [Fact]
    public async Task Remove_DeletesEntry()
    {
        var repo = CreateRepo();
        var key = await repo.Store(Encoding.UTF8.GetBytes("delete me"));

        await repo.Remove(key);

        (await repo.Retrieve(key)).Should().BeNull();
    }

    [Fact]
    public async Task Remove_Missing_NoOp()
    {
        var repo = CreateRepo();
        var act = async () => await repo.Remove("missing");
        await act.Should().NotThrowAsync();
    }

    // ── TTL ──────────────────────────────────────────────────────

    [Fact]
    public async Task Store_WithTtl_Retrieve_ReturnsNull_AfterExpiry()
    {
        var repo = CreateRepo(ttl: TimeSpan.FromMilliseconds(50));
        var key = await repo.Store(Encoding.UTF8.GetBytes("ttl data"));

        (await repo.Retrieve(key)).Should().NotBeNull();

        await Task.Delay(80);

        (await repo.Retrieve(key)).Should().BeNull();
    }

    [Fact]
    public async Task Store_ExplicitTtl_OverridesDefault()
    {
        var repo = CreateRepo(ttl: TimeSpan.FromSeconds(30));
        var key = await repo.Store(
            Encoding.UTF8.GetBytes("short-lived"),
            ttl: TimeSpan.FromMilliseconds(50));

        await Task.Delay(80);

        (await repo.Retrieve(key)).Should().BeNull();
    }

    // ── Auto-create table ────────────────────────────────────────

    [Fact]
    public async Task AutoCreateTable_CreatesTable()
    {
        var repo = CreateRepo();

        // First operation triggers table creation
        var key = await repo.Store(Encoding.UTF8.GetBytes("test"));

        var rows = _db.Query($"SELECT * FROM redb_claim_check WHERE claim_key = '{key}'");
        rows.Should().HaveCount(1);
    }

    // ── Binary data ──────────────────────────────────────────────

    [Fact]
    public async Task Store_BinaryData_Roundtrip()
    {
        var repo = CreateRepo();
        var data = new byte[] { 0x00, 0xFF, 0x42, 0x13, 0x80 };

        var key = await repo.Store(data);
        var retrieved = await repo.Retrieve(key);

        retrieved.Should().BeEquivalentTo(data);
    }

    // ── Constructor validation ───────────────────────────────────

    [Fact]
    public void Ctor_NullFactory_Throws()
    {
        var act = () => new SqlClaimCheckRepository(null!, new SqlClaimCheckOptions());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        var act = () => new SqlClaimCheckRepository(_db.CreateFactory(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    public void Dispose() => _db.Dispose();
}

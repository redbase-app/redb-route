using redb.Route.GenericFile;

namespace redb.Route.Tests.GenericFile;

/// <summary>
/// Tests for the idempotent repository shared by the File, FTP and SFTP transports.
/// Consolidated here from the per-connector copies that tested the now-removed
/// <c>InMemoryFileIdempotentRepository</c> / <c>InMemorySftpIdempotentRepository</c>.
/// </summary>
public class InMemoryGenericFileIdempotentRepositoryTests
{
    private static GenericFileInfo FileInfo(
        string path = "/in/order.csv", long length = 100, DateTimeOffset? lastModified = null)
        => new()
        {
            Name = path[(path.LastIndexOf('/') + 1)..],
            FullPath = path,
            BasePath = "/in",
            Length = length,
            LastModified = lastModified ?? new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero)
        };

    // ── Add / Contains / Remove ─────────────────────────────────────

    [Fact]
    public async Task Add_NewKey_ReturnsTrue()
        => (await new InMemoryGenericFileIdempotentRepository().Add("key1")).Should().BeTrue();

    [Fact]
    public async Task Add_DuplicateKey_ReturnsFalse()
    {
        var repo = new InMemoryGenericFileIdempotentRepository();
        await repo.Add("key1");

        (await repo.Add("key1")).Should().BeFalse();
    }

    [Fact]
    public async Task Contains_TracksWhatWasAdded()
    {
        var repo = new InMemoryGenericFileIdempotentRepository();
        await repo.Add("key1");

        (await repo.Contains("key1")).Should().BeTrue();
        (await repo.Contains("key2")).Should().BeFalse();
    }

    [Fact]
    public async Task Remove_MakesTheKeyAddableAgain()
    {
        var repo = new InMemoryGenericFileIdempotentRepository();
        await repo.Add("key1");

        await repo.Remove("key1");

        (await repo.Contains("key1")).Should().BeFalse();
        (await repo.Add("key1")).Should().BeTrue();
    }

    [Fact]
    public async Task Remove_UnknownKey_DoesNotThrow()
    {
        var repo = new InMemoryGenericFileIdempotentRepository();
        await repo.Add("key1");

        var act = () => repo.Remove("missing");

        await act.Should().NotThrowAsync();
        repo.Count.Should().Be(1);
    }

    [Fact]
    public async Task Clear_EmptiesTheRepository()
    {
        var repo = new InMemoryGenericFileIdempotentRepository();
        await repo.Add("key1");
        await repo.Add("key2");

        await repo.Clear();

        repo.Count.Should().Be(0);
    }

    [Fact]
    public async Task Count_ReflectsDistinctKeys()
    {
        var repo = new InMemoryGenericFileIdempotentRepository();
        await repo.Add("key1");
        await repo.Add("key2");
        await repo.Add("key1");

        repo.Count.Should().Be(2);
    }

    [Fact]
    public async Task Confirm_DoesNotThrow()
    {
        var repo = new InMemoryGenericFileIdempotentRepository();
        await repo.Add("key1");

        var act = () => repo.Confirm("key1");

        await act.Should().NotThrowAsync();
    }

    // ── Case sensitivity ────────────────────────────────────────────

    [Fact]
    public async Task Keys_AreCaseSensitive()
    {
        // The key embeds the file path. Every SFTP/FTP server and every non-Windows file
        // system is case-sensitive, so folding case here would hide a real second file.
        var repo = new InMemoryGenericFileIdempotentRepository();
        await repo.Add("/upload/File.TXT");

        (await repo.Add("/upload/file.txt")).Should().BeTrue();
    }

    // ── DefaultKey ──────────────────────────────────────────────────

    [Fact]
    public void DefaultKey_CombinesPathTimestampAndLength()
    {
        var file = FileInfo("/upload/order.csv", length: 4096);

        var key = InMemoryGenericFileIdempotentRepository.DefaultKey(file);

        key.Should().Contain("/upload/order.csv");
        key.Should().Contain("|");
        key.Should().Contain("4096");
    }

    [Fact]
    public void DefaultKey_DiffersByPath_Timestamp_AndLength()
    {
        var stamp = new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

        var baseline = InMemoryGenericFileIdempotentRepository.DefaultKey(FileInfo("/upload/a.csv", 100, stamp));
        var otherPath = InMemoryGenericFileIdempotentRepository.DefaultKey(FileInfo("/upload/b.csv", 100, stamp));
        var otherSize = InMemoryGenericFileIdempotentRepository.DefaultKey(FileInfo("/upload/a.csv", 200, stamp));
        var otherStamp = InMemoryGenericFileIdempotentRepository.DefaultKey(
            FileInfo("/upload/a.csv", 100, stamp.AddSeconds(1)));

        new[] { baseline, otherPath, otherSize, otherStamp }.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void DefaultKey_NullFile_Throws()
    {
        var act = () => InMemoryGenericFileIdempotentRepository.DefaultKey(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Concurrency ─────────────────────────────────────────────────

    [Fact]
    public async Task Add_IsSafeUnderConcurrency_AndOnlyOneCallerWins()
    {
        var repo = new InMemoryGenericFileIdempotentRepository();

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => repo.Add("same-key")));

        results.Count(won => won).Should().Be(1, "the claim must be granted exactly once");
        repo.Count.Should().Be(1);
    }
}

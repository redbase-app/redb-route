using redb.Route.Sftp;

namespace redb.Route.Tests.Sftp;

public sealed class InMemorySftpIdempotentRepositoryTests
{
    [Fact]
    public async Task Add_NewKey_ReturnsTrue()
    {
        var repo = new InMemorySftpIdempotentRepository();
        var result = await repo.Add("key1");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Add_DuplicateKey_ReturnsFalse()
    {
        var repo = new InMemorySftpIdempotentRepository();
        await repo.Add("key1");
        var result = await repo.Add("key1");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task Add_CaseInsensitive_ReturnsFalse()
    {
        var repo = new InMemorySftpIdempotentRepository();
        await repo.Add("KEY");
        var result = await repo.Add("key");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task Contains_ExistingKey_ReturnsTrue()
    {
        var repo = new InMemorySftpIdempotentRepository();
        await repo.Add("key1");
        var result = await repo.Contains("key1");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_NonExistingKey_ReturnsFalse()
    {
        var repo = new InMemorySftpIdempotentRepository();
        var result = await repo.Contains("key1");
        result.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_ExistingKey_AllowsReAdd()
    {
        var repo = new InMemorySftpIdempotentRepository();
        await repo.Add("key1");
        await repo.Remove("key1");
        var result = await repo.Add("key1");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Remove_NonExistingKey_NoError()
    {
        var repo = new InMemorySftpIdempotentRepository();
        await repo.Remove("nonexistent");
        // Should not throw
    }

    [Fact]
    public async Task Confirm_DoesNotRemoveKey()
    {
        var repo = new InMemorySftpIdempotentRepository();
        await repo.Add("key1");
        await repo.Confirm("key1");
        var result = await repo.Contains("key1");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task Clear_RemovesAllKeys()
    {
        var repo = new InMemorySftpIdempotentRepository();
        await repo.Add("key1");
        await repo.Add("key2");
        await repo.Add("key3");

        await repo.Clear();

        repo.Count.Should().Be(0);
        (await repo.Contains("key1")).Should().BeFalse();
    }

    [Fact]
    public async Task Count_TracksKeys()
    {
        var repo = new InMemorySftpIdempotentRepository();
        repo.Count.Should().Be(0);

        await repo.Add("key1");
        repo.Count.Should().Be(1);

        await repo.Add("key2");
        repo.Count.Should().Be(2);

        await repo.Remove("key1");
        repo.Count.Should().Be(1);
    }

    [Fact]
    public void DefaultKey_GeneratesCompositeKey()
    {
        var remotePath = "/upload/report.csv";
        var lastModified = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero);
        var length = 1024L;

        var key = InMemorySftpIdempotentRepository.DefaultKey(remotePath, lastModified, length);

        key.Should().Contain(remotePath);
        key.Should().Contain("1024");
        key.Should().Contain("|");
    }

    [Fact]
    public void DefaultKey_DifferentFiles_DifferentKeys()
    {
        var ts = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero);

        var key1 = InMemorySftpIdempotentRepository.DefaultKey("/upload/a.csv", ts, 100);
        var key2 = InMemorySftpIdempotentRepository.DefaultKey("/upload/b.csv", ts, 100);
        var key3 = InMemorySftpIdempotentRepository.DefaultKey("/upload/a.csv", ts, 200);
        var key4 = InMemorySftpIdempotentRepository.DefaultKey("/upload/a.csv", ts.AddSeconds(1), 100);

        key1.Should().NotBe(key2);
        key1.Should().NotBe(key3);
        key1.Should().NotBe(key4);
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentAdds_NoErrors()
    {
        var repo = new InMemorySftpIdempotentRepository();
        var tasks = Enumerable.Range(0, 100)
            .Select(i => Task.Run(() => repo.Add($"key{i}")))
            .ToArray();

        await Task.WhenAll(tasks);
        repo.Count.Should().Be(100);
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentAddAndRemove_NoErrors()
    {
        var repo = new InMemorySftpIdempotentRepository();

        // Pre-populate
        for (int i = 0; i < 50; i++)
            await repo.Add($"key{i}");

        var addTasks = Enumerable.Range(50, 50)
            .Select(i => Task.Run(() => repo.Add($"key{i}")));
        var removeTasks = Enumerable.Range(0, 25)
            .Select(i => Task.Run(() => repo.Remove($"key{i}")));

        await Task.WhenAll(addTasks.Concat(removeTasks));

        repo.Count.Should().Be(75); // 50 pre-populated - 25 removed + 50 added
    }
}

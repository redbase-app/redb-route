using redb.Route.File;

namespace redb.Route.Tests.File;

/// <summary>
/// Tests for InMemoryFileIdempotentRepository.
/// </summary>
public class IdempotentRepositoryTests
{
    [Fact]
    public async Task Add_NewKey_ReturnsTrue()
    {
        var repo = new InMemoryFileIdempotentRepository();

        var result = await repo.Add("key1");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Add_DuplicateKey_ReturnsFalse()
    {
        var repo = new InMemoryFileIdempotentRepository();
        await repo.Add("key1");

        var result = await repo.Add("key1");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Contains_ExistingKey_ReturnsTrue()
    {
        var repo = new InMemoryFileIdempotentRepository();
        await repo.Add("key1");

        var result = await repo.Contains("key1");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Contains_NonExistingKey_ReturnsFalse()
    {
        var repo = new InMemoryFileIdempotentRepository();

        var result = await repo.Contains("key1");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Remove_ExistingKey_AllowsReprocessing()
    {
        var repo = new InMemoryFileIdempotentRepository();
        await repo.Add("key1");
        await repo.Remove("key1");

        var result = await repo.Add("key1");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Remove_NonExistingKey_DoesNotThrow()
    {
        var repo = new InMemoryFileIdempotentRepository();

        var act = () => repo.Remove("nonexistent");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Clear_RemovesAllKeys()
    {
        var repo = new InMemoryFileIdempotentRepository();
        await repo.Add("key1");
        await repo.Add("key2");
        await repo.Add("key3");

        await repo.Clear();

        repo.Count.Should().Be(0);
        (await repo.Add("key1")).Should().BeTrue();
    }

    [Fact]
    public async Task Count_ReflectsCurrentState()
    {
        var repo = new InMemoryFileIdempotentRepository();

        repo.Count.Should().Be(0);

        await repo.Add("key1");
        repo.Count.Should().Be(1);

        await repo.Add("key2");
        repo.Count.Should().Be(2);

        await repo.Remove("key1");
        repo.Count.Should().Be(1);
    }

    [Fact]
    public async Task Confirm_DoesNotThrow()
    {
        var repo = new InMemoryFileIdempotentRepository();
        await repo.Add("key1");

        var act = () => repo.Confirm("key1");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void DefaultKey_GeneratesCompositeKey()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(tempPath, "hello");
            var fi = new FileInfo(tempPath);

            var key = InMemoryFileIdempotentRepository.DefaultKey(fi);

            key.Should().Contain(fi.FullName);
            key.Should().Contain("|");
            key.Should().Contain(fi.Length.ToString());
        }
        finally
        {
            System.IO.File.Delete(tempPath);
        }
    }

    [Fact]
    public void DefaultKey_NullFile_Throws()
    {
        var act = () => InMemoryFileIdempotentRepository.DefaultKey(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task CaseInsensitive_Keys()
    {
        var repo = new InMemoryFileIdempotentRepository();
        await repo.Add("C:\\Folder\\File.TXT");

        var result = await repo.Add("c:\\folder\\file.txt");

        result.Should().BeFalse(); // Case-insensitive
    }
}

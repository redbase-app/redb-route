using redb.Route.Core;
using FluentAssertions;

namespace redb.Route.Tests.Core;

public class StreamCacheTests
{
    [Fact]
    public async Task SmallStream_CachesInMemory_SeekableAndRereadable()
    {
        var data = "hello world"u8.ToArray();
        var source = new MemoryStream(data);
        var cache = new StreamCache(spoolThreshold: 1024);

        await cache.CacheFromSourceAsync(source).ConfigureAwait(false);

        cache.CanSeek.Should().BeTrue();
        cache.CanRead.Should().BeTrue();
        cache.Length.Should().Be(data.Length);
        cache.Position.Should().Be(0);

        // First read
        using var reader1 = new StreamReader(cache, leaveOpen: true);
        var text1 = await reader1.ReadToEndAsync().ConfigureAwait(false);
        text1.Should().Be("hello world");

        // Rewind and re-read
        cache.Position = 0;
        using var reader2 = new StreamReader(cache, leaveOpen: true);
        var text2 = await reader2.ReadToEndAsync().ConfigureAwait(false);
        text2.Should().Be("hello world");
    }

    [Fact]
    public async Task LargeStream_SpoolsToDisk_StillSeekable()
    {
        // Create data that exceeds the spool threshold
        var data = new byte[256];
        Random.Shared.NextBytes(data);
        var source = new MemoryStream(data);
        var cache = new StreamCache(spoolThreshold: 64); // Low threshold to trigger spool

        await cache.CacheFromSourceAsync(source).ConfigureAwait(false);

        cache.CanSeek.Should().BeTrue();
        cache.Length.Should().Be(256);
        cache.Position.Should().Be(0);

        // Read all
        var result = new byte[256];
        var totalRead = 0;
        int bytesRead;
        while ((bytesRead = await cache.ReadAsync(result.AsMemory(totalRead)).ConfigureAwait(false)) > 0)
            totalRead += bytesRead;

        totalRead.Should().Be(256);
        result.Should().Equal(data);

        // Rewind and verify
        cache.Position = 0;
        totalRead = 0;
        while ((bytesRead = await cache.ReadAsync(result.AsMemory(totalRead)).ConfigureAwait(false)) > 0)
            totalRead += bytesRead;
        result.Should().Equal(data);
    }

    [Fact]
    public async Task Dispose_CleansUpResources()
    {
        var data = new byte[256];
        Random.Shared.NextBytes(data);
        var source = new MemoryStream(data);
        var cache = new StreamCache(spoolThreshold: 64);

        await cache.CacheFromSourceAsync(source).ConfigureAwait(false);

        await cache.DisposeAsync().ConfigureAwait(false);

        cache.CanRead.Should().BeFalse();
        cache.CanSeek.Should().BeFalse();
    }

    [Fact]
    public async Task EmptySource_CreatesEmptyCache()
    {
        var source = new MemoryStream();
        var cache = new StreamCache();

        await cache.CacheFromSourceAsync(source).ConfigureAwait(false);

        cache.Length.Should().Be(0);
        cache.Position.Should().Be(0);

        var buffer = new byte[10];
        var read = await cache.ReadAsync(buffer).ConfigureAwait(false);
        read.Should().Be(0);
    }

    [Fact]
    public async Task Write_ThrowsNotSupported()
    {
        var cache = new StreamCache();
        await cache.CacheFromSourceAsync(new MemoryStream()).ConfigureAwait(false);

        var act = () => cache.Write(new byte[1], 0, 1);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public async Task SetLength_ThrowsNotSupported()
    {
        var cache = new StreamCache();
        await cache.CacheFromSourceAsync(new MemoryStream()).ConfigureAwait(false);

        var act = () => cache.SetLength(100);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public async Task Seek_WorksCorrectly()
    {
        var data = "abcdefghij"u8.ToArray();
        var source = new MemoryStream(data);
        var cache = new StreamCache();

        await cache.CacheFromSourceAsync(source).ConfigureAwait(false);

        cache.Seek(5, SeekOrigin.Begin);
        cache.Position.Should().Be(5);

        var buffer = new byte[5];
        var read = cache.Read(buffer, 0, 5);
        read.Should().Be(5);
        buffer.Should().Equal("fghij"u8.ToArray());
    }
}

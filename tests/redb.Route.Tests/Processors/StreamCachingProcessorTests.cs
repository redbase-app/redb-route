using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Processors;
using FluentAssertions;

namespace redb.Route.Tests.Processors;

public class StreamCachingProcessorTests
{
    [Fact]
    public async Task StreamBody_WrappedInStreamCache()
    {
        var data = "test data"u8.ToArray();
        object? capturedBody = null;
        var next = new DelegateProcessor(ex => capturedBody = ex.In.Body);
        var processor = new StreamCachingProcessor(next, new StreamCacheOptions());

        var exchange = new Exchange(new Message(new MemoryStream(data)));

        await processor.Process(exchange).ConfigureAwait(false);

        capturedBody.Should().BeOfType<StreamCache>();
        var cache = (StreamCache)capturedBody!;
        cache.CanSeek.Should().BeTrue();
        cache.Length.Should().Be(data.Length);
    }

    [Fact]
    public async Task NonStreamBody_PassedThrough()
    {
        var next = new DelegateProcessor(_ => { });
        var processor = new StreamCachingProcessor(next, new StreamCacheOptions());

        var exchange = new Exchange(new Message("hello"));

        await processor.Process(exchange).ConfigureAwait(false);

        exchange.In.Body.Should().Be("hello");
    }

    [Fact]
    public async Task AlreadyStreamCache_NotDoubleWrapped()
    {
        var original = new StreamCache();
        await original.CacheFromSourceAsync(new MemoryStream("hello"u8.ToArray())).ConfigureAwait(false);

        object? capturedBody = null;
        var next = new DelegateProcessor(ex => capturedBody = ex.In.Body);
        var processor = new StreamCachingProcessor(next, new StreamCacheOptions());

        var exchange = new Exchange(new Message(original));

        await processor.Process(exchange).ConfigureAwait(false);

        capturedBody.Should().BeSameAs(original);
    }

    [Fact]
    public async Task CachedStream_CanBeReread()
    {
        var data = "rereadable content"u8.ToArray();
        var results = new ConcurrentBag<string>();
        var next = new DelegateProcessor(async ex =>
        {
            var stream = (Stream)ex.In.Body!;
            using var reader = new StreamReader(stream, leaveOpen: true);
            results.Add(await reader.ReadToEndAsync().ConfigureAwait(false));
            stream.Position = 0;
        });
        var processor = new StreamCachingProcessor(next, new StreamCacheOptions());

        var exchange = new Exchange(new Message(new MemoryStream(data)));

        await processor.Process(exchange).ConfigureAwait(false);

        results.Should().ContainSingle().Which.Should().Be("rereadable content");

        // Verify we can still re-read
        var stream = (Stream)exchange.In.Body!;
        stream.Position = 0;
        using var postReader = new StreamReader(stream, leaveOpen: true);
        var postText = await postReader.ReadToEndAsync().ConfigureAwait(false);
        postText.Should().Be("rereadable content");
    }

    [Fact]
    public async Task NullBody_PassedThrough()
    {
        var next = new DelegateProcessor(_ => { });
        var processor = new StreamCachingProcessor(next, new StreamCacheOptions());

        var exchange = new Exchange(new Message());

        await processor.Process(exchange).ConfigureAwait(false);

        exchange.In.Body.Should().BeNull();
    }
}

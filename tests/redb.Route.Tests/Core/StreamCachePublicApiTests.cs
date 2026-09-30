using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Core;

/// <summary>
/// <see cref="StreamCache"/> as connectors use it (AS4 spools a decrypted attachment before the route may see it):
/// written through, then completed and read from the start. Plus the caching step's ownership of the stream it
/// replaces, the engine-wide switch, and the byte count of a stream body.
/// </summary>
public class StreamCachePublicApiTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("redb-streamcache-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class TrackingStream(byte[] data) : MemoryStream(data)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { Disposed = true; return base.DisposeAsync(); }
    }

    /// <summary>Forward-only, as a network or decrypting stream is.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    [Fact]
    public async Task Written_through_then_completed_it_reads_from_the_start_and_spools_past_the_threshold()
    {
        var data = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        await using var cache = new StreamCache(new StreamCacheOptions { SpoolThreshold = 1024, TempDirectory = _temp });

        await cache.WriteAsync(data.AsMemory(0, 3000));
        cache.Write(data, 3000, 2000);
        cache.IsSpooled.Should().BeTrue();
        Directory.GetFiles(_temp).Should().ContainSingle("the content above the threshold went to a temporary file");

        cache.CompleteWriting();

        cache.CanWrite.Should().BeFalse();
        cache.CanSeek.Should().BeTrue();
        var read = new MemoryStream();
        await cache.CopyToAsync(read);
        read.ToArray().Should().Equal(data);
    }

    [Fact]
    public async Task It_refuses_to_be_read_before_writing_completed_and_written_after()
    {
        await using var cache = new StreamCache(new StreamCacheOptions { TempDirectory = _temp });
        cache.Write([1, 2, 3], 0, 3);

        var readEarly = () => cache.ReadByte();
        readEarly.Should().Throw<NotSupportedException>().WithMessage("*CompleteWriting*");

        cache.CompleteWriting();
        var writeLate = () => cache.Write([4], 0, 1);
        writeLate.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public async Task Disposing_it_removes_the_temporary_file()
    {
        var cache = new StreamCache(new StreamCacheOptions { SpoolThreshold = 16, TempDirectory = _temp });
        await cache.WriteAsync(new byte[100]);
        Directory.GetFiles(_temp).Should().HaveCount(1);

        await cache.DisposeAsync();

        Directory.GetFiles(_temp).Should().BeEmpty();
    }

    [Fact]
    public async Task The_caching_step_closes_the_stream_it_replaces()
    {
        var source = new TrackingStream("payload"u8.ToArray());
        var exchange = new Exchange(new Message(source));

        await new StreamCachingTransformer(new StreamCacheOptions { TempDirectory = _temp }).Process(exchange);

        exchange.In.Body.Should().BeOfType<StreamCache>();
        source.Disposed.Should().BeTrue("the exchange now owns the cache; nobody else would ever close the source");
    }

    [Fact]
    public async Task The_engine_switch_caches_every_routes_stream_body()
    {
        var options = new RouteEngineOptions();
        options.StreamCaching.Enabled = true;
        options.StreamCaching.TempDirectory = _temp;
        await using var context = new RouteContext(options: options);
        object? seen = null;
        context.AddRoutes(r => r.From("direct://cached").Process(e => seen = e.In.Body));
        await context.Start();
        var producer = context.GetEndpoint("direct://cached").CreateProducer();

        await producer.Process(new Exchange(new Message(new ForwardOnlyStream("abc"u8.ToArray()))));

        seen.Should().BeOfType<StreamCache>("StreamCacheOptions.Enabled turns caching on for all routes");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_byte_count_of_a_stream_body_is_its_length_or_nothing(bool seekable)
    {
        Stream body = seekable ? new MemoryStream(new byte[42]) : new ForwardOnlyStream(new byte[42]);

        var size = EndpointBase<EndpointOptions>.EstimateBodySize(body);

        size.Should().Be(seekable ? 42 : 0, "a forward-only stream cannot be measured without reading it");
        if (!seekable)
            body.Position.Should().Be(0, "measuring must not consume the body");
    }
}

/// <summary>
/// The stream cache options a connector spools with come from the same chain the <c>.StreamCaching()</c> step uses:
/// a registered <see cref="StreamCacheOptions"/>, then the engine's <see cref="RouteEngineOptions.StreamCaching"/>,
/// then the defaults.
/// </summary>
public class StreamCacheOptionsResolutionTests
{
    [Fact]
    public async Task The_engine_options_reach_a_connector()
    {
        var engine = new RouteEngineOptions();
        engine.StreamCaching.SpoolThreshold = 4096;
        engine.StreamCaching.TempDirectory = "/spool";
        await using var context = new RouteContext(options: engine);

        var options = redb.Route.Extensions.RouteContextExtensions.GetStreamCacheOptions(context);

        options.SpoolThreshold.Should().Be(4096);
        options.TempDirectory.Should().Be("/spool");
    }

    [Fact]
    public async Task A_registered_service_wins_over_the_engine_options()
    {
        var engine = new RouteEngineOptions();
        engine.StreamCaching.SpoolThreshold = 4096;
        await using var context = new RouteContext(options: engine);
        context.AddService(typeof(StreamCacheOptions), new StreamCacheOptions { SpoolThreshold = 99 });

        redb.Route.Extensions.RouteContextExtensions.GetStreamCacheOptions(context).SpoolThreshold.Should().Be(99);
    }

    [Fact]
    public void Without_a_context_the_defaults_apply()
        => redb.Route.Extensions.RouteContextExtensions.GetStreamCacheOptions(null).SpoolThreshold
            .Should().Be(StreamCache.DefaultSpoolThreshold);
}

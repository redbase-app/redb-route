using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;

namespace redb.Route.Processors;

/// <summary>
/// Wraps a forward-only Stream body with a seekable <see cref="StreamCache"/>,
/// allowing downstream processors to re-read the body multiple times.
/// Non-Stream bodies pass through unchanged.
/// </summary>
internal sealed class StreamCachingProcessor : IProcessor
{
    private readonly IProcessor _next;
    private readonly StreamCacheOptions _options;

    /// <summary>Creates a stream caching processor.</summary>
    /// <param name="next">The next processor in the pipeline.</param>
    /// <param name="options">Stream cache options (spool threshold, temp directory).</param>
    internal StreamCachingProcessor(IProcessor next, StreamCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);
        _next = next;
        _options = options;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        await StreamCacheBody.CacheAsync(exchange, _options, ct).ConfigureAwait(false);

        await _next.Process(exchange, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Standalone (no-next) variant of stream caching, suitable for use as a
/// <see cref="ProcessorDefinition"/> leaf inside a <see cref="PipelineProcessor"/>.
/// Wraps a forward-only <see cref="Stream"/> body with a seekable <see cref="StreamCache"/>;
/// the pipeline advances to the next step automatically.
/// Non-Stream bodies pass through unchanged.
/// </summary>
internal sealed class StreamCachingTransformer : IProcessor
{
    private readonly StreamCacheOptions _options;

    internal StreamCachingTransformer(StreamCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        await StreamCacheBody.CacheAsync(exchange, _options, ct).ConfigureAwait(false);
    }
}

/// <summary>The one place a stream body is replaced by its cache.</summary>
internal static class StreamCacheBody
{
    /// <summary>
    /// Replaces a forward-only stream body with a <see cref="StreamCache"/> and closes the source: the exchange owns its
    /// body and disposes only what the body is, so a replaced stream nobody closes would hold its file or connection
    /// until the garbage collector ran (a streamed file the consumer then fails to delete). Camel closes the source too.
    /// </summary>
    public static async Task CacheAsync(IExchange exchange, StreamCacheOptions options, CancellationToken ct)
    {
        if (exchange.In.Body is not Stream stream || stream is StreamCache)
            return;

        var cache = new StreamCache(options);
        try
        {
            await cache.CacheFromSourceAsync(stream, ct).ConfigureAwait(false);
        }
        catch
        {
            // Not cached: the body stays the source, and the half-written cache goes.
            await cache.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        exchange.In.Body = cache;
        await stream.DisposeAsync().ConfigureAwait(false);
    }
}

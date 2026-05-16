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
        if (exchange.In.Body is Stream stream and not StreamCache)
        {
            var cache = new StreamCache(_options.SpoolThreshold, _options.TempDirectory);
            await cache.CacheFromSourceAsync(stream, ct).ConfigureAwait(false);
            exchange.In.Body = cache;
        }

        await _next.Process(exchange, ct).ConfigureAwait(false);
    }
}

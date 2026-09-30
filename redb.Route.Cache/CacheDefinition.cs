using System.Security.Cryptography;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Definitions;
using redb.Route.Expressions;

namespace redb.Route.Cache;

/// <summary>
/// The caching scope (WSO2 <c>Cache</c> mediator analog): on a hit the body (and optionally the headers)
/// comes from the cache and the inner steps are skipped; on a miss they run and the result is stored.
/// Header <c>cache.hit</c> says which. Close with <see cref="EndCache"/>.
/// </summary>
public sealed class CacheDefinition : RouteDefinitionBase<CacheDefinition>, IRouteScope
{
    private readonly Func<IExchange, string> _key;
    private bool _keyFromBody;
    private TimeSpan? _ttl;
    private TimeSpan? _sliding;
    private string _region = "default";
    private bool _cacheHeaders;
    private CacheProvider? _provider;

    internal CacheDefinition(Func<IExchange, string> key, TimeSpan? ttl)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _ttl = ttl;
    }

    /// <summary>Key namespace; <c>clear</c> on the component empties one region. Default <c>default</c>.</summary>
    public CacheDefinition Region(string region)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        _region = region;
        return this;
    }

    /// <summary>Absolute lifetime of an entry.</summary>
    public CacheDefinition Ttl(TimeSpan ttl) { _ttl = ttl; return this; }

    /// <summary>Lifetime renewed on every hit.</summary>
    public CacheDefinition SlidingExpiration(TimeSpan sliding) { _sliding = sliding; return this; }

    /// <summary>Store and restore the headers together with the body.</summary>
    public CacheDefinition CacheHeaders(bool value = true) { _cacheHeaders = value; return this; }

    /// <summary>Use the <c>IDistributedCache</c> registered in DI or on the context.</summary>
    public CacheDefinition Distributed() { _provider = CacheProvider.Distributed; return this; }

    /// <summary>Use the in-process cache (default).</summary>
    public CacheDefinition InMemory() { _provider = CacheProvider.Memory; return this; }

    /// <summary>
    /// Key = SHA-256 of the body instead of the explicit key (WSO2 "hash of the request" mode). Only the
    /// body is hashed: the content type and the headers are not part of the key. A stream body is read
    /// to the end for it, and the message continues with its bytes; an object goes through the context's
    /// data format, the way the distributed cache stores it.
    /// </summary>
    public CacheDefinition KeyFromBody()
    {
        _keyFromBody = true;
        return this;
    }

    /// <summary>Closes the scope and returns to the enclosing route.</summary>
    public IRouteDefinition EndCache()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException("EndCache() called without a parent route."));

    /// <inheritdoc />
    public IRouteDefinition End() => EndCache();

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var options = CacheStores.Options(context);
        var provider = _provider ?? options.DefaultProvider;
        var store = CacheStores.Resolve(context, provider);
        var flights = CacheStores.Flights(context, provider);
        var inner = NodePipeline.Body(context, Outputs);
        var region = _region;
        var registry = context.GetService<IDataFormatRegistry>();
        Func<IExchange, string> key = _keyFromBody ? exchange => BodyHash(exchange.In, registry) : _key;
        var ttl = _ttl ?? options.DefaultTtl;
        if (ttl is { } t && t <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), t, "Cache: the TTL must be positive.");
        if (_sliding is { } s && s <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException("sliding", s, "Cache: the sliding expiration must be positive.");
        return new CacheScopeProcessor(store, flights, e => CacheKeys.For(region, key(e)), ttl, _sliding, _cacheHeaders, inner);
    }

    internal static Func<IExchange, string> KeyFromTemplate(string keyTemplate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyTemplate);
        var expression = new StringExpression(keyTemplate);
        // Invariant text: a decimal or a date key must spell the same on every machine sharing one Redis.
        return exchange => expression.Evaluate<object>(exchange) switch
        {
            null => throw new InvalidOperationException($"Cache key '{keyTemplate}' evaluated to null."),
            string text => text,
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            var other => other.ToString() ?? throw new InvalidOperationException($"Cache key '{keyTemplate}' evaluated to null."),
        };
    }

    /// <summary>
    /// SHA-256 of the message body. A stream can be read once and the inner steps still need the body,
    /// so its bytes replace it on the message, as they do when a miss is stored.
    /// </summary>
    internal static string BodyHash(IMessage message, IDataFormatRegistry? registry)
    {
        byte[] bytes;
        switch (message.Body)
        {
            case null:
                bytes = [];
                break;
            case byte[] raw:
                bytes = raw;
                break;
            case string text:
                bytes = Encoding.UTF8.GetBytes(text);
                break;
            case Stream stream:
                bytes = CacheEntry.ReadToEnd(stream);
                message.Body = bytes;
                break;
            case var body:
                try
                {
                    bytes = CacheSerialization.Serialize(registry, body, message.ContentType).Bytes;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Cache: KeyFromBody cannot hash a {body.GetType().Name} body: {ex.Message}", ex);
                }
                break;
        }
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

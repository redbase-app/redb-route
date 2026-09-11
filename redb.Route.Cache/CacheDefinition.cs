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
    private Func<IExchange, string> _key;
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

    /// <summary>Key = SHA-256 of the body (WSO2 "hash of the whole request" mode) instead of the explicit key.</summary>
    public CacheDefinition KeyFromBody()
    {
        _key = static exchange => BodyHash(exchange.In.Body);
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
        var store = CacheStores.Resolve(context, _provider ?? options.DefaultProvider);
        var inner = NodePipeline.Body(context, Outputs);
        var region = _region;
        var key = _key;
        var ttl = _ttl ?? options.DefaultTtl;
        if (ttl is { } t && t <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), t, "Cache: the TTL must be positive.");
        if (_sliding is { } s && s <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException("sliding", s, "Cache: the sliding expiration must be positive.");
        return new CacheScopeProcessor(store, e => CacheKeys.For(region, key(e)), region, ttl, _sliding, _cacheHeaders, inner);
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

    internal static string BodyHash(object? body)
    {
        var bytes = body switch
        {
            null => [],
            byte[] b => b,
            string s => Encoding.UTF8.GetBytes(s),
            _ => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body),
        };
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

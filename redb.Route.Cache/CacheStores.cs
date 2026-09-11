using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.Cache;

/// <summary>
/// Resolves the store for a node: options and caches come from the context services first
/// (<c>UseCache</c>, <c>AddService</c>), then from DI, then — for the in-process cache — a
/// <see cref="MemoryCache"/> is created once per context and shared by every cache node.
/// </summary>
internal static class CacheStores
{
    private static readonly object Sync = new();

    public static RouteCacheOptions Options(IRouteContext context)
        => context.GetService<RouteCacheOptions>()
           ?? context.GetServiceProvider()?.GetService<RouteCacheOptions>()
           ?? new RouteCacheOptions();

    public static ICacheStore Resolve(IRouteContext context, CacheProvider provider)
    {
        lock (Sync)
        {
            return provider == CacheProvider.Distributed ? Distributed(context) : Memory(context);
        }
    }

    private static ICacheStore Memory(IRouteContext context)
    {
        if (context.GetService<MemoryCacheStore>() is { } existing) return existing;

        var hostCache = context.GetService<IMemoryCache>() ?? context.GetServiceProvider()?.GetService<IMemoryCache>();
        if (hostCache is null)
        {
            var options = Options(context);
            hostCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = options.MaxEntries });
            context.AddService(typeof(IMemoryCache), hostCache);
        }

        var store = new MemoryCacheStore(hostCache);
        context.AddService(typeof(MemoryCacheStore), store);
        return store;
    }

    private static ICacheStore Distributed(IRouteContext context)
    {
        if (context.GetService<DistributedCacheStore>() is { } existing) return existing;

        var cache = context.GetService<IDistributedCache>() ?? context.GetServiceProvider()?.GetService<IDistributedCache>()
            ?? throw new InvalidOperationException(
                "Cache: no IDistributedCache is registered. Add one in DI (services.AddStackExchangeRedisCache(...), " +
                "AddDistributedMemoryCache()) or on the context (context.AddService(typeof(IDistributedCache), cache)).");

        var store = new DistributedCacheStore(cache, context.GetService<IDataFormatRegistry>());
        context.AddService(typeof(DistributedCacheStore), store);
        return store;
    }
}

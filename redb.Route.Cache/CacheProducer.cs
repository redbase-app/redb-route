using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Cache;

/// <summary>Executes the endpoint's action against the resolved store.</summary>
internal sealed class CacheProducer : IProducer
{
    private readonly CacheEndpoint _endpoint;
    private readonly CacheEndpointOptions _options;
    private ICacheStore? _store;
    private TimeSpan? _ttl;
    private TimeSpan? _sliding;

    public CacheProducer(CacheEndpoint endpoint, CacheEndpointOptions options)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    private readonly object _startLock = new();
    private volatile bool _started;

    public Task Start(CancellationToken ct = default)
    {
        // Idempotent and atomic: two first messages racing into a lazily started producer must not see
        // the store set while the TTL is still missing (an entry stored without expiry).
        lock (_startLock)
        {
            if (_started) return Task.CompletedTask;
            var context = (_endpoint.Component as ComponentBase)?.Context
                ?? throw new InvalidOperationException("cache: component is not attached to a route context.");
            var options = CacheStores.Options(context);
            _ttl = CacheDuration.Parse(_options.Ttl) ?? options.DefaultTtl;
            _sliding = CacheDuration.Parse(_options.Sliding);
            _store = CacheStores.Resolve(context, _options.ParsedProvider ?? options.DefaultProvider);
            _started = true;
        }
        return Task.CompletedTask;
    }

    public Task Stop(CancellationToken ct = default) => Task.CompletedTask;

    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (!_started) await Start(ct).ConfigureAwait(false);
        var store = _store!;

        if (_options.ParsedAction == CacheAction.Clear)
        {
            await store.ClearAsync(_endpoint.Region, ct).ConfigureAwait(false);
            return;
        }

        var rawKey = _options.Key!.Value.Resolve(exchange);
        if (string.IsNullOrEmpty(rawKey))
            throw new InvalidOperationException("cache: key resolved to an empty value.");
        var key = CacheKeys.For(_endpoint.Region, rawKey);

        switch (_options.ParsedAction)
        {
            case CacheAction.Get:
                var hit = await store.GetAsync(key, ct).ConfigureAwait(false);
                if (hit is not null) hit.ApplyTo(exchange.In);
                exchange.In.Headers[CacheScopeProcessor.HitHeader] = hit is not null;
                break;
            case CacheAction.Put:
                await store.SetAsync(key, CacheEntry.From(exchange.In, _options.CacheHeaders), _ttl, _sliding, ct).ConfigureAwait(false);
                break;
            case CacheAction.Remove:
                await store.RemoveAsync(key, ct).ConfigureAwait(false);
                break;
        }
    }
}

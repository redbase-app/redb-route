namespace redb.Route.Cache;

/// <summary>One provider behind both forms of the EIP (scope and component).</summary>
internal interface ICacheStore
{
    ValueTask<CacheEntry?> GetAsync(string key, CancellationToken ct);

    ValueTask SetAsync(string key, CacheEntry entry, TimeSpan? ttl, TimeSpan? sliding, CancellationToken ct);

    ValueTask RemoveAsync(string key, CancellationToken ct);

    /// <summary>Removes every key of a region (keys are <c>region:key</c>).</summary>
    ValueTask ClearAsync(string region, CancellationToken ct);
}

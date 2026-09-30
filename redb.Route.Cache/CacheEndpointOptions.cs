using redb.Route.Core;

namespace redb.Route.Cache;

/// <summary>
/// Options of a <c>cache:</c> endpoint:
/// <c>cache:&lt;region&gt;?action=get|put|remove|clear&amp;key=${header.id}&amp;ttl=5m&amp;sliding=1m&amp;provider=memory|distributed&amp;headers=X-Rate,Content-Language</c>.
/// </summary>
public sealed class CacheEndpointOptions : EndpointOptions
{
    /// <summary><c>get</c> (default), <c>put</c>, <c>remove</c> or <c>clear</c>.</summary>
    public string Action { get; set; } = "get";

    /// <summary>Entry key; <c>${...}</c> resolved per message. Required except for <c>clear</c>.</summary>
    public DynamicValue<string>? Key { get; set; }

    /// <summary>Absolute lifetime for <c>put</c> (<c>5m</c>, <c>30s</c>, <c>hh:mm:ss</c>).</summary>
    public string? Ttl { get; set; }

    /// <summary>Sliding lifetime for <c>put</c>.</summary>
    public string? Sliding { get; set; }

    /// <summary><c>memory</c> (default) or <c>distributed</c>.</summary>
    public string? Provider { get; set; }

    /// <summary>
    /// <c>put</c> only: the headers stored with the body, by name, comma-separated. A <c>put</c> sees the
    /// message as a whole and cannot tell the headers of the value from the headers of the request, so
    /// it stores none unless named; a credential (<c>Authorization</c>, <c>Cookie</c>, …) cannot be named.
    /// </summary>
    public string? Headers { get; set; }

    /// <summary>Parsed action.</summary>
    public CacheAction ParsedAction => Action.ToLowerInvariant() switch
    {
        "get" => CacheAction.Get,
        "put" => CacheAction.Put,
        "remove" => CacheAction.Remove,
        "clear" => CacheAction.Clear,
        _ => throw new ArgumentException($"Cache endpoint: unknown action '{Action}' (get, put, remove, clear)."),
    };

    /// <summary>Parsed provider; <c>null</c> = package default.</summary>
    public CacheProvider? ParsedProvider => Provider?.ToLowerInvariant() switch
    {
        null or "" => null,
        "memory" => CacheProvider.Memory,
        "distributed" => CacheProvider.Distributed,
        _ => throw new ArgumentException($"Cache endpoint: unknown provider '{Provider}' (memory, distributed)."),
    };

    /// <summary>The header names of <see cref="Headers"/>; <c>null</c> when none were given.</summary>
    public IReadOnlyCollection<string>? ParsedHeaders
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Headers)) return null;
            var names = Headers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Length == 0)
                throw new ArgumentException("Cache endpoint: headers= names no header.");
            foreach (var name in names)
                if (CacheHeaderPolicy.IsCredential(name))
                    throw new ArgumentException($"Cache endpoint: '{name}' is a credential and cannot be cached: a hit would hand it to another exchange.");
            return names;
        }
    }

    /// <inheritdoc />
    public override void Validate()
    {
        _ = ParsedAction;
        _ = ParsedProvider;
        _ = CacheDuration.Parse(Ttl);
        _ = CacheDuration.Parse(Sliding);
        if (ParsedAction != CacheAction.Clear && Key is null)
            throw new ArgumentException($"Cache endpoint: action '{Action}' needs key=...");
        if (ParsedHeaders is not null && ParsedAction != CacheAction.Put)
            throw new ArgumentException($"Cache endpoint: headers= belongs to action=put; action '{Action}' takes the entry as it was stored.");
    }

    /// <inheritdoc />
    protected override string? UnknownParameterHint(string name)
        => string.Equals(name, "cacheHeaders", StringComparison.OrdinalIgnoreCase)
            ? "A put stores no headers unless they are named: headers=X-Rate,Content-Language. It cannot tell the headers of the value from the headers of the request, and a hit hands what it stores to another exchange."
            : null;
}

/// <summary>Operations of the <c>cache:</c> component.</summary>
public enum CacheAction
{
    /// <summary>Read the entry into the message, or into <c>Out</c> when it was cached as a reply; sets <c>cache.hit</c>.</summary>
    Get,
    /// <summary>Store the message under the key.</summary>
    Put,
    /// <summary>Remove the key.</summary>
    Remove,
    /// <summary>Remove every key of the region.</summary>
    Clear,
}

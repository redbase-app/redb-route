using redb.Route.Abstractions;

namespace redb.Route.Cache;

/// <summary>
/// Which headers a cache entry may carry. A hit hands one exchange's headers to another, so an entry
/// keeps what the miss produced, never what the exchange came in with, and never a credential: an
/// <c>Authorization</c>, <c>Proxy-Authorization</c> (RFC 9110), <c>Cookie</c> or <c>Set-Cookie</c>
/// (RFC 6265) belongs to the exchange it arrived on. The HTTP consumer drops <c>Authorization</c> after
/// its own check for the same reason; the cache is the other place a header crosses exchanges.
/// </summary>
internal static class CacheHeaderPolicy
{
    private static readonly HashSet<string> Credentials = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie",
    };

    /// <summary>A header a cache entry never carries, whichever form named it.</summary>
    public static bool IsCredential(string name) => Credentials.Contains(name);

    /// <summary>The header values an exchange came in with, by name, taken before the inner steps run.</summary>
    public static Dictionary<string, object?> Snapshot(IMessage message)
        => new(message.Headers, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The headers of <paramref name="message"/> the inner steps produced: added, or set to another
    /// instance than the one in <paramref name="before"/>. A header the steps did not touch is the same
    /// instance afterwards, which is how the HTTP consumer tells a route's response header from an
    /// echoed request header. Credentials and <c>cache.hit</c> are never part of it.
    /// </summary>
    public static Dictionary<string, object?> Produced(IMessage message, IReadOnlyDictionary<string, object?> before)
    {
        var produced = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in message.Headers)
        {
            if (IsCredential(name) || string.Equals(name, CacheScopeProcessor.HitHeader, StringComparison.OrdinalIgnoreCase)) continue;
            if (before.TryGetValue(name, out var arrived) && ReferenceEquals(arrived, value)) continue;
            produced[name] = value;
        }
        return produced;
    }

    /// <summary>The names in <paramref name="before"/> the inner steps removed from <paramref name="message"/>.</summary>
    public static List<string> Removed(IMessage message, IReadOnlyDictionary<string, object?> before)
    {
        var removed = new List<string>();
        foreach (var name in before.Keys)
            if (!message.Headers.ContainsKey(name))
                removed.Add(name);
        return removed;
    }
}

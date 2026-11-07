namespace redb.Route.Cache;

/// <summary>
/// How a region and a key become one store key. The region is length-prefixed, so region <c>a</c>
/// with key <c>b:c</c> and region <c>a:b</c> with key <c>c</c> never share an entry, and clearing a
/// region never reaches a neighbour whose name merely starts the same way.
/// </summary>
internal static class CacheKeys
{
    /// <summary>The store key of <paramref name="key"/> in <paramref name="region"/>.</summary>
    public static string For(string region, string key) => $"{region.Length}:{region}:{key}";

    /// <summary>The prefix every key of <paramref name="region"/> starts with (region-wide clear).</summary>
    public static string Prefix(string region) => $"{region.Length}:{region}:";
}

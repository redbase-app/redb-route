using System.Globalization;

namespace redb.Route.Cache;

/// <summary>Which cache backs a node.</summary>
public enum CacheProvider
{
    /// <summary>In-process <c>IMemoryCache</c> (created on demand when none is registered).</summary>
    Memory,

    /// <summary><c>IDistributedCache</c> registered in DI or on the context (Redis, SQL Server, …).</summary>
    Distributed,
}

/// <summary>Package-wide settings: <c>context.UseCache(o => …)</c> / <c>services.AddRedbRouteCache(o => …)</c>.</summary>
public sealed class RouteCacheOptions
{
    /// <summary>Upper bound on entries of the package-created in-process cache (one unit per entry); <c>null</c> = unbounded. Ignored for a cache registered by the host.</summary>
    public long? MaxEntries { get; set; }

    /// <summary>TTL applied when a node does not give one; <c>null</c> = no expiration.</summary>
    public TimeSpan? DefaultTtl { get; set; }

    /// <summary>Provider used when a node does not choose one. Default <see cref="CacheProvider.Memory"/>.</summary>
    public CacheProvider DefaultProvider { get; set; } = CacheProvider.Memory;
}

/// <summary>Durations as people write them in URIs: <c>500ms</c>, <c>30s</c>, <c>5m</c>, <c>2h</c>, <c>1d</c>, or <c>hh:mm:ss</c>.</summary>
public static class CacheDuration
{
    /// <summary>Parses a duration; <c>null</c> / empty → <c>null</c>.</summary>
    public static TimeSpan? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();

        TimeSpan? parsed = null;
        foreach (var (suffix, factor) in Units)
        {
            if (!value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var number = value[..^suffix.Length].Trim();
            if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                parsed = factor(amount);
                break;
            }
        }

        // A bare number is ambiguous — TimeSpan would read "5" as five days — so a unit is required.
        if (parsed is null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            throw new FormatException($"'{text}' has no unit: write 5s, 5m, 2h, 1d or hh:mm:ss.");

        if (parsed is null && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span))
            parsed = span;

        if (parsed is null)
            throw new FormatException($"'{text}' is not a duration (use 500ms, 30s, 5m, 2h, 1d or hh:mm:ss).");
        if (parsed.Value <= TimeSpan.Zero)
            throw new FormatException($"'{text}' is not a positive duration.");
        return parsed;
    }

    private static readonly (string Suffix, Func<double, TimeSpan> Factor)[] Units =
    [
        ("ms", TimeSpan.FromMilliseconds),
        ("s", TimeSpan.FromSeconds),
        ("m", TimeSpan.FromMinutes),
        ("h", TimeSpan.FromHours),
        ("d", TimeSpan.FromDays),
    ];
}

using System.Globalization;

namespace redb.Route.Core;

/// <summary>
/// One resolver for every consumer-concurrency option that accepts <c>auto</c>
/// (план HTTP_CONCURRENCY_LIMITS_PLAN, решение В-7). The industry default for broker consumers
/// is 1 — ordering is preserved and handlers need not be thread-safe (Camel JMS/SQS, Spring,
/// the Azure SDK all ship 1) — so the default stays 1 and parallelism is an explicit opt-in:
/// a number, or <c>auto</c> = <c>max(CPU count, 2)</c>, the NServiceBus formula.
/// <para>
/// The option is a STRING on purpose: the URI binder silently turns an unconvertible value into
/// the default for an int property, so <c>concurrentConsumers=auto</c> on an int option would
/// quietly mean 1 — the worst possible outcome. A string binds verbatim and this resolver fails
/// loud on anything that is neither a positive integer nor <c>auto</c>.
/// </para>
/// </summary>
public static class ConcurrencyOption
{
    /// <summary>The value <c>auto</c> resolves to: max(logical CPU count, 2).</summary>
    public static int Auto => Math.Max(Environment.ProcessorCount, 2);

    /// <summary>
    /// Resolves a raw option value: null/empty → <paramref name="defaultValue"/>, an integer ≥ 1 →
    /// itself, <c>auto</c> (case-insensitive) → <see cref="Auto"/>; anything else throws, naming
    /// the option and the valid values.
    /// </summary>
    public static int Resolve(string? raw, string optionName, int defaultValue = 1)
    {
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;

        var value = raw.Trim();
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return Auto;

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1)
            return n;

        throw new ArgumentException(
            $"Invalid value '{raw}' for option '{optionName}'. " +
            "Valid values: an integer >= 1, or 'auto' (= max(CPU count, 2)).");
    }
}

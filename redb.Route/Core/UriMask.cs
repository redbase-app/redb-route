using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace redb.Route.Core;

/// <summary>
/// Endpoint URI mask shared by AdviceWith (<c>MockEndpoints</c>, <c>WeaveByToUri</c>), endpoint
/// interception and the header / property masks. Three pattern forms, checked in order:
/// <list type="bullet">
///   <item><description><c>regex:</c> prefix — a .NET regular expression (case-insensitive, culture-invariant,
///   one-second timeout, compiled once per pattern) matched against the URI as written, without its query
///   string, and with <c>scheme://</c> collapsed to <c>scheme:</c>;</description></item>
///   <item><description>trailing <c>*</c> — case-insensitive prefix match;</description></item>
///   <item><description>otherwise — exact case-insensitive match.</description></item>
/// </list>
/// A pattern without a query string ignores the candidate's query (<c>"kafka://orders"</c> matches
/// <c>"kafka://orders?acks=all"</c>); a pattern that carries one matches only that query
/// (<c>"kafka://orders?acks=all"</c> does not match <c>"?acks=none"</c>). Both <c>scheme://path</c>
/// and <c>scheme:path</c> spellings are tried on either side, so a pattern written one way matches an
/// endpoint written the other.
/// </summary>
public static class UriMask
{
    /// <summary>Prefix selecting the regular-expression pattern form.</summary>
    public const string RegexPrefix = "regex:";

    private static readonly ConcurrentDictionary<string, Regex> Regexes = new(StringComparer.Ordinal);

    /// <summary>
    /// Checks that <paramref name="pattern"/> is a usable mask and throws an <see cref="ArgumentException"/>
    /// naming the problem otherwise (a blank mask, an invalid regular expression). Called where a mask is
    /// declared, so a mistake fails there and not inside route compilation.
    /// </summary>
    public static void Validate(string? pattern, string? paramName = null)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("The URI mask is blank.", paramName ?? nameof(pattern));
        if (!pattern.StartsWith(RegexPrefix, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            _ = CompiledRegex(pattern[RegexPrefix.Length..]);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"The URI mask '{pattern}' is not a valid regular expression: {ex.Message}", paramName ?? nameof(pattern), ex);
        }
    }

    /// <summary>Returns <c>true</c> when <paramref name="uri"/> matches <paramref name="pattern"/>.</summary>
    /// <param name="pattern">Mask: exact URI, prefix ending in <c>*</c>, or <c>regex:&lt;expression&gt;</c>.</param>
    /// <param name="uri">Candidate endpoint URI.</param>
    public static bool IsMatch(string pattern, string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);

        if (pattern.StartsWith(RegexPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var regex = CompiledRegex(pattern[RegexPrefix.Length..]);
            return RegexCandidates(uri).Any(regex.IsMatch);
        }

        if (pattern.EndsWith('*'))
        {
            // The full URI is tried, so a prefix that itself carries a query ("kafka://orders?acks=*")
            // requires that query; a prefix without one matches whatever query the endpoint has.
            var prefix = pattern[..^1];
            return Spellings(prefix).Any(p => Spellings(uri).Any(c => c.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
        }

        var candidate = pattern.Contains('?') ? uri : WithoutQuery(uri);
        return Spellings(pattern).Any(p => Spellings(candidate).Any(c => string.Equals(c, p, StringComparison.OrdinalIgnoreCase)));
    }

    private static Regex CompiledRegex(string expression)
        => Regexes.GetOrAdd(expression, static e => new Regex(e, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));

    /// <summary>What a regular expression is tried against: as written, query-stripped, and the stripped form with <c>scheme://</c> collapsed.</summary>
    private static IEnumerable<string> RegexCandidates(string uri)
    {
        yield return uri;
        var stripped = WithoutQuery(uri);
        if (stripped.Length != uri.Length)
            yield return stripped;
        var collapsed = stripped.Replace("://", ":", StringComparison.Ordinal);
        if (!string.Equals(collapsed, stripped, StringComparison.Ordinal))
            yield return collapsed;
    }

    /// <summary>The two spellings of one URI: as written, and with <c>scheme://</c> collapsed to <c>scheme:</c>.</summary>
    private static IEnumerable<string> Spellings(string uri)
    {
        yield return uri;
        var collapsed = uri.Replace("://", ":", StringComparison.Ordinal);
        if (!string.Equals(collapsed, uri, StringComparison.Ordinal))
            yield return collapsed;
    }

    private static string WithoutQuery(string uri)
    {
        var queryIndex = uri.IndexOf('?');
        return queryIndex >= 0 ? uri[..queryIndex] : uri;
    }
}

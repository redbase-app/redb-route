using System.Text.RegularExpressions;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// The general-purpose <see cref="IHeaderFilterStrategy"/>, Apache Camel's <c>DefaultHeaderFilterStrategy</c>: for
/// each direction a list of names, a regular expression and name prefixes; a header that matches any of them is
/// dropped — or, with <see cref="FilterOnMatch"/> set to <c>false</c>, only a header that matches is kept.
/// <example>
/// Send only <c>Correlation-ID</c> (a whitelist on the outgoing side):
/// <code>
/// context.AddToRegistry("correlationOnly", new DefaultHeaderFilterStrategy
/// {
///     OutFilter = ["Correlation-ID"],
///     FilterOnMatch = false,
/// });
/// From("direct://order").To("http://sap/orders?headerFilterStrategy=#correlationOnly");
/// </code>
/// In markup the same object is a <c>&lt;bean&gt;</c> with the properties of this class.
/// </example>
/// <para>
/// With <see cref="FilterOnMatch"/> <c>false</c> a direction with nothing configured drops every header: a whitelist
/// with no names lets nothing through. A header with a <c>null</c> value is dropped unless
/// <see cref="AllowNullValues"/> is set. Names in the lists are compared without regard to case unless
/// <see cref="CaseInsensitive"/> is turned off.
/// </para>
/// </summary>
public class DefaultHeaderFilterStrategy : IHeaderFilterStrategy
{
    // One reference per direction: the pattern and its source change together, so a thread never pairs a new
    // source with an old compiled pattern (the strategy is shared by every exchange of every route using it).
    private CompiledPattern? _outPattern;
    private CompiledPattern? _inPattern;

    /// <summary>Names matched in the outgoing direction (exchange → message).</summary>
    public IList<string> OutFilter { get; set; } = new List<string>();

    /// <summary>A regular expression matched against the whole name in the outgoing direction.</summary>
    public string? OutFilterPattern { get; set; }

    /// <summary>Name prefixes matched in the outgoing direction.</summary>
    public IList<string> OutFilterStartsWith { get; set; } = new List<string>();

    /// <summary>Names matched in the incoming direction (message → exchange).</summary>
    public IList<string> InFilter { get; set; } = new List<string>();

    /// <summary>A regular expression matched against the whole name in the incoming direction.</summary>
    public string? InFilterPattern { get; set; }

    /// <summary>Name prefixes matched in the incoming direction.</summary>
    public IList<string> InFilterStartsWith { get; set; } = new List<string>();

    /// <summary>
    /// What a match means. <c>true</c> (the default): a matching header is dropped — a blacklist. <c>false</c>: only
    /// a matching header is kept — a whitelist.
    /// </summary>
    public bool FilterOnMatch { get; set; } = true;

    /// <summary>
    /// Compare names in the lists without regard to case; <c>true</c> by default, as in Camel (header names of most
    /// protocols are case-insensitive). The prefixes and the pattern are not affected.
    /// </summary>
    public bool CaseInsensitive { get; set; } = true;

    /// <summary>
    /// Also match the lower-cased name against the prefixes and, when <see cref="CaseInsensitive"/> is off, the lists.
    /// The pattern is matched against the name as it is, as in Camel.
    /// </summary>
    public bool LowerCase { get; set; }

    /// <summary>Let a header with a <c>null</c> value through; by default it is dropped.</summary>
    public bool AllowNullValues { get; set; }

    /// <inheritdoc />
    public virtual bool FilterOutgoing(string name, object? value, IExchange exchange)
        => Filter(name, value, OutFilter, OutFilterStartsWith, OutRegex());

    /// <inheritdoc />
    public virtual bool FilterIncoming(string name, object? value, IExchange exchange)
        => Filter(name, value, InFilter, InFilterStartsWith, InRegex());

    private bool Filter(string name, object? value, IList<string> names, IList<string> prefixes, Regex? pattern)
    {
        if (string.IsNullOrEmpty(name))
            return true;
        if (value is null && !AllowNullValues)
            return true;

        var lower = LowerCase ? name.ToLowerInvariant() : null;

        foreach (var prefix in prefixes)
        {
            if (string.IsNullOrEmpty(prefix))
                continue;
            if (name.StartsWith(prefix, StringComparison.Ordinal)
                || (lower is not null && lower.StartsWith(prefix, StringComparison.Ordinal)))
                return FilterOnMatch;
        }

        if (pattern is not null && pattern.IsMatch(name))
            return FilterOnMatch;

        var comparison = CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var candidate in names)
        {
            if (string.Equals(candidate, name, comparison) || (lower is not null && string.Equals(candidate, lower, comparison)))
                return FilterOnMatch;
        }

        // Nothing matched: a blacklist lets the header through, a whitelist drops it.
        return !FilterOnMatch;
    }

    private Regex? OutRegex() => Compiled(OutFilterPattern, ref _outPattern);

    private Regex? InRegex() => Compiled(InFilterPattern, ref _inPattern);

    /// <summary>The pattern compiled once per value of the property; matched against the whole name, as in Camel.</summary>
    private static Regex? Compiled(string? source, ref CompiledPattern? cache)
    {
        if (string.IsNullOrEmpty(source))
            return null;
        var current = Volatile.Read(ref cache);
        if (current is null || !string.Equals(current.Source, source, StringComparison.Ordinal))
        {
            current = new CompiledPattern(source,
                new Regex($"^(?:{source})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
            Volatile.Write(ref cache, current);
        }
        return current.Pattern;
    }

    private sealed record CompiledPattern(string Source, Regex Pattern);
}

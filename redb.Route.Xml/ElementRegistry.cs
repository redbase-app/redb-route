namespace redb.Route.Xml;

/// <summary>
/// The element registry (Р21): one name — one contribution, the union of the core set and the
/// package extensions of a concrete installation. A duplicate name is a hard build error, never
/// a silent override; the parser, the schema and the catalog all read this one list.
/// </summary>
public sealed class ElementRegistry
{
    private readonly Dictionary<string, IXmlElementContribution> _byName = new(StringComparer.Ordinal);

    /// <summary>Registers one contribution. A duplicate element name is refused loudly.</summary>
    public void Add(IXmlElementContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (!_byName.TryAdd(contribution.Name, contribution))
            throw new InvalidOperationException(
                $"XML element '{contribution.Name}' is already registered by " +
                $"{_byName[contribution.Name].GetType().FullName}. One name — one contribution; " +
                "rename the element or drop the duplicate registration.");
    }

    /// <summary>The contribution for a local element name, or null.</summary>
    public IXmlElementContribution? Find(string localName)
        => _byName.GetValueOrDefault(localName);

    /// <summary>Every registered element name (for diagnostics and the "did you mean" hint).</summary>
    public IReadOnlyCollection<string> Names => _byName.Keys;

    /// <summary>Every registered contribution — the one list the schema and the catalog read (Ф4 §2).</summary>
    public IReadOnlyCollection<IXmlElementContribution> Contributions => _byName.Values;

    /// <summary>The closest registered name by edit distance, or null when nothing is close.</summary>
    internal string? ClosestTo(string name)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in _byName.Keys)
        {
            var d = Levenshtein(name, candidate);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = candidate;
            }
        }
        return bestDistance <= Math.Max(2, name.Length / 3) ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>Builds the registry: the core contributions plus the supplied extensions.</summary>
    public static ElementRegistry CreateDefault(IReadOnlyList<IXmlElementContribution>? extensions = null)
    {
        var registry = new ElementRegistry();
        foreach (var contribution in CoreElements.CoreContributions.All)
            registry.Add(contribution);
        if (extensions is not null)
            foreach (var contribution in extensions)
                registry.Add(contribution);
        return registry;
    }
}

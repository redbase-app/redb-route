namespace redb.Route.DataFormats.XmlJson.Internal;

/// <summary>
/// The <see cref="XmlJsonOptions.Arrays"/> paths, parsed once: <c>/a/b/c</c> matches the element path from the root,
/// <c>//b/c</c> matches its end at any depth. A step without a prefix compares with the local name, a step with one
/// compares with the name as the document writes it.
/// </summary>
internal sealed class ArrayPaths
{
    private readonly List<(bool Anywhere, string[] Steps)> _paths = [];

    public ArrayPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            _paths.Add(Parse(path));
    }

    /// <summary>Whether no path is given.</summary>
    public bool IsEmpty => _paths.Count == 0;

    private static (bool, string[]) Parse(string path)
    {
        var anywhere = path.StartsWith("//", StringComparison.Ordinal);
        if (!anywhere && !path.StartsWith('/'))
            throw new ArgumentException(
                $"xmljson arrays path '{path}' starts with '/' (from the root) or '//' (at any depth).", nameof(path));
        var steps = path[(anywhere ? 2 : 1)..].Split('/');
        if (steps.Any(s => s.Length == 0 || s.Any(char.IsWhiteSpace)))
            throw new ArgumentException(
                $"xmljson arrays path '{path}' has an empty or blank step; write it as /Order/Items/Item or //Item.",
                nameof(path));
        return (anywhere, steps);
    }

    /// <summary>
    /// Whether the element at the end of <paramref name="path"/> (local and written names, root first) is an array.
    /// </summary>
    public bool Matches(IReadOnlyList<(string Local, string Written)> path)
    {
        foreach (var (anywhere, steps) in _paths)
        {
            if (steps.Length > path.Count || (!anywhere && steps.Length != path.Count))
                continue;
            var offset = path.Count - steps.Length;
            var all = true;
            for (var i = 0; i < steps.Length && all; i++)
            {
                var (local, written) = path[offset + i];
                all = steps[i].Contains(':')
                    ? string.Equals(steps[i], written, StringComparison.Ordinal)
                    : string.Equals(steps[i], local, StringComparison.Ordinal);
            }
            if (all)
                return true;
        }
        return false;
    }
}

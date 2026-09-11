using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// Default <see cref="IRouteResourceResolver"/>: probes, in order, the reference as an absolute
/// path, <see cref="ResourceRoot"/> (when set), <see cref="AppContext.BaseDirectory"/>, and the
/// current working directory. The working directory is deliberately LAST: it preserves the
/// pre-resolver behaviour (a bare <c>File.Exists</c>) as the final fallback rather than the rule.
/// </summary>
public sealed class RouteResourceResolver : IRouteResourceResolver
{
    /// <summary>The shared instance used when no resolver is registered on the context.</summary>
    public static RouteResourceResolver Default { get; } = new();

    /// <summary>Optional search base probed first for relative references (e.g. a package root).</summary>
    public string? ResourceRoot { get; init; }

    /// <inheritdoc />
    public string? Resolve(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return Candidates(reference).FirstOrDefault(File.Exists);
    }

    /// <inheritdoc />
    public string DescribeSearch(string reference) => string.Join(", ", Candidates(reference));

    private IEnumerable<string> Candidates(string reference)
    {
        if (Path.IsPathRooted(reference))
        {
            yield return reference;
            yield break;
        }
        if (!string.IsNullOrWhiteSpace(ResourceRoot))
            yield return Path.GetFullPath(Path.Combine(ResourceRoot, reference));
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, reference));
        yield return Path.GetFullPath(reference); // current working directory — last on purpose
    }
}

/// <summary>Shared resolve-or-throw used by the components that read files from endpoint URIs.</summary>
internal static class ResourceResolution
{
    /// <summary>
    /// Resolves <paramref name="reference"/> through the context's registered
    /// <see cref="IRouteResourceResolver"/> (or <see cref="RouteResourceResolver.Default"/>) and
    /// returns the absolute path. Throws <see cref="FileNotFoundException"/> naming every probed
    /// location when the resource does not exist.
    /// </summary>
    public static string Resolve(IRouteContext? context, string reference, string what)
    {
        var resolver = context?.GetService<IRouteResourceResolver>() ?? RouteResourceResolver.Default;
        var resolved = resolver.Resolve(reference);
        if (resolved is null)
            throw new FileNotFoundException(
                $"{what} not found: '{reference}'. Searched: {resolver.DescribeSearch(reference)}.");
        return resolved;
    }
}

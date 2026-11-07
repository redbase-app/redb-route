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
public static class ResourceResolution
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

    /// <summary>
    /// Pins a <see cref="TextSource"/> that names a file by a relative path to the file the context
    /// can actually see: the registered <see cref="IRouteResourceResolver"/> first (a package keeps
    /// its resources where only the resolver knows), then <paramref name="baseDirectory"/>, which is
    /// what the consuming package was configured with. Inline text, an embedded resource and an
    /// absolute path are returned untouched.
    /// <para>
    /// The returned source names the resolved file, so a compile cache keyed by it distinguishes two
    /// packages that ship the same relative name — a hot-reloaded module must not render the previous
    /// version's text.
    /// </para>
    /// </summary>
    /// <param name="context">Route context whose resolver is consulted; null falls back to the default one.</param>
    /// <param name="source">The source as the route author wrote it.</param>
    /// <param name="baseDirectory">The package's own base directory, probed after the resolver.</param>
    /// <param name="what">Noun for the error message, e.g. "Template".</param>
    /// <exception cref="FileNotFoundException">Neither place holds the file; the message names both.</exception>
    public static TextSource Locate(IRouteContext? context, TextSource source, string baseDirectory, string what)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.FilePath is not { } reference || Path.IsPathRooted(reference))
            return source;

        var resolver = context?.GetService<IRouteResourceResolver>() ?? RouteResourceResolver.Default;
        if (resolver.Resolve(reference) is { } resolved)
            return TextSource.Located(reference, resolved);

        // Not known to the resolver: the package's own base directory is the second place, and the
        // only one the pre-resolver behaviour ever used.
        var fromBase = Path.GetFullPath(Path.Combine(baseDirectory, reference));
        if (System.IO.File.Exists(fromBase))
            return TextSource.Located(reference, fromBase);

        // Both places in one line, each once: the default resolver already probes the process base
        // directory, which for most packages is the base directory as well.
        var searched = resolver.DescribeSearch(reference)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(fromBase)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        throw new FileNotFoundException(
            $"{what} not found: '{reference}'. Searched: {string.Join(", ", searched)}.",
            fromBase);
    }
}

using System.Xml.Linq;
using redb.Route.Core;

namespace redb.Route.Xml;

/// <summary>
/// Loading sugar on <see cref="RouteContext"/> — the TestKit-style surface
/// (<c>new RouteContext().AddXmlRoutesFromContent(...)</c>) and file loading. Both go through one
/// <see cref="XmlRouteLoader"/>.
/// </summary>
public static class RouteContextXmlExtensions
{
    /// <summary>Parses the XML text and registers its routes. Errors carry positions and are aggregated.</summary>
    public static RouteContext AddXmlRoutesFromContent(this RouteContext context, string xml,
        string? sourceName = null, XmlRouteLoaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        var document = XDocument.Parse(xml, LoadOptions.SetLineInfo);
        new XmlRouteLoader(context, options).Load(document, sourceName);
        return context;
    }

    /// <summary>Loads every named <c>.route.xml</c> file, each parsed with its own positions.</summary>
    public static RouteContext AddXmlRoutes(this RouteContext context, params string[] filePaths)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (filePaths.Length == 0)
            throw new ArgumentException("At least one route file path is required.", nameof(filePaths));
        var loader = new XmlRouteLoader(context);
        foreach (var path in filePaths)
            LoadFile(loader, context, path);
        return context;
    }

    /// <summary>
    /// Loads route files by a glob pattern (<c>"routes/*.route.xml"</c>); a plain path without
    /// wildcards loads that one file. A pattern matching nothing is an error, not a silent empty
    /// context — same fail-fast rule the rest of the loader follows.
    /// </summary>
    public static RouteContext AddXmlRoutes(this RouteContext context, string globPattern,
        XmlRouteLoaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(globPattern);
        var loader = new XmlRouteLoader(context, options);
        if (!globPattern.Contains('*') && !globPattern.Contains('?'))
        {
            LoadFile(loader, context, globPattern);
            return context;
        }

        var directory = Path.GetDirectoryName(globPattern) is { Length: > 0 } dir ? dir : ".";
        var mask = Path.GetFileName(globPattern);
        var probed = new List<string>();
        string? found = null;
        foreach (var root in ProbeRoots(directory))
        {
            probed.Add(root);
            if (Directory.Exists(root) && Directory.EnumerateFiles(root, mask).Any())
            {
                found = root;
                break;
            }
        }
        if (found is null)
        {
            throw new XmlRouteException(globPattern,
                [$"no route files match '{mask}'. Searched: {string.Join("; ", probed.Distinct())}."]);
        }
        // Ordinal order keeps the load deterministic across file systems.
        foreach (var path in Directory.GetFiles(found, mask).OrderBy(p => p, StringComparer.Ordinal))
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            loader.Load(document, path);
        }
        return context;
    }

    /// <summary>Parses a <c>context.xml</c> text: components, context beans, the onInit pipeline.</summary>
    public static RouteContext AddXmlContextFromContent(this RouteContext context, string xml,
        string? sourceName = null, XmlRouteLoaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        var document = XDocument.Parse(xml, LoadOptions.SetLineInfo);
        new XmlContextLoader(context, options).Load(document, sourceName);
        return context;
    }

    /// <summary>Loads a <c>context.xml</c> file through the route resource resolver.</summary>
    public static RouteContext AddXmlContext(this RouteContext context, string filePath,
        XmlRouteLoaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var resolved = ResourceResolution.Resolve(context, filePath, "Context file");
        var document = XDocument.Load(resolved, LoadOptions.SetLineInfo);
        new XmlContextLoader(context, options).Load(document, filePath);
        return context;
    }

    private static void LoadFile(XmlRouteLoader loader, RouteContext context, string path)
    {
        // Through the route resource resolver: a relative path must not depend on the process
        // working directory (the AppContext base wins over cwd).
        var resolved = ResourceResolution.Resolve(context, path, "Route file");
        var document = XDocument.Load(resolved, LoadOptions.SetLineInfo);
        loader.Load(document, path);
    }

    private static IEnumerable<string> ProbeRoots(string directory)
    {
        if (Path.IsPathRooted(directory))
        {
            yield return directory;
            yield break;
        }
        yield return Path.Combine(AppContext.BaseDirectory, directory);
        yield return Path.GetFullPath(directory);
    }
}

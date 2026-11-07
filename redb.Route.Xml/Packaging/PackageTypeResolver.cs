using System.Reflection;
using System.Runtime.Loader;

namespace redb.Route.Xml.Packaging;

/// <summary>
/// The type lookup of the packaging checks with a build output at hand (<c>redb-route-xml check|pack --bin</c>): the
/// assemblies of that output first, then the shared framework the worker runs on. A worker resolves a markup type name
/// against everything its process has loaded, which includes framework assemblies a project's <c>bin/</c> never carries
/// (<c>System.Net.Http</c> for <see cref="System.Net.Http.HttpRequestException"/>); a check that looked in <c>bin/</c> only
/// refused markup the worker runs.
/// </summary>
public static class PackageTypeResolver
{
    /// <summary>
    /// Loads every assembly of <paramref name="binDir"/> into a load context of its own and returns the lookup.
    /// A type name is <c>Full.Name</c> or <c>Full.Name, Assembly</c>.
    /// </summary>
    /// <param name="binDir">The build output directory.</param>
    /// <param name="warn">Receives a line for every DLL in the directory that is not a loadable assembly.</param>
    public static Func<string, Type?> Create(string binDir, Action<string>? warn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binDir);

        var context = new AssemblyLoadContext("route-pack-check", isCollectible: false);
        context.Resolving += (loadContext, assemblyName) =>
        {
            var candidate = Path.Combine(binDir, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? loadContext.LoadFromAssemblyPath(candidate) : null;
        };
        foreach (var dll in Directory.GetFiles(binDir, "*.dll"))
        {
            try
            {
                context.LoadFromAssemblyPath(dll);
            }
            catch (BadImageFormatException)
            {
                warn?.Invoke($"warning: {Path.GetFileName(dll)} is not a loadable assembly — skipped by the deep bean checks.");
            }
        }

        return typeName =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
            var comma = typeName.IndexOf(',');
            var fullName = (comma >= 0 ? typeName[..comma] : typeName).Trim();
            var assemblyName = comma >= 0 ? typeName[(comma + 1)..].Trim() : null;

            var assemblies = assemblyName is null
                ? context.Assemblies
                : context.Assemblies.Where(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
            foreach (var assembly in assemblies)
            {
                if (assembly.GetType(fullName) is { } type)
                    return type;
            }

            return Type.GetType(typeName, throwOnError: false) ?? FrameworkType(fullName, assemblyName);
        };
    }

    /// <summary>
    /// A type of the shared framework this process runs on: in the named assembly, or else in the assembly named like
    /// the type's namespace or a prefix of it (<c>System.Net.Http.HttpRequestException</c> → <c>System.Net.Http</c>), and
    /// only then in any framework assembly. Framework assemblies resolve their forwarders, so a type that moved
    /// (<c>System.Net.Sockets.SocketException</c>) is found through the assembly its name suggests.
    /// </summary>
    internal static Type? FrameworkType(string fullName, string? assemblyName)
    {
        var platform = PlatformAssemblies();

        if (assemblyName is not null)
            return platform.TryGetValue(assemblyName, out var named) ? Load(named)?.GetType(fullName) : null;

        for (var dot = fullName.LastIndexOf('.'); dot > 0; dot = fullName.LastIndexOf('.', dot - 1))
        {
            if (platform.TryGetValue(fullName[..dot], out var path) && Load(path)?.GetType(fullName) is { } type)
                return type;
        }

        foreach (var path in platform.Values)
        {
            if (Load(path)?.GetType(fullName) is { } type)
                return type;
        }
        return null;
    }

    private static Dictionary<string, string> PlatformAssemblies() => Platform.Value;

    // The shared framework only (the directory System.Private.CoreLib is loaded from), not the tool's own dependencies,
    // which the trusted platform list also holds: the check must not accept a type that neither bin/ nor the framework has.
    private static readonly Lazy<Dictionary<string, string>> Platform = new(() =>
    {
        var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? string.Empty;
        var list = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(Path.GetDirectoryName(path), frameworkDir, StringComparison.OrdinalIgnoreCase))
                byName.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        }
        return byName;
    });

    private static Assembly? Load(string path)
    {
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(AssemblyName.GetAssemblyName(path));
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            return null;
        }
    }
}

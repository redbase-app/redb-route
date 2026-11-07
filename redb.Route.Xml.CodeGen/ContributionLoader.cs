using System.Reflection;
using System.Runtime.Loader;
using redb.Route.Xml.Packaging;

namespace redb.Route.Xml.CodeGen;

/// <summary>
/// Reads what the connector assemblies of a <c>--bin</c> directory bring to the tool: their XML element contributions
/// and their package checks (<see cref="IPackageCheck"/>).
/// </summary>
internal static class ContributionLoader
{
    /// <summary>
    /// Loads every <c>redb.Route.*.dll</c> of <paramref name="binDir"/> into its own load context and creates its
    /// contributions. An assembly the tool itself runs on (<paramref name="shared"/> returns it: redb.Route.Xml above
    /// all) is never loaded from the directory: a second copy would give the connectors a different
    /// <see cref="IXmlElementContribution"/> type, the cast would fail and the check would report every connector
    /// element as unknown. The tool's own assemblies are not scanned either, their elements are built in.
    /// </summary>
    public static List<IXmlElementContribution> Load(string binDir, Func<string?, Assembly?> shared)
        => LoadAll(binDir, shared).Contributions;

    /// <summary>
    /// The contributions and the package checks of <paramref name="binDir"/>, read in one load context, as
    /// <see cref="Load(string, Func{string?, Assembly?})"/> reads the contributions.
    /// </summary>
    public static (List<IXmlElementContribution> Contributions, List<IPackageCheck> Checks) LoadAll(
        string binDir, Func<string?, Assembly?> shared)
        // The file order is the file system's: alphabetical on NTFS, not on APFS or ext4. Nothing below may depend
        // on which file comes first.
        => LoadAll(binDir, Directory.GetFiles(binDir, "redb.Route.*.dll"), shared);

    /// <summary>
    /// Loads the given assemblies of <paramref name="binDir"/> in the given order. The tests call it with both orders
    /// of the same files, because a directory listing gives whichever order its file system keeps.
    /// </summary>
    public static List<IXmlElementContribution> Load(
        string binDir, IReadOnlyList<string> dlls, Func<string?, Assembly?> shared)
        => LoadAll(binDir, dlls, shared).Contributions;

    /// <summary>The contributions and the package checks of the given assemblies, in the given order.</summary>
    public static (List<IXmlElementContribution> Contributions, List<IPackageCheck> Checks) LoadAll(
        string binDir, IReadOnlyList<string> dlls, Func<string?, Assembly?> shared)
    {
        var contributions = new List<IXmlElementContribution>();
        var checks = new List<IPackageCheck>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var context = new AssemblyLoadContext($"contrib:{binDir}", isCollectible: false);
        context.Resolving += (loadContext, assemblyName) =>
        {
            if (shared(assemblyName.Name) is { } own) return own;
            var candidate = Path.Combine(binDir, assemblyName.Name + ".dll");
            return File.Exists(candidate) ? loadContext.LoadFromAssemblyPath(candidate) : null;
        };
        foreach (var dll in dlls)
        {
            Assembly assembly;
            try
            {
                var simpleName = AssemblyName.GetAssemblyName(dll).Name;
                if (shared(simpleName) is not null)
                    continue;
                // The Resolving hook may have already pulled this simple name while satisfying a dependency; loading
                // the local copy on top would throw «assembly with same name is already loaded». First copy wins.
                assembly = context.Assemblies.FirstOrDefault(a => a.GetName().Name == simpleName)
                    ?? context.LoadFromAssemblyPath(dll);
            }
            catch (BadImageFormatException)
            {
                continue;
            }
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.Where(t => t is not null)!];
            }
            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) is null)
                    continue;
                var contribution = typeof(IXmlElementContribution).IsAssignableFrom(type);
                var check = type.IsPublic && typeof(IPackageCheck).IsAssignableFrom(type);
                if ((!contribution && !check) || !seen.Add(type.FullName ?? type.Name))
                    continue;
                var instance = Activator.CreateInstance(type)!;
                if (contribution)
                    contributions.Add((IXmlElementContribution)instance);
                if (check)
                    checks.Add((IPackageCheck)instance);
            }
        }
        return (contributions, checks);
    }
}

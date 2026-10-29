using System.Reflection;
using System.Runtime.Loader;

namespace redb.Route.Xml.CodeGen;

/// <summary>Reads the XML element contributions of the connector assemblies in a <c>--bin</c> directory.</summary>
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
        // The file order is the file system's: alphabetical on NTFS, not on APFS or ext4. Nothing below may depend
        // on which file comes first.
        => Load(binDir, Directory.GetFiles(binDir, "redb.Route.*.dll"), shared);

    /// <summary>
    /// Loads the given assemblies of <paramref name="binDir"/> in the given order. The tests call it with both orders
    /// of the same files, because a directory listing gives whichever order its file system keeps.
    /// </summary>
    public static List<IXmlElementContribution> Load(
        string binDir, IReadOnlyList<string> dlls, Func<string?, Assembly?> shared)
    {
        var contributions = new List<IXmlElementContribution>();
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
                if (type.IsAbstract || type.IsInterface
                    || !typeof(IXmlElementContribution).IsAssignableFrom(type)
                    || type.GetConstructor(Type.EmptyTypes) is null
                    || !seen.Add(type.FullName ?? type.Name))
                    continue;
                contributions.Add((IXmlElementContribution)Activator.CreateInstance(type)!);
            }
        }
        return contributions;
    }
}

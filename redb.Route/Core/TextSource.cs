using System.Reflection;

namespace redb.Route.Core;

/// <summary>
/// Where a piece of configuration text comes from — a Scriban template, a JSONata specification, a
/// schema: a file (relative to a base directory), an embedded resource, or inline text. Shared by the
/// packages that consume such text so they follow one rule: a plain <c>string</c> in the DSL is always
/// a <b>locator</b> (<c>"Templates/order.json.sbn"</c> or <c>"assembly:Acme.Orders/Templates/order.json.sbn"</c>);
/// inline text must be marked with <see cref="Inline"/>. A string never has to be guessed as
/// "path or text" — the rule the route expression language follows too.
/// </summary>
public abstract class TextSource
{
    /// <summary>Prefix of an embedded-resource locator: <c>assembly:&lt;AssemblyName&gt;/&lt;resource path&gt;</c>.</summary>
    public const string AssemblyPrefix = "assembly:";

    /// <summary>Human-readable name used in error messages (file path, resource locator, or <c>&lt;inline&gt;</c>).</summary>
    public abstract string Name { get; }

    /// <summary>Stable identity of the source as written: two sources with the same key name the same thing.</summary>
    public abstract string CacheKey { get; }

    /// <summary>
    /// Identity for compile caches once the base directory is known. A file is keyed by its resolved
    /// path plus its size and last write time, so two configurations pointing at different directories
    /// never share a compiled object and an edited file compiles afresh on the next route build (a
    /// hot-reloaded module in the same process included). Other sources return <see cref="CacheKey"/>.
    /// </summary>
    public virtual string ResolveCacheKey(string baseDirectory) => CacheKey;

    /// <summary>Reads the text. A relative file path is resolved against <paramref name="baseDirectory"/>.</summary>
    public abstract string Read(string baseDirectory);

    /// <summary>
    /// The file path this source was written with, or <c>null</c> when the text does not come from a
    /// file. Lets a package look the path up the way the rest of the framework does (see
    /// <see cref="ResourceResolution.Locate"/>) before falling back to its own base directory.
    /// </summary>
    public virtual string? FilePath => null;

    /// <summary>Inline text.</summary>
    public static TextSource Inline(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new InlineTextSource(text);
    }

    /// <summary>A file; a relative path is resolved against the base directory the consuming package is configured with.</summary>
    public static TextSource File(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new FileTextSource(path);
    }

    /// <summary>
    /// A file that has already been located (see <see cref="ResourceResolution.Locate"/>): read and
    /// cached by <paramref name="fullPath"/>, but still named the way the route author wrote it, so
    /// an error message says <c>templates/order.sbn</c> and not the unpacked package's temp path.
    /// </summary>
    internal static TextSource Located(string writtenAs, string fullPath) => new ResolvedFileTextSource(writtenAs, fullPath);

    /// <summary>An embedded resource of <paramref name="assembly"/>; <paramref name="resourcePath"/> may use <c>/</c> or <c>.</c> separators and may omit the assembly-name prefix.</summary>
    public static TextSource Embedded(Assembly assembly, string resourcePath)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        return new EmbeddedTextSource(assembly, resourcePath);
    }

    /// <summary>Parses a DSL / XML locator: <c>assembly:Name/path</c> → embedded resource, anything else → file path.</summary>
    public static TextSource FromLocator(string locator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locator);
        if (!locator.StartsWith(AssemblyPrefix, StringComparison.OrdinalIgnoreCase))
            return File(locator);

        var rest = locator[AssemblyPrefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1)
            throw new ArgumentException($"Embedded locator must look like 'assembly:AssemblyName/Path/To/file', got '{locator}'.", nameof(locator));

        var assemblyName = rest[..slash];
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
            ?? Assembly.Load(assemblyName);
        return Embedded(assembly, rest[(slash + 1)..]);
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

internal sealed class InlineTextSource(string text) : TextSource
{
    public override string Name => "<inline>";
    public override string CacheKey => "inline:" + text;
    public override string Read(string baseDirectory) => text;
}

internal sealed class FileTextSource(string path) : TextSource
{
    public override string Name => path;
    public override string CacheKey => "file:" + path;
    public override string? FilePath => path;

    public override string ResolveCacheKey(string baseDirectory)
    {
        var full = Resolve(baseDirectory);
        var info = new FileInfo(full);
        return info.Exists ? $"file:{full}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : "file:" + full;
    }

    private string Resolve(string baseDirectory)
        => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(baseDirectory, path));

    public override string Read(string baseDirectory)
    {
        var full = Resolve(baseDirectory);
        if (!System.IO.File.Exists(full))
            throw new FileNotFoundException($"File '{path}' not found (resolved to '{full}'; base directory '{baseDirectory}').", full);
        return System.IO.File.ReadAllText(full);
    }
}

internal sealed class ResolvedFileTextSource(string writtenAs, string fullPath) : TextSource
{
    public override string Name => writtenAs;
    public override string CacheKey => "file:" + fullPath;
    public override string? FilePath => fullPath;

    public override string ResolveCacheKey(string baseDirectory)
    {
        var info = new FileInfo(fullPath);
        return info.Exists ? $"file:{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : "file:" + fullPath;
    }

    public override string Read(string baseDirectory)
    {
        if (!System.IO.File.Exists(fullPath))
            throw new FileNotFoundException($"File '{writtenAs}' not found (resolved to '{fullPath}').", fullPath);
        return System.IO.File.ReadAllText(fullPath);
    }
}

internal sealed class EmbeddedTextSource(Assembly assembly, string resourcePath) : TextSource
{
    public override string Name => $"{AssemblyPrefix}{assembly.GetName().Name}/{resourcePath}";
    public override string CacheKey => "embedded:" + Name;

    public override string Read(string baseDirectory)
    {
        var names = assembly.GetManifestResourceNames();
        var dotted = resourcePath.Replace('/', '.').Replace('\\', '.');
        var candidates = new[] { resourcePath, dotted, $"{assembly.GetName().Name}.{dotted}" };
        var match = candidates.FirstOrDefault(c => names.Contains(c, StringComparer.Ordinal))
            ?? names.FirstOrDefault(n => n.EndsWith("." + dotted, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException(
                $"Embedded resource '{resourcePath}' not found in assembly '{assembly.GetName().Name}'. Available resources: {string.Join(", ", names)}");

        using var stream = assembly.GetManifestResourceStream(match)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

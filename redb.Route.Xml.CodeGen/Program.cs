using System.Xml.Linq;
using redb.Route.Xml;

// redb-route-xml — the Ф6 developer tool (docs/Route-XML/08-PHASE6-CODEGEN.md §7).
// Not part of the runtime package: it serves the developer, not the running service.
//
//   redb-route-xml csharp  <file.route.xml> --namespace My.Routes [--class Name] [--style readable|machine] [--out dir]
//   redb-route-xml mermaid <file.route.xml> [--out dir]
//   redb-route-xml xsd     [--out dir]
//
// csharp:  prints (or writes) the fluent C# the file parses into.
// mermaid: prints (or writes) the flowchart for the file's routes (loaded with lax scheme
//          checking off — the tool has no components; use it on documents whose schemes you know).
// xsd:     prints (or writes) the generated schema for the core element set.

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: redb-route-xml new|pack|check|csharp|mermaid|xsd … " +
                            "(new <Name> [--context name] [--dir parent]; " +
                            "pack <projectDir> --version 1.0.0 [--name n] [--out dir] [--entry dll]; " +
                            "check <projectDir> [--name n])");
    return 2;
}

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var command = args[0];
var outDir = Option("--out");

try
{
    switch (command)
    {
        case "csharp":
        {
            var file = args.ElementAtOrDefault(1)
                ?? throw new ArgumentException("csharp needs the .route.xml file path.");
            var ns = Option("--namespace")
                ?? throw new ArgumentException("csharp needs --namespace <My.Routes>.");
            var className = Option("--class") ?? DefaultClassName(file);
            var style = Option("--style") == "machine" ? CodeGenStyle.Machine : CodeGenStyle.Readable;
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            var code = XmlCodeGenerator.Generate(document, className, ns, style, Path.GetFullPath(file));
            Emit(code, outDir, className + ".cs");
            return 0;
        }
        case "mermaid":
        {
            var file = args.ElementAtOrDefault(1)
                ?? throw new ArgumentException("mermaid needs the .route.xml file path.");
            // The tool builds the definition tree in a scratch context. A diagram needs neither
            // live beans nor real predicates, so the document copy loses its <bean> sections,
            // registry predicates get stand-ins, and unknown schemes get stub components —
            // the drawn structure is exactly the parsed structure.
            var diagramDoc = XDocument.Load(Path.GetFullPath(file), LoadOptions.SetLineInfo);
            using var context = new redb.Route.Core.RouteContext();
            foreach (var bean in diagramDoc.Root!.Elements().Where(e => e.Name.LocalName == "bean").ToList())
                bean.Remove();
            foreach (var predicate in diagramDoc.Descendants().Attributes("predicate").Select(a => a.Value).Distinct())
                context.AddToRegistry(predicate.TrimStart('#'), new PredicateStub());
            foreach (var scheme in CollectSchemes(file))
                if (context.GetComponent<redb.Route.Abstractions.IComponent>(scheme) is null)
                    context.AddComponent(new SchemeStub(scheme));
            context.AddXmlRoutesFromContent(diagramDoc.ToString(), Path.GetFullPath(file));
            var definitions = new List<redb.Route.Abstractions.IProcessorDefinition>();
            foreach (var builder in context.RouteBuilders)
            {
                if (!builder.IsBuilt)
                    builder.InternalBuild(context);
                definitions.AddRange(builder.ExceptionDefinitions);
                definitions.AddRange(builder.Intercepts);
                definitions.AddRange(builder.OnCompletions);
                definitions.AddRange(builder.Definitions);
            }
            var diagram = redb.Route.Diagnostics.MermaidRenderer.Render(
                definitions, Option("--direction") ?? "TD");
            Emit(diagram, outDir, Path.GetFileNameWithoutExtension(file) + ".mmd");
            return 0;
        }
        case "xsd":
        {
            // xsd [binDir...] [--out dir] — package contributions ride in the same way
            // `elements` takes them, so a project's local schema knows its <redbSave>/<cache>
            // exactly like the pack gate does (otherwise the IDE flags every package element).
            var xsdBinDirs = args.Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
            var xsdContributions = xsdBinDirs
                .SelectMany(d => LoadContributions(Path.GetFullPath(d)))
                .DistinctBy(c => c.Name)
                .ToList();
            var schema = XmlRouteSchema.Generate(
                ElementRegistry.CreateDefault(xsdContributions.Count > 0 ? xsdContributions : null)).ToString();
            Emit(schema, outDir, "redb-route-1.0.xsd");
            return 0;
        }
        case "elements":
        {
            // elements [binDir...] [--out dir] — every element spec of the registry as JSON:
            // the machine-readable list 09-UX §6 asks for at the start of Ф8 (palette,
            // categories, property panels). --bins add package contributions the same way
            // the worker's discovery sees them.
            var binDirs = args.Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
            var contributions = binDirs
                .SelectMany(d => LoadContributions(Path.GetFullPath(d)))
                .DistinctBy(c => c.Name)
                .ToList();
            var elementRegistry = ElementRegistry.CreateDefault(contributions.Count > 0 ? contributions : null);
            object Describe(redb.Route.Xml.ElementSpec spec) => new
            {
                name = spec.Name,
                kind = spec.Kind.ToString(),
                attributes = spec.Attributes.Select(a => new
                {
                    name = a.Name,
                    type = a.Type.ToString(),
                    required = a.Required,
                    enumValues = a.EnumValues,
                }),
                children = spec.Children.Select(Describe),
                allowsSteps = spec.AllowsSteps,
                allowsText = spec.AllowsTextContent,
                takesEndpoint = spec.TakesEndpoint,
            };
            var json = System.Text.Json.JsonSerializer.Serialize(
                elementRegistry.Contributions.Select(c => c.Spec).OrderBy(s => s.Name, StringComparer.Ordinal).Select(Describe),
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                });
            Emit(json, outDir, "redb-route-elements.json");
            return 0;
        }
        case "new":
        {
            var name = args.ElementAtOrDefault(1)
                ?? throw new ArgumentException("new needs the project name.");
            var dir = redb.Route.Xml.Packaging.RouteProjectScaffold.Create(
                Option("--dir") ?? Directory.GetCurrentDirectory(), name, Option("--context"),
                Option("--runtime-ref"));
            Console.Out.WriteLine(dir);
            return 0;
        }
        case "check":
        case "pack":
        {
            var projectDir = Path.GetFullPath(args.ElementAtOrDefault(1)
                ?? throw new ArgumentException($"{command} needs the project directory."));
            var name = Option("--name") ?? InferPackageName(projectDir);
            var version = Option("--version") ?? "0.0.0";
            // --bin <dir>: the built output — turns the bean checks deep (types, properties,
            // method= targets are verified against the real assemblies) and brings the Р21
            // markup contributions those assemblies carry (<cache>, <redbQuery>, …) into the
            // XSD gate, the way the worker's own discovery will see them at load.
            var resolver = Option("--bin") is { } binDir ? BuildBinResolver(Path.GetFullPath(binDir)) : null;
            var extensions = Option("--bin") is { } contribDir
                ? LoadContributions(Path.GetFullPath(contribDir))
                : null;
            if (extensions is not null)
                Console.Error.WriteLine($"--bin contributions: {extensions.Count} ({string.Join(", ", extensions.Select(e => e.Name))})");
            var result = command == "check"
                ? redb.Route.Xml.Packaging.RoutePackage.Check(projectDir, name, version, resolver, extensions)
                : redb.Route.Xml.Packaging.RoutePackage.Build(projectDir, name, version,
                    outDir ?? Path.Combine(projectDir, "pkg"),
                    args.Where((a, i) => i > 0 && args[i - 1] == "--entry").ToList(), resolver, extensions);
            foreach (var finding in result.Findings)
                (finding.IsError ? Console.Error : Console.Out).WriteLine(finding.ToString());
            if (result.Errors.Any())
                return 1;
            Console.Out.WriteLine($"{name} {version}: {result.Manifest.Artifacts.Count} artifact(s), " +
                $"{result.Manifest.RequiredConfigKeys.Count} required config key(s)" +
                (command == "pack" ? $" → {(outDir ?? Path.Combine(projectDir, "pkg"))}" : " — ok"));
            return 0;
        }
        case "catalog":
        {
            // catalog <binDir>... [--out dir] — reflects over BUILT connector outputs (no
            // project builds from here: point it at bin/ directories that already exist) and
            // writes the component catalog plus the catalog-aware XSD.
            var dirs = args.Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (dirs.Count == 0)
                throw new ArgumentException("catalog needs at least one bin directory with built connector assemblies.");
            // Every listed directory serves as a dependency probe for every other one: a Tsak
            // worker keeps the connectors in Libs/shared but their dependencies (Quartz, SDKs)
            // in the host bin, so `catalog Libs/shared bin/Debug/net10.0` must resolve across.
            var probeDirs = dirs.Select(Path.GetFullPath).ToArray();
            var components = new List<redb.Route.Abstractions.IComponent>();
            foreach (var dir in probeDirs)
                components.AddRange(LoadComponents(dir, probeDirs));
            var catalog = redb.Route.Xml.ComponentCatalog.Build(components);
            // The same bins carry the markup contributions (<redbSave>, <cache>, …) the worker's
            // discovery loads — the catalog XSD must know them too, or every package element
            // shows as invalid in the editor (owner finding on the XmlDemo, 2026-09-09).
            var catalogContributions = probeDirs
                .SelectMany(d => LoadContributions(d))
                .DistinctBy(c => c.Name)
                .ToList();
            Emit(redb.Route.Xml.ComponentCatalog.ToJson(catalog), outDir, "redb-route-catalog.json");
            if (outDir is not null)
                Emit(XmlRouteSchema.Generate(
                        ElementRegistry.CreateDefault(catalogContributions.Count > 0 ? catalogContributions : null),
                        catalog).ToString(),
                    outDir, "redb-route-1.0.catalog.xsd");
            Console.Error.WriteLine($"{catalog.Count} scheme(s): {string.Join(", ", catalog.Select(c => c.Scheme))}");
            return 0;
        }
        default:
            Console.Error.WriteLine($"unknown command '{command}' (new, pack, check, catalog, csharp, mermaid, xsd).");
            return 2;
    }
}
catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or XmlRouteException or InvalidOperationException or NotSupportedException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static Func<string, Type?> BuildBinResolver(string binDir)
{
    var context = new System.Runtime.Loader.AssemblyLoadContext("route-pack-check", isCollectible: false);
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
            Console.Error.WriteLine($"warning: {Path.GetFileName(dll)} is not a loadable assembly — skipped by the deep bean checks.");
        }
    }
    return typeName =>
    {
        var comma = typeName.IndexOf(',');
        var full = (comma >= 0 ? typeName[..comma] : typeName).Trim();
        var assemblyName = comma >= 0 ? typeName[(comma + 1)..].Trim() : null;
        var assemblies = assemblyName is null
            ? context.Assemblies
            : context.Assemblies.Where(a => a.GetName().Name == assemblyName);
        foreach (var assembly in assemblies)
        {
            if (assembly.GetType(full) is { } type)
                return type;
        }
        return Type.GetType(typeName); // core/BCL types (System.String in <convertBody> etc.)
    };
}

/// <summary>
/// The tool casts what a bin carries to ITS OWN redb.Route.Xml / redb.Route types. A bin built
/// against another version of those assemblies yields types the cast silently rejects: zero
/// contributions, zero components, then a baffling «unknown element» from the XSD gate (owner
/// finding: a 3.7.3 tool over 4.0.0 bins, 2026-09-16). Refuse the skew out loud instead.
/// </summary>
static void RequireSameRuntime(string binDir, System.Reflection.Assembly own)
{
    var name = own.GetName();
    var candidate = Path.Combine(binDir, name.Name + ".dll");
    if (!File.Exists(candidate))
        return;
    var binVersion = System.Reflection.AssemblyName.GetAssemblyName(candidate).Version;
    if (binVersion is null || name.Version is null || binVersion == name.Version)
        return;
    Console.Error.WriteLine(
        $"error: {name.Name} {binVersion} in '{binDir}' is not the {name.Name} {name.Version} this redb-route-xml was built with — " +
        "contributions and components of another runtime version cannot be read. Update the tool to the runtime's version " +
        "(dotnet tool update -g redb.Route.Xml.CodeGen --add-source <feed>) or point --bin at a matching build.");
    Environment.Exit(1);
}

static List<redb.Route.Xml.IXmlElementContribution> LoadContributions(string binDir)
{
    RequireSameRuntime(binDir, typeof(redb.Route.Xml.IXmlElementContribution).Assembly);
    // Same ALC pattern as LoadComponents: the tool's own assemblies (redb.Route.Xml above all)
    // win through the Default-context fallback, so a contribution loaded from --bin implements
    // THE tool's IXmlElementContribution and the cast holds.
    var contributions = new List<redb.Route.Xml.IXmlElementContribution>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var context = new System.Runtime.Loader.AssemblyLoadContext($"contrib:{binDir}", isCollectible: false);
    context.Resolving += (loadContext, assemblyName) =>
    {
        var candidate = Path.Combine(binDir, assemblyName.Name + ".dll");
        return File.Exists(candidate) ? loadContext.LoadFromAssemblyPath(candidate) : null;
    };
    foreach (var dll in Directory.GetFiles(binDir, "redb.Route.*.dll"))
    {
        System.Reflection.Assembly assembly;
        try
        {
            // The Resolving hook may have already pulled this simple name from another probe
            // dir while satisfying a dependency; loading the local copy on top would throw
            // «assembly with same name is already loaded» — first copy wins, like the worker.
            var simpleName = System.Reflection.AssemblyName.GetAssemblyName(dll).Name;
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
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.Where(t => t is not null)!];
        }
        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface
                || !typeof(redb.Route.Xml.IXmlElementContribution).IsAssignableFrom(type)
                || type.GetConstructor(Type.EmptyTypes) is null
                || !seen.Add(type.FullName ?? type.Name))
                continue;
            contributions.Add((redb.Route.Xml.IXmlElementContribution)Activator.CreateInstance(type)!);
        }
    }
    return contributions;
}

static IEnumerable<redb.Route.Abstractions.IComponent> LoadComponents(string binDir, string[]? probeDirs = null)
{
    RequireSameRuntime(binDir, typeof(redb.Route.Abstractions.IComponent).Assembly);
    var probes = probeDirs is { Length: > 0 } ? probeDirs : [binDir];
    var context = new System.Runtime.Loader.AssemblyLoadContext($"catalog:{binDir}", isCollectible: false);
    context.Resolving += (loadContext, assemblyName) =>
    {
        foreach (var probe in probes)
        {
            var candidate = Path.Combine(probe, assemblyName.Name + ".dll");
            if (File.Exists(candidate))
                return loadContext.LoadFromAssemblyPath(candidate);
        }
        return null;
    };
    foreach (var dll in Directory.GetFiles(binDir, "redb.Route.*.dll"))
    {
        System.Reflection.Assembly assembly;
        try
        {
            // The Resolving hook may have already pulled this simple name from another probe
            // dir while satisfying a dependency; loading the local copy on top would throw
            // «assembly with same name is already loaded» — first copy wins, like the worker.
            var simpleName = System.Reflection.AssemblyName.GetAssemblyName(dll).Name;
            assembly = context.Assemblies.FirstOrDefault(a => a.GetName().Name == simpleName)
                ?? context.LoadFromAssemblyPath(dll);
        }
        catch (BadImageFormatException)
        {
            continue;
        }
        // Mirror the engine's RouteContextExtensions.AddComponents: ALL types (a connector may
        // keep its components internal behind a registration extension — Firebase does), the
        // loadable subset when some types need assemblies this host does not carry, and a skip
        // for anything that will not instantiate. The catalog must describe what a route in a
        // worker can actually reach, so the tool must not be stricter than the engine.
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            Console.Error.WriteLine($"warning: {Path.GetFileName(dll)}: some types did not load ({ex.LoaderExceptions.FirstOrDefault()?.Message}); using the rest.");
            types = [.. ex.Types.Where(t => t is not null)!];
        }
        var componentTypes = types
            .Where(t => !t.IsAbstract && !t.IsInterface
                        && typeof(redb.Route.Abstractions.IComponent).IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) is not null);
        foreach (var type in componentTypes)
        {
            redb.Route.Abstractions.IComponent component;
            try
            {
                component = (redb.Route.Abstractions.IComponent)Activator.CreateInstance(type)!;
            }
            catch (Exception ex) when (ex is System.Reflection.TargetInvocationException or MissingMethodException)
            {
                Console.Error.WriteLine($"warning: {type.FullName}: constructor failed ({ex.InnerException?.Message ?? ex.Message}); skipped.");
                continue;
            }
            yield return component;
        }
    }
}

static string InferPackageName(string projectDir)
{
    // The csproj's RoutePackageName property, else the single csproj name, else the directory.
    foreach (var csproj in Directory.GetFiles(projectDir, "*.csproj"))
    {
        var doc = XDocument.Load(csproj);
        if (doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "RoutePackageName")?.Value is { Length: > 0 } fromProp)
            return fromProp;
        return Path.GetFileNameWithoutExtension(csproj);
    }
    return Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}

static string DefaultClassName(string file)
{
    var stem = Path.GetFileName(file).Replace(".route.xml", "", StringComparison.Ordinal);
    var parts = stem.Split(['-', '.', '_'], StringSplitOptions.RemoveEmptyEntries);
    return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..])) + "Routes";
}

static void Emit(string content, string? outDir, string fileName)
{
    if (outDir is null)
    {
        Console.Out.Write(content);
        return;
    }
    Directory.CreateDirectory(outDir);
    var path = Path.Combine(outDir, fileName);
    File.WriteAllText(path, content);
    Console.Out.WriteLine(path);
}

static IEnumerable<string> CollectSchemes(string file)
{
    var document = XDocument.Load(file);
    var schemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var attribute in document.Descendants().Attributes("uri"))
    {
        var uri = attribute.Value;
        var colon = uri.IndexOf(':');
        if (colon > 0 && !uri.StartsWith("{{", StringComparison.Ordinal) && !uri.StartsWith("${", StringComparison.Ordinal))
            schemes.Add(uri[..colon]);
    }
    foreach (var address in document.Descendants().Where(e => e.Name.LocalName is "to" or "toD" or "from" or "wireTap" or "enrich" or "pollEnrich"))
        foreach (var child in address.Elements())
            schemes.Add(child.Name.LocalName);
    return schemes;
}

file sealed class SchemeStub(string scheme) : redb.Route.Core.ComponentBase
{
    public override string Scheme { get; } = scheme;
    public override redb.Route.Abstractions.IEndpoint CreateEndpoint(redb.Route.Abstractions.EndpointUri uri)
        => throw new NotSupportedException("diagram-only stub");
}

file sealed class PredicateStub : redb.Route.Abstractions.IPredicate
{
    public bool Matches(redb.Route.Abstractions.IExchange exchange) => false;
}

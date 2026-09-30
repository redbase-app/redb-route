using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using redb.Route.Core;

namespace redb.Route.Xml.Packaging;

/// <summary>
/// The package manifest (Route-XML Ф5.1): the four fields Tsak modules always had, the three
/// the XML artifacts add, and the required-configuration-keys manifest (эскиз 11 §1.4 —
/// every <c>{{key}}</c> without a default; the value legitimately arrives from ANY layer of
/// the merged context configuration at deploy time, so packaging records the requirement
/// instead of demanding a value).
/// </summary>
public sealed record RoutePackageManifest
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public IReadOnlyList<string> EntryPoints { get; init; } = [];
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyList<string> Artifacts { get; init; } = [];
    public string SchemaVersion { get; init; } = "1.0";
    public string Resources { get; init; } = "resources";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Context { get; init; }
    public IReadOnlyList<string> RequiredConfigKeys { get; init; } = [];
}

/// <summary>One finding of the packaging checks: a hard error or a warning.</summary>
public sealed record PackageFinding(bool IsError, string File, string Message)
{
    public override string ToString() => $"{(IsError ? "error" : "warning")}: {File}: {Message}";
}

/// <summary>The result of building or checking a package.</summary>
public sealed record PackageResult(
    RoutePackageManifest Manifest,
    IReadOnlyList<PackageFinding> Findings)
{
    /// <summary>The findings that fail the check.</summary>
    public IEnumerable<PackageFinding> Errors => Findings.Where(f => f.IsError);

    /// <summary>The findings that are advisory only.</summary>
    public IEnumerable<PackageFinding> Warnings => Findings.Where(f => !f.IsError);
}

/// <summary>
/// Builds a route package from a project directory (Route-XML Ф5, the Tsak-independent half):
/// the Ф5.1 layout — <c>manifest.json</c>, the L4 identity config, the XML artifacts, the
/// resources — with the Ф5.4 packaging checks run first, so a broken reference is a build
/// error, never a deployment surprise. The layout is exactly what the Tsak module loader will
/// consume; nothing here depends on Tsak.
/// </summary>
public static class RoutePackage
{
    /// <summary>The conventional project layout the builder and the scaffolder share.</summary>
    public const string RoutesDir = "routes";
    /// <summary>Resources directory name (XSLT, schemas — resolved from the package root).</summary>
    public const string ResourcesDir = "resources";
    /// <summary>Config directory name (the L4 identity file and the L3 deploy sample).</summary>
    public const string ConfigDir = "config";
    /// <summary>The context-level document, loaded before the route artifacts.</summary>
    public const string ContextFile = "context.xml";

    /// <summary>
    /// The directory a route's <c>file=</c> references resolve against outside a package - the
    /// tool's <c>csharp</c> and <c>mermaid</c>. A route under <c>{project}/routes/</c> reads
    /// <c>{project}/resources/</c>, where the gate checks and the Tsak loader looks; any other
    /// file reads its own directory.
    /// </summary>
    public static string ResourceRootFor(string routeFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeFile);
        var directory = Path.GetDirectoryName(Path.GetFullPath(routeFile))!;
        if (!string.Equals(Path.GetFileName(directory), RoutesDir, StringComparison.OrdinalIgnoreCase))
            return directory;
        var project = Path.GetDirectoryName(directory)!;
        var resources = Path.Combine(project, ResourcesDir);
        return Directory.Exists(resources) ? resources : project;
    }

    /// <summary>
    /// Runs the packaging checks for a project directory without producing anything — the CI
    /// gate form (Ф5.4: «без гейта проверки будут пропускаться локально на бегу»).
    /// <paramref name="typeResolver"/> — when the built assemblies are at hand (the tool's
    /// <c>--bin</c>) — turns the bean checks DEEP: a renamed type, a non-public type, a typo in
    /// a property or a <c>method=</c> becomes a build error instead of a first-message surprise.
    /// <paramref name="catalog"/> — the component catalog of the same built output — holds every
    /// endpoint's parameters against its connector: a name a strict connector does not know
    /// (<see cref="CatalogComponent.Lenient"/> false) is an error here, in the engine's own words,
    /// instead of a refusal when the route starts.
    /// </summary>
    public static PackageResult Check(string projectDir, string name, string version,
        Func<string, Type?>? typeResolver = null,
        IReadOnlyList<IXmlElementContribution>? extensions = null,
        IReadOnlyList<CatalogComponent>? catalog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDir);
        var findings = new List<PackageFinding>();
        var artifacts = CollectArtifacts(projectDir, findings);
        var requiredKeys = RunChecks(projectDir, artifacts, findings, typeResolver, extensions, catalog);
        var manifest = BuildManifest(projectDir, name, version, artifacts, requiredKeys);
        return new PackageResult(manifest, findings);
    }

    /// <summary>
    /// Builds the package: checks first (errors refuse the build), then the Ф5.1 layout in
    /// <paramref name="outDir"/>, then the <c>.tpkg</c> zip beside it. Returns the result with
    /// the manifest and every finding.
    /// </summary>
    public static PackageResult Build(string projectDir, string name, string version, string outDir,
        IReadOnlyList<string>? entryPoints = null, Func<string, Type?>? typeResolver = null,
        IReadOnlyList<IXmlElementContribution>? extensions = null,
        IReadOnlyList<CatalogComponent>? catalog = null)
    {
        var result = Check(projectDir, name, version, typeResolver, extensions, catalog);
        if (result.Errors.Any())
            return result;

        var manifest = result.Manifest with { EntryPoints = entryPoints ?? [] };
        var stage = Path.Combine(outDir, $"{name}-{version}");
        if (Directory.Exists(stage))
            Directory.Delete(stage, recursive: true);
        Directory.CreateDirectory(stage);

        File.WriteAllText(Path.Combine(stage, "manifest.json"),
            JsonSerializer.Serialize(manifest, ManifestJson));

        // L4 — the identity stub only (эскиз 11 §1.4: secrets never ride inside the package).
        var l4Source = Path.Combine(projectDir, ConfigDir, $"{name}.config.json");
        File.WriteAllText(Path.Combine(stage, $"{name}.config.json"),
            File.Exists(l4Source)
                ? File.ReadAllText(l4Source)
                : JsonSerializer.Serialize(new { ContextName = manifest.Context ?? name, AutoStart = true }, ManifestJson));

        foreach (var artifact in manifest.Artifacts)
        {
            var target = Path.Combine(stage, artifact);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(projectDir, artifact), target);
        }
        if (File.Exists(Path.Combine(projectDir, ContextFile)))
            File.Copy(Path.Combine(projectDir, ContextFile), Path.Combine(stage, ContextFile));

        var resources = Path.Combine(projectDir, ResourcesDir);
        if (Directory.Exists(resources))
            CopyTree(resources, Path.Combine(stage, ResourcesDir));

        foreach (var entryPoint in manifest.EntryPoints)
        {
            var source = Path.GetFullPath(entryPoint, projectDir);
            File.Copy(source, Path.Combine(stage, Path.GetFileName(source)), overwrite: true);
        }

        var zipPath = Path.Combine(outDir, $"{name}-{version}.tpkg");
        if (File.Exists(zipPath))
            File.Delete(zipPath);
        ZipFile.CreateFromDirectory(stage, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        return new PackageResult(manifest, result.Findings);
    }

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    private static List<string> CollectArtifacts(string projectDir, List<PackageFinding> findings)
    {
        var routesDir = Path.Combine(projectDir, RoutesDir);
        if (!Directory.Exists(routesDir))
        {
            findings.Add(new PackageFinding(true, RoutesDir, "the routes directory is missing — a route package carries at least one .route.xml."));
            return [];
        }
        // Deterministic ordinal order — the manifest is the explicit, checkable list (Ф5.1).
        return [.. Directory.GetFiles(routesDir, "*.route.xml")
            .Select(f => Path.GetRelativePath(projectDir, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)];
    }

    private static RoutePackageManifest BuildManifest(string projectDir, string name, string version,
        List<string> artifacts, IReadOnlyList<string> requiredKeys)
    {
        string? contextName = null;
        var l4 = Path.Combine(projectDir, ConfigDir, $"{name}.config.json");
        if (File.Exists(l4))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(l4));
                if (document.RootElement.TryGetProperty("ContextName", out var ctx))
                    contextName = ctx.GetString();
            }
            catch (JsonException)
            {
                // reported by RunChecks
            }
        }
        return new RoutePackageManifest
        {
            Name = name,
            Version = version,
            Artifacts = artifacts,
            Context = contextName,
            RequiredConfigKeys = requiredKeys,
        };
    }

    // ── Ф5.4 — the packaging checks ─────────────────────────────────────────

    private static readonly Regex Placeholder = new(@"\{\{([^{}:]+)(:[^{}]*)?\}\}", RegexOptions.Compiled);
    private static readonly Regex PasswordLikeOption = new(@"(?i)(password|passwd|pwd|secret)\s*=\s*(?!\{\{|\$\{)[^&\s""']+", RegexOptions.Compiled);

    private static readonly Regex BeanMethodOption = new(@"(?i)[?&]method=([A-Za-z0-9_]+)", RegexOptions.Compiled);

    private static readonly Regex LeadingReference = new(@"^#([A-Za-z0-9_.\-]+)", RegexOptions.Compiled);
    private static readonly Regex UriScheme = new(@"^[A-Za-z][A-Za-z0-9+.\-]*:(//)?", RegexOptions.Compiled);

    /// <summary>
    /// Registry references by POSITION (Ф0 §2.1: a reference is a value that STARTS with '#'):
    /// the whole attribute value, the path right after the scheme (<c>bean:#x</c>) and every URI
    /// option value (<c>dataSource=#main-db</c>). A '#' anywhere else is text — the sql parameter
    /// <c>:#name</c> must not read as a dangling bean.
    /// </summary>
    private static IEnumerable<string> RegistryReferences(string value)
    {
        if (LeadingReference.Match(value) is { Success: true } whole)
        {
            yield return whole.Groups[1].Value;
            yield break;
        }
        var query = value.IndexOf('?');
        var address = query >= 0 ? value[..query] : value;
        if (UriScheme.Match(address) is { Success: true } scheme
            && LeadingReference.Match(address[scheme.Length..]) is { Success: true } path)
            yield return path.Groups[1].Value;
        if (query < 0)
            yield break;
        foreach (var pair in value[(query + 1)..].Split('&'))
        {
            var equals = pair.IndexOf('=');
            if (equals >= 0 && LeadingReference.Match(pair[(equals + 1)..]) is { Success: true } option)
                yield return option.Groups[1].Value;
        }
    }

    private static IReadOnlyList<string> RunChecks(string projectDir, List<string> artifacts, List<PackageFinding> findings,
        Func<string, Type?>? typeResolver,
        IReadOnlyList<IXmlElementContribution>? extensions = null,
        IReadOnlyList<CatalogComponent>? catalog = null)
    {
        var registry = ElementRegistry.CreateDefault(extensions);
        var requiredKeys = new SortedSet<string>(StringComparer.Ordinal);
        var declaredBeans = new HashSet<string>(StringComparer.Ordinal);
        var beanDeclarations = new List<(string File, BeanDeclaration Bean)>();
        var beanMethodCalls = new List<(string File, string Bean, string Method)>();
        var referencedBeans = new List<(string File, string Name)>();
        var documents = new List<(string File, XDocument Document)>();

        var contextPath = Path.Combine(projectDir, ContextFile);
        if (File.Exists(contextPath))
            LoadDocument(ContextFile, contextPath, documents, findings);
        foreach (var artifact in artifacts)
            LoadDocument(artifact, Path.Combine(projectDir, artifact), documents, findings);

        foreach (var (file, document) in documents)
        {
            // XSD validation catches the broken markup wholesale (root name decides the flavor).
            if (document.Root?.Name.LocalName == "routes")
            {
                foreach (var (line, column, message) in XmlRouteSchema.Validate(document, registry))
                    findings.Add(new PackageFinding(true, $"{file}({line},{column})", message));
            }

            foreach (var element in document.Descendants())
            {
                foreach (var text in element.Nodes().OfType<XText>())
                {
                    if (text.Value.Contains("]]>", StringComparison.Ordinal))
                        findings.Add(new PackageFinding(true, file, $"<{element.Name.LocalName}> content contains ']]>' — silently broken XML on repackaging."));
                    ScanPlaceholders(text.Value, requiredKeys);
                }
                if (element.Name.LocalName == "bean" && element.Attribute("name")?.Value is { Length: > 0 } beanName)
                    declaredBeans.Add(beanName);

                foreach (var attribute in element.Attributes())
                {
                    ScanPlaceholders(attribute.Value, requiredKeys);
                    foreach (var reference in RegistryReferences(attribute.Value))
                        referencedBeans.Add((file, reference));
                    if (attribute.Value.StartsWith("bean:#", StringComparison.Ordinal)
                        && BeanMethodOption.Match(attribute.Value) is { Success: true } methodMatch)
                    {
                        var beanRef = attribute.Value["bean:#".Length..];
                        var end = beanRef.IndexOf('?');
                        beanMethodCalls.Add((file, end >= 0 ? beanRef[..end] : beanRef, methodMatch.Groups[1].Value));
                    }
                    if (PasswordLikeOption.IsMatch(attribute.Value))
                        findings.Add(new PackageFinding(false, file,
                            $"<{element.Name.LocalName} {attribute.Name.LocalName}=…> looks like a literal secret — " +
                            "supply secrets through the merged context configuration (L3/L5), never inside the package."));
                    if (IsResourceLocator(element, attribute, registry))
                        CheckResource(projectDir, file, element, attribute, findings);
                }
            }
        }

        CheckBeanDeclarations(documents, declaredBeans, beanDeclarations, findings);

        // Elements that register objects under a name of their own (<redb><idempotentRepository
        // name>) declare that name like a <bean> does; references that must name an object of a
        // given type (<idempotentConsumer repository>) are held against it once types are known.
        var registered = new Dictionary<string, string>(StringComparer.Ordinal);
        var typedReferences = new List<TypedReference>();
        foreach (var (file, document) in documents)
            foreach (var element in document.Root?.Elements() ?? [])
                CollectRegistryAttributes(file, element, registry.Find(element.Name.LocalName)?.Spec, registry,
                    registered, typedReferences);
        declaredBeans.UnionWith(registered.Keys);

        // The registry is one per context (Ф0 §2.1), so a #name may be declared in ANY artifact
        // of the package; what remains unresolved is either external (module code) — a warning —
        // or a typo. Without the module's code we cannot tell, so the finding names both.
        foreach (var (file, name) in referencedBeans.DistinctBy(r => r.Name))
        {
            if (!declaredBeans.Contains(name))
                findings.Add(new PackageFinding(false, file,
                    $"'#{name}' is not declared by any <bean> of this package — either module code registers it at startup (fine), or it is a dangling reference."));
        }

        foreach (var (file, document) in documents)
        {
            if (document.Root is not { } root)
                continue;
            CheckUnreachableSteps(file, root, registry, findings);
            CheckConditionTemplates(file, root, registry.Find(root.Name.LocalName)?.Spec, registry, findings);
        }

        if (typeResolver is not null)
        {
            var instanceTypes = RunDeepBeanChecks(beanDeclarations, beanMethodCalls, typeResolver, findings);
            CheckTypedReferences(typedReferences, instanceTypes, registered, typeResolver, findings);
            foreach (var (file, document) in documents)
                foreach (var element in document.Root?.Elements() ?? [])
                    CheckTypeAttributes(file, element, registry.Find(element.Name.LocalName)?.Spec, registry, typeResolver, findings);
            if (catalog is not null)
            {
                var parameters = new EndpointParameterChecker(catalog, typeResolver, findings);
                foreach (var (file, document) in documents)
                    foreach (var element in document.Root?.Elements() ?? [])
                        CheckEndpoints(file, element, registry.Find(element.Name.LocalName)?.Spec, registry, parameters);
            }
        }

        return [.. requiredKeys];
    }

    /// <summary>
    /// The assembly-backed half of Ф5.4: bean types resolve and are public, declared properties
    /// are writable, and every <c>bean:#x?method=M</c> names an existing public method.
    /// </summary>
    /// <summary>
    /// A step written after a terminal one (<see cref="ElementSpec.Terminal"/>: stop, rollbackAll,
    /// throwException) in the same list never runs. Not an error - the route loads and works - but
    /// dead markup that reads as if it did something; the first such step is named with its
    /// position. Generic over the specs, so a contribution that declares a terminal step is covered.
    /// </summary>
    private static void CheckUnreachableSteps(string file, XElement parent, ElementRegistry registry,
        List<PackageFinding> findings)
    {
        XElement? terminal = null;
        foreach (var child in parent.Elements())
        {
            var spec = registry.Find(child.Name.LocalName)?.Spec;
            var isStep = spec?.Kind is XmlElementKind.Step or XmlElementKind.Scope or XmlElementKind.Branching;
            if (terminal is not null && isStep)
            {
                var line = (IXmlLineInfo)child;
                findings.Add(new PackageFinding(false, $"{file}({line.LineNumber},{line.LinePosition})",
                    $"<{child.Name.LocalName}> never runs: it comes after <{terminal.Name.LocalName}/>, " +
                    "which ends the route for the exchange."));
                terminal = null;
            }
            if (isStep && spec!.Terminal)
                terminal = child;
            CheckUnreachableSteps(file, child, registry, findings);
        }
    }

    /// <summary>
    /// A file the runtime will read from the package: the format's own <c>file=</c> (every
    /// element taking a file-or-content pair), or an attribute its spec marks
    /// <see cref="AttributeSpec.Resource"/> — a package element that names its file otherwise.
    /// </summary>
    private static bool IsResourceLocator(XElement element, XAttribute attribute, ElementRegistry registry)
        => attribute.Name.LocalName == "file"
           || registry.Find(element.Name.LocalName)?.Spec.Attributes.Any(a =>
                  a.Resource && string.Equals(a.Name, attribute.Name.LocalName, StringComparison.Ordinal)) == true;

    /// <summary>
    /// The file is under <c>resources/</c> (or at the project root), where the worker's resource
    /// resolver will look. Two kinds of value cannot be checked here and are left alone: an
    /// <c>assembly:</c> locator (the file ships inside an assembly) and a value that still holds
    /// a placeholder (it is only known once configuration resolves it).
    /// </summary>
    private static void CheckResource(string projectDir, string file, XElement element, XAttribute attribute,
        List<PackageFinding> findings)
    {
        var value = attribute.Value;
        if (value.StartsWith("assembly:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("{{", StringComparison.Ordinal)
            || value.Contains("${", StringComparison.Ordinal))
            return;
        var resource = Path.Combine(projectDir, ResourcesDir, value);
        var direct = Path.Combine(projectDir, value);
        if (!File.Exists(resource) && !File.Exists(direct))
        {
            var line = (IXmlLineInfo)attribute;
            findings.Add(new PackageFinding(true, $"{file}({line.LineNumber},{line.LinePosition})",
                $"resource '{value}' referenced by <{element.Name.LocalName} {attribute.Name.LocalName}=…> is not under '{ResourcesDir}/'."));
        }
    }

    /// <summary>
    /// A condition written as a template is true whatever it renders: <c>${header.kind} == 'order'</c>
    /// renders to the text «refund == 'order'», which is non-empty, so the branch always wins. The
    /// comparison has to be written without the braces — <c>header.kind == 'order'</c> — or entirely
    /// inside them. A WARNING, not an error: the gate names the line, the author decides. Driven by
    /// <see cref="AttributeSpec.Condition"/>, so a package contribution's own condition is covered.
    /// </summary>
    private static void CheckConditionTemplates(string file, XElement element, ElementSpec? spec,
        ElementRegistry registry, List<PackageFinding> findings)
    {
        foreach (var attribute in element.Attributes())
        {
            var attributeSpec = spec?.Attributes.FirstOrDefault(a =>
                string.Equals(a.Name, attribute.Name.LocalName, StringComparison.Ordinal));
            if (attributeSpec is not { Condition: true } || !LooksLikeTemplateComparison(attribute.Value))
                continue;
            var line = (IXmlLineInfo)attribute;
            findings.Add(new PackageFinding(false, $"{file}({line.LineNumber},{line.LinePosition})",
                $"{attribute.Name.LocalName}=\"{attribute.Value}\" compares OUTSIDE ${{…}}: a condition holding a " +
                "placeholder is rendered to text first, and non-empty text is true whatever it says. Write the " +
                "comparison without the braces (header.kind == 'order') or entirely inside them."));
        }
        foreach (var child in element.Elements())
        {
            var childSpec = spec?.Children.FirstOrDefault(c =>
                                string.Equals(c.Name, child.Name.LocalName, StringComparison.Ordinal))
                            ?? registry.Find(child.Name.LocalName)?.Spec;
            CheckConditionTemplates(file, child, childSpec, registry, findings);
        }
    }

    /// <summary>
    /// The value carries a <c>${…}</c> placeholder AND an operator outside of it. Both halves matter:
    /// a lone <c>${header.enabled}</c> renders to «true»/«false» and reads correctly, and a comparison
    /// written entirely inside the braces is evaluated as an expression, not rendered.
    /// </summary>
    private static bool LooksLikeTemplateComparison(string value)
    {
        if (!value.Contains("${", StringComparison.Ordinal))
            return false;
        var outside = System.Text.RegularExpressions.Regex.Replace(value, @"\$\{[^}]*\}", " ");
        return Operators.Any(op => outside.Contains(op, StringComparison.Ordinal));
    }

    /// <summary>What turns a rendered condition into a comparison the author expected to be evaluated.</summary>
    private static readonly string[] Operators =
        ["==", "!=", ">=", "<=", ">", "<", "&&", "||", " and ", " or ", " eq ", " ne "];

    /// <summary>
    /// Every attribute a spec declares as a type resolves against the built assemblies: the loader
    /// would fail on it at the worker, so the build fails on it here. Generic over the specs - no
    /// list of elements - so a package contribution's type attributes are checked the same way.
    /// Values carrying placeholders or expressions are left to the load.
    /// </summary>
    private static void CheckTypeAttributes(string file, XElement element, ElementSpec? spec, ElementRegistry registry,
        Func<string, Type?> typeResolver, List<PackageFinding> findings)
    {
        if (spec is not null)
        {
            foreach (var attributeSpec in spec.Attributes.Where(a => a.Type is AttributeType.TypeName or AttributeType.TypeNameList))
            {
                var value = element.Attribute(attributeSpec.Name)?.Value;
                if (string.IsNullOrWhiteSpace(value) || value.Contains("{{", StringComparison.Ordinal)
                    || value.Contains("${", StringComparison.Ordinal))
                    continue;
                var names = attributeSpec.Type == AttributeType.TypeNameList
                    ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : [value.Trim()];
                foreach (var name in names)
                {
                    if (typeResolver(name) is not null)
                        continue;
                    var line = (IXmlLineInfo)element;
                    findings.Add(new PackageFinding(true, $"{file}({line.LineNumber},{line.LinePosition})",
                        $"<{element.Name.LocalName} {attributeSpec.Name}=...>: type '{name}' was not found in the built assemblies."));
                }
            }
        }
        foreach (var child in element.Elements())
        {
            var childSpec = spec?.Children.FirstOrDefault(c => c.Name == child.Name.LocalName)
                            ?? registry.Find(child.Name.LocalName)?.Spec;
            CheckTypeAttributes(file, child, childSpec, registry, typeResolver, findings);
        }
    }

    /// <summary>
    /// Every top-level <c>&lt;bean&gt;</c> read by the loader's own grammar, in LOAD order —
    /// context.xml, then the artifacts as the manifest lists them, each top to bottom — and each
    /// reference held against the beans registered before it. A bean declared later is a hard
    /// error (the load would fail); a bean the package does not declare at all may come from
    /// module code, so that stays a warning.
    /// </summary>
    private static void CheckBeanDeclarations(
        List<(string File, XDocument Document)> documents,
        HashSet<string> declaredBeans,
        List<(string File, BeanDeclaration Bean)> beanDeclarations,
        List<PackageFinding> findings)
    {
        var registeredSoFar = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, document) in documents)
        {
            foreach (var element in document.Root?.Elements().Where(e => e.Name.LocalName == "bean") ?? [])
            {
                var declaration = BeanModel.Parse(element,
                    (at, message) => findings.Add(new PackageFinding(true, Position(file, at), message)));
                if (declaration is not null)
                {
                    beanDeclarations.Add((file, declaration));
                    foreach (var reference in BeanModel.References(declaration))
                    {
                        if (registeredSoFar.Contains(reference.Bean))
                            continue;
                        findings.Add(declaredBeans.Contains(reference.Bean)
                            ? new PackageFinding(true, Position(file, reference.At),
                                $"bean reference '{reference.Bean}' points forward — beans are built in load order " +
                                "(context.xml, then the route files as the manifest lists them, each top to bottom); declare it above.")
                            : new PackageFinding(false, Position(file, reference.At),
                                $"bean reference '{reference.Bean}' is not declared by any <bean> of this package — either module code " +
                                "registers it before the XML loads (fine), or it is a dangling reference."));
                    }
                }
                if (element.Attribute("name")?.Value is { Length: > 0 } registered)
                    registeredSoFar.Add(registered);
            }
        }
    }

    /// <summary>A reference attribute whose object must be of a given type (<see cref="AttributeSpec.References"/>).</summary>
    private sealed record TypedReference(string File, XElement Element, string Attribute, string Name, string TypeName);

    /// <summary>
    /// Walks an element with its spec, as the parser does, and collects the attributes that
    /// register a name (<see cref="AttributeSpec.Registers"/>) and the references that must name
    /// an object of a given type (<see cref="AttributeSpec.References"/>).
    /// </summary>
    private static void CollectRegistryAttributes(string file, XElement element, ElementSpec? spec, ElementRegistry registry,
        Dictionary<string, string> registered, List<TypedReference> typedReferences)
    {
        foreach (var attributeSpec in spec?.Attributes ?? [])
        {
            if (element.Attribute(attributeSpec.Name)?.Value is not { Length: > 0 } value)
                continue;
            if (attributeSpec.Registers is { } registeredType)
                registered[value] = registeredType;
            if (attributeSpec.References is { } referencedType)
                typedReferences.Add(new TypedReference(file, element, attributeSpec.Name,
                    value.StartsWith('#') ? value[1..] : value, referencedType));
        }
        foreach (var child in element.Elements())
        {
            var childSpec = spec?.Children.FirstOrDefault(c => c.Name == child.Name.LocalName)
                            ?? registry.Find(child.Name.LocalName)?.Spec;
            CollectRegistryAttributes(file, child, childSpec, registry, registered, typedReferences);
        }
    }

    /// <summary>
    /// With the built assemblies at hand: every typed reference names an object the engine will
    /// accept — a <c>&lt;bean&gt;</c> of that type, or an element that registers one. The wording
    /// follows the engine's refusal (<c>'x' is registered as T, which is not an I…</c>). A name
    /// nothing in the package declares is the undeclared-reference warning, not repeated here.
    /// </summary>
    private static void CheckTypedReferences(
        List<TypedReference> typedReferences,
        IReadOnlyDictionary<string, Type> instanceTypes,
        IReadOnlyDictionary<string, string> registered,
        Func<string, Type?> typeResolver,
        List<PackageFinding> findings)
    {
        foreach (var reference in typedReferences)
        {
            var actual = instanceTypes.TryGetValue(reference.Name, out var beanType) ? beanType
                : registered.TryGetValue(reference.Name, out var registeredType) ? typeResolver(registeredType)
                : null;
            if (actual is null || typeResolver(reference.TypeName) is not { } expected || expected.IsAssignableFrom(actual))
                continue;
            findings.Add(new PackageFinding(true, Position(reference.File, reference.Element),
                $"<{reference.Element.Name.LocalName} {reference.Attribute}=\"#{reference.Name}\">: '{reference.Name}' is registered as " +
                $"{actual.FullName}, which is not an {expected.Name}."));
        }
    }

    private static IReadOnlyDictionary<string, Type> RunDeepBeanChecks(
        List<(string File, BeanDeclaration Bean)> beanDeclarations,
        List<(string File, string Bean, string Method)> beanMethodCalls,
        Func<string, Type?> typeResolver,
        List<PackageFinding> findings)
    {
        // What each registered bean IS: the declared type, or what its factory method returns.
        var instanceTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var (file, bean) in beanDeclarations)
        {
            var checker = new BeanTypeChecker(file, instanceTypes, typeResolver, findings);
            if (checker.Check(bean) is { } instanceType && bean.Name is { Length: > 0 } name)
                instanceTypes[name] = instanceType;
        }
        foreach (var (file, bean, method) in beanMethodCalls.DistinctBy(c => (c.Bean, c.Method)))
        {
            if (!instanceTypes.TryGetValue(bean, out var type))
                continue; // undeclared bean — already a finding, or module code owns it
            if (!type.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                    .Any(m => m.Name == method))
                findings.Add(new PackageFinding(true, file,
                    $"bean '{bean}': type '{type.FullName}' has no public method '{method}' (bean:#{bean}?method={method})."));
        }
        return instanceTypes;
    }

    /// <summary>
    /// The <c>--bin</c> half of the bean checks: every value of a declaration, nested beans and
    /// list items included, held against the type it will be bound to — the same rules
    /// <see cref="BeanFactory"/> applies at load, reported in its words. Text values are left to
    /// the load: they convert only after their placeholders resolve.
    /// </summary>
    private sealed class BeanTypeChecker(
        string file,
        IReadOnlyDictionary<string, Type> instanceTypes,
        Func<string, Type?> typeResolver,
        List<PackageFinding> findings)
    {
        /// <summary>The type the bean's instance will have, or null after a finding.</summary>
        public Type? Check(BeanDeclaration bean)
        {
            var label = bean.Name is { Length: > 0 } name ? $"bean '{name}'" : "nested bean";
            var type = typeResolver(bean.TypeName);
            if (type is null)
                return Error(bean.At, $"{label}: type '{bean.TypeName}' was not found in the built assemblies.");
            if (!type.IsPublic && !type.IsNestedPublic)
                return Error(bean.At, $"{label}: type '{bean.TypeName}' exists but is not public — bean: needs a public type.");

            var instanceType = type;
            List<Type>? argumentTypes = null;
            if (bean.TypedArguments)
            {
                argumentTypes = [];
                foreach (var argument in bean.ConstructorArgs)
                {
                    if (typeResolver(argument.TypeName!) is not { } argumentType)
                        return Error(argument.Value.At,
                            $"{label}: <constructorArg type=\"{argument.TypeName}\">: the type was not found in the built assemblies.");
                    argumentTypes.Add(argumentType);
                }
            }
            if (bean.FactoryMethod is { } factory)
            {
                // The loader's own choice (BeanFactory.FactoryMethod): the exact signature, or the
                // single overload of that arity — several of them are refused, not guessed.
                MethodInfo method;
                try
                {
                    method = BeanFactory.FactoryMethod(type, factory, bean.ConstructorArgs.Count, argumentTypes);
                }
                catch (InvalidOperationException ex)
                {
                    return Error(bean.At, $"{label}: {ex.Message}");
                }
                // The parameters after the given arguments are optional and take their defaults.
                var parameters = method.GetParameters();
                for (var i = 0; i < bean.ConstructorArgs.Count; i++)
                    CheckValue(bean.ConstructorArgs[i].Value, parameters[i].ParameterType, $"{factory} argument {i + 1}");
                instanceType = method.ReturnType;
            }
            else if (argumentTypes is not null)
            {
                ConstructorInfo constructor;
                try
                {
                    constructor = BeanFactory.Constructor(type, bean.ConstructorArgs.Count, argumentTypes);
                }
                catch (InvalidOperationException ex)
                {
                    return Error(bean.At, $"{label}: {ex.Message}");
                }
                var parameters = constructor.GetParameters();
                for (var i = 0; i < bean.ConstructorArgs.Count; i++)
                    CheckValue(bean.ConstructorArgs[i].Value, parameters[i].ParameterType, $"constructorArg {i + 1}");
            }
            else
            {
                // The constructor is chosen at load from the built arguments, so no parameter types them.
                for (var i = 0; i < bean.ConstructorArgs.Count; i++)
                    CheckValue(bean.ConstructorArgs[i].Value, null, $"constructorArg {i + 1}");
            }

            foreach (var property in bean.Properties)
            {
                if (instanceType.GetProperty(property.Key) is not { CanWrite: true } info)
                {
                    Error(property.At, $"{label}: type '{instanceType.FullName}' has no writable public property '{property.Key}'.");
                    continue;
                }
                CheckValue(property.Value, info.PropertyType, property.Key);
            }
            return instanceType;
        }

        private void CheckValue(BeanValue value, Type? target, string slot)
        {
            switch (value)
            {
                case RefBeanValue reference:
                    if (target is not null && instanceTypes.TryGetValue(reference.Bean, out var referenced)
                        && !target.IsAssignableFrom(referenced))
                        Error(reference.At, BeanFactory.NotAssignable(referenced, target, slot));
                    break;
                case NestedBeanValue nested:
                    if (Check(nested.Bean) is { } nestedType && target is not null && !target.IsAssignableFrom(nestedType))
                        Error(nested.At, BeanFactory.NotAssignable(nestedType, target, slot));
                    break;
                case ListBeanValue list:
                    CheckList(list, target, slot);
                    break;
            }
        }

        private void CheckList(ListBeanValue list, Type? target, string slot)
        {
            Type? element = null;
            if (target is not null)
            {
                element = BeanFactory.ElementTypeOf(target);
                if (element is null)
                {
                    Error(list.At, BeanFactory.NotAListShape(target, slot));
                    return;
                }
            }
            if (list.ElementTypeName is not null)
            {
                var named = typeResolver(list.ElementTypeName);
                if (named is null)
                {
                    Error(list.At, $"<list of=\"{list.ElementTypeName}\">: the type was not found in the built assemblies.");
                    return;
                }
                if (element is not null && named != element)
                {
                    Error(list.At, BeanFactory.ElementTypeDiffers(named, element, slot));
                    return;
                }
                element = named;
            }
            else if (element is null)
            {
                Error(list.At, BeanFactory.ConstructorListNeedsOf(slot));
                return;
            }
            for (var i = 0; i < list.Items.Count; i++)
                CheckValue(list.Items[i], element, $"{slot}[{i}]");
        }

        private Type? Error(XElement at, string message)
        {
            findings.Add(new PackageFinding(true, Position(file, at), message));
            return null;
        }
    }

    private static string Position(string file, XElement at)
        => at is IXmlLineInfo line && line.HasLineInfo() ? $"{file}({line.LineNumber},{line.LinePosition})" : file;

    /// <summary>
    /// Walks a document for endpoints — a <c>uri</c> attribute of an address-carrying element,
    /// or its structured endpoint child — and hands each one's parameters to the checker.
    /// <c>interceptFrom</c> / <c>interceptSendToEndpoint</c> carry a PATTERN to match, never an
    /// endpoint to create, so they bind nothing and are not held against a connector.
    /// </summary>
    private static void CheckEndpoints(string file, XElement element, ElementSpec? spec, ElementRegistry registry,
        EndpointParameterChecker checker)
    {
        if (element.Name.LocalName is "interceptFrom" or "interceptSendToEndpoint")
            return;
        var takesEndpoint = element.Name.LocalName == "from" || spec?.TakesEndpoint == true;
        if (takesEndpoint)
        {
            // <from> creates the consumer; every other address-carrying step creates a producer
            // (pollEnrich included — the engine polls through a producer).
            var side = element.Name.LocalName == "from" ? EndpointRole.Consumer : EndpointRole.Producer;
            if (element.Attribute("uri")?.Value is { } uri)
            {
                checker.CheckUri(file, element, uri, side);
            }
            else if (element.Elements().ToList() is [var endpoint])
            {
                checker.CheckStructured(file, endpoint, side);
            }
            return;
        }
        foreach (var child in element.Elements())
        {
            var childSpec = spec?.Children.FirstOrDefault(c => c.Name == child.Name.LocalName)
                            ?? registry.Find(child.Name.LocalName)?.Spec;
            // Children with an address of their own (a recipient list's <recipient uri=…>) are
            // specs with a uri attribute of their own.
            if (childSpec?.Attributes.Any(a => a.Name == "uri" && a.Type == AttributeType.Uri) == true
                && !childSpec.TakesEndpoint && child.Attribute("uri")?.Value is { } childUri)
            {
                checker.CheckUri(file, child, childUri, EndpointRole.Producer);
                continue;
            }
            CheckEndpoints(file, child, childSpec, registry, checker);
        }
    }

    /// <summary>
    /// Holds an endpoint's parameter names against its connector the way
    /// <c>EndpointOptions.BindFromUri</c> does when the endpoint is created: a name with no
    /// writable option is refused unless the connector is lenient. The wording is the engine's
    /// own — the checker binds the unknown names into a fresh options object and reports what
    /// the engine said, nearest option and connector hint included. Names built at run time
    /// (<c>${…}</c>, <c>{{…}}</c>) are not knowable here and are left to the engine.
    /// </summary>
    private sealed class EndpointParameterChecker(
        IReadOnlyList<CatalogComponent> catalog,
        Func<string, Type?> typeResolver,
        List<PackageFinding> findings)
    {
        public void CheckUri(string file, XElement at, string uri, EndpointRole side)
        {
            // An address whose scheme itself comes from a placeholder or an expression names no
            // connector until run time — there is nothing to hold its parameters against here.
            if (uri.StartsWith("{{", StringComparison.Ordinal) || uri.StartsWith("${", StringComparison.Ordinal))
                return;
            var scheme = SchemeOf(uri);
            if (scheme is null || Find(scheme) is null)
                return;
            Check(file, at, at, scheme, EndpointUriParser.Parse(uri).RawParameters.Select(p => (p.Key, p.Value)), side);
        }

        public void CheckStructured(string file, XElement endpoint, EndpointRole side)
        {
            var component = Find(endpoint.Name.LocalName);
            if (component is null)
                return;
            var options = StructuredEndpoint.Options(endpoint, component.PathSynonym,
                (at, message) => findings.Add(new PackageFinding(true, Position(file, at), message)));
            if (options is null)
                return;
            Check(file, endpoint, endpoint.Parent!, component.Scheme, options.Select(o => (o.Key, o.Value)), side);
        }

        /// <summary>The scheme the way <see cref="EndpointUriParser"/> reads it, or null when there is none.</summary>
        private static string? SchemeOf(string uri)
        {
            var colon = uri.IndexOf(':');
            return colon > 0 ? uri[..colon].ToLowerInvariant() : null;
        }

        private void Check(string file, XElement at, XElement step, string scheme, IEnumerable<(string Key, string Value)> parameters, EndpointRole side)
        {
            if (Find(scheme) is not { } component)
                return;
            var pairs = parameters.ToList();
            var written = pairs.Select(p => p.Key)
                .Where(k => !k.Contains("${", StringComparison.Ordinal) && !k.Contains("{{", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // An option only the other side reads — the declaration the connector refuses it by
            // (EndpointRole), lenient connector or not.
            foreach (var key in written)
            {
                if (component.Options.FirstOrDefault(o => string.Equals(o.Name, key, StringComparison.OrdinalIgnoreCase))
                        is { Role: { } role } && role != side)
                {
                    findings.Add(new PackageFinding(true, Position(file, at),
                        $"<{scheme}>: '{key}' is read only by the {Side(role)} side of the {scheme} endpoint; " +
                        $"<{step.Name.LocalName}> creates the {Side(side)}, which refuses it."));
                }
            }

            // Beside the option that names the connection factory a connection parameter is refused,
            // the factory being the whole connection — the declarations and wording of
            // EndpointOptions.BindFromUri ([ConnectionFactoryReference], [ConnectionParameter]).
            if (component.Options.FirstOrDefault(o => o.ConnectionFactoryReference) is { } reference
                && pairs.FirstOrDefault(p => string.Equals(p.Key, reference.Name, StringComparison.OrdinalIgnoreCase))
                    is { Value.Length: > 0 } factory)
            {
                var beside = written
                    .Where(k => component.Options.Any(o => o.ConnectionParameter && string.Equals(o.Name, k, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (beside.Count > 0)
                    findings.Add(new PackageFinding(true, Position(file, at),
                        $"<{scheme}>: {factory.Key} '{factory.Value}' sets the whole connection, so {string.Join(", ", beside)} " +
                        $"cannot be given on the URI as well. Set them on the factory, or drop {factory.Key} and give the connection on the URI."));
            }

            if (component is not { Lenient: false, OptionsType: { } optionsTypeName })
                return;
            if (typeResolver(optionsTypeName) is not { } optionsType)
                return;
            var writable = optionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = written.Where(k => !writable.Contains(k)).ToList();
            if (unknown.Count == 0)
                return;
            findings.Add(new PackageFinding(true, Position(file, at), $"<{scheme}>: {EngineWording(optionsType, scheme, unknown)}"));
        }

        private static string Side(EndpointRole role) => role == EndpointRole.Consumer ? "consumer" : "producer";

        private CatalogComponent? Find(string scheme)
            => catalog.FirstOrDefault(c => string.Equals(c.Scheme, scheme, StringComparison.OrdinalIgnoreCase)
                                            || c.AlternateSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// The refusal the engine gives for these names. The options type comes from the built
        /// output, possibly in its own load context, so the call goes through reflection; the
        /// engine throws <see cref="ArgumentException"/> (a core type, shared by every context).
        /// </summary>
        private static string EngineWording(Type optionsType, string scheme, IReadOnlyList<string> unknown)
        {
            // Without a parameterless constructor there is no options object to ask; the finding
            // stands all the same, named plainly.
            if (optionsType.GetConstructor(Type.EmptyTypes) is null)
                return string.Join(" ", unknown.Select(k => $"'{k}' is not an option of the {scheme} endpoint."));
            var options = Activator.CreateInstance(optionsType)!;
            var bind = optionsType.GetMethod("BindFromUri", [typeof(IReadOnlyDictionary<string, string>)])
                ?? throw new InvalidOperationException($"{optionsType.FullName} has no BindFromUri — not an endpoint options type.");
            try
            {
                bind.Invoke(options, [unknown.ToDictionary(k => k, _ => string.Empty, StringComparer.OrdinalIgnoreCase)]);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException refusal)
            {
                // .NET appends " (Parameter 'name')" to the message; the finding names the parameter already.
                var suffix = refusal.ParamName is { } parameter ? $" (Parameter '{parameter}')" : null;
                return suffix is not null && refusal.Message.EndsWith(suffix, StringComparison.Ordinal)
                    ? refusal.Message[..^suffix.Length]
                    : refusal.Message;
            }
            throw new InvalidOperationException(
                $"{optionsType.FullName} accepted {string.Join(", ", unknown)} although it has no such options and is not lenient — " +
                "the package gate and EndpointOptions.BindFromUri disagree.");
        }
    }

    private static void LoadDocument(string file, string path, List<(string, XDocument)> documents, List<PackageFinding> findings)
    {
        try
        {
            documents.Add((file, SafeXml.LoadFile(path, LoadOptions.SetLineInfo)));
        }
        catch (System.Xml.XmlException ex)
        {
            findings.Add(new PackageFinding(true, file, $"not well-formed XML: {ex.Message}"));
        }
    }

    private static void ScanPlaceholders(string value, SortedSet<string> requiredKeys)
    {
        foreach (Match match in Placeholder.Matches(value))
        {
            if (!match.Groups[2].Success)
                requiredKeys.Add(match.Groups[1].Value.Trim());
        }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}

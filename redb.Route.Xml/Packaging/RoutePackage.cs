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
public static partial class RoutePackage
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
    /// The first directory a route's <c>file=</c> references are looked up in outside a package. A
    /// route under <c>{project}/routes/</c> reads <c>{project}/resources/</c>; any other file reads its
    /// own directory. The full search, with the project root after it, is
    /// <see cref="ResourceCandidatesFor"/>.
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
    /// Where a route's <c>file=</c> reference is read from outside a package, in order. A route under
    /// <c>{project}/routes/</c> reads <c>{project}/resources/{reference}</c> (<c>file="order.xsd"</c>), then
    /// <c>{project}/{reference}</c> (<c>file="resources/order.xsd"</c>): the rule the packaging checks hold a
    /// project to and the Tsak loader applies to a package. Any other route file reads its own directory; a
    /// rooted reference is itself.
    /// </summary>
    public static IReadOnlyList<string> ResourceCandidatesFor(string routeFile, string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (Path.IsPathRooted(reference))
            return [reference];
        var directory = Path.GetDirectoryName(Path.GetFullPath(routeFile))!;
        return string.Equals(Path.GetFileName(directory), RoutesDir, StringComparison.OrdinalIgnoreCase)
            ? ProjectResourceCandidates(Path.GetDirectoryName(directory)!, reference)
            : [Path.GetFullPath(Path.Combine(directory, reference))];
    }

    /// <summary>
    /// Runs the <see cref="IPackageCheck"/> implementations of the build output over the documents. A check that throws
    /// is a defect of that check, reported as an error naming it, and the others still run.
    /// </summary>
    private static void RunPackageChecks(string projectDir, List<(string File, XDocument Document)> documents,
        IReadOnlyList<IPackageCheck>? checks, List<PackageFinding> findings)
    {
        if (checks is not { Count: > 0 })
            return;
        var context = new PackageCheckContext(projectDir,
            documents.Select(d => new PackageDocument(d.File, d.Document)).ToList(), findings);
        foreach (var check in checks)
        {
            try
            {
                check.Check(context);
            }
            catch (Exception ex)
            {
                findings.Add(new PackageFinding(true, check.GetType().FullName ?? check.GetType().Name,
                    $"the package check failed: {ex.GetType().Name}: {ex.Message}"));
            }
        }
    }

    /// <summary>
    /// Where a resource reference lands in the package, as the checks hold it (findings register row 42): Pack ships the
    /// <c>resources/</c> tree, so a reference resolves only to a file inside it. Returns the file, or <c>null</c> with
    /// <paramref name="problem"/> saying why: missing, or present outside <c>resources/</c> (a file beside the project,
    /// which local runs find and the worker never gets).
    /// </summary>
    internal static string? PackagedResource(string projectDir, string reference, out string? problem)
    {
        problem = null;
        var resources = Path.GetFullPath(Path.Combine(projectDir, ResourcesDir)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var candidates = Path.IsPathRooted(reference) ? [reference] : ProjectResourceCandidates(projectDir, reference);
        foreach (var candidate in candidates)
            if (File.Exists(candidate) && Path.GetFullPath(candidate).StartsWith(resources, comparison))
                return candidate;
        problem = candidates.FirstOrDefault(File.Exists) is { } outside
            ? $"is at '{Path.GetRelativePath(projectDir, outside)}', outside '{ResourcesDir}/': pack ships only '{ResourcesDir}/', so the worker would not find it. Move it under '{ResourcesDir}/'"
            : $"is not under '{ResourcesDir}/'";
        return null;
    }

    private static string[] ProjectResourceCandidates(string projectDir, string reference) =>
    [
        Path.GetFullPath(Path.Combine(projectDir, ResourcesDir, reference)),
        Path.GetFullPath(Path.Combine(projectDir, reference)),
    ];

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
    /// <paramref name="dependencies"/> — the manifest names of the packages this one needs opened first (a carrier of
    /// shared companion DLLs) — go into <c>manifest.json</c> as written; only their shape is checked here, since the
    /// packages they name are on the worker, not in this project.
    /// <paramref name="checks"/> — the <see cref="IPackageCheck"/> implementations of the build output — run after the
    /// gate's own checks, over the same documents; a check that throws is an error naming it.
    /// </summary>
    public static PackageResult Check(string projectDir, string name, string version,
        Func<string, Type?>? typeResolver = null,
        IReadOnlyList<IXmlElementContribution>? extensions = null,
        IReadOnlyList<CatalogComponent>? catalog = null,
        IReadOnlyList<string>? dependencies = null,
        IReadOnlyList<IPackageCheck>? checks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDir);
        var findings = new List<PackageFinding>();
        var artifacts = CollectArtifacts(projectDir, findings);
        CheckPackageConfigName(projectDir, name, findings);
        CheckDependencies(name, dependencies ?? [], findings);
        var requiredKeys = RunChecks(projectDir, artifacts, findings, typeResolver, extensions, catalog, checks);
        var manifest = BuildManifest(projectDir, name, version, artifacts, requiredKeys)
            with { Dependencies = [.. dependencies ?? []] };
        return new PackageResult(manifest, findings);
    }

    /// <summary>
    /// A dependency is another package's manifest name: not blank, not a file (<c>carrier.tpkg</c>, a path), not this
    /// package, and listed once. Names compare ignoring case, as the worker matches them.
    /// </summary>
    private static void CheckDependencies(string name, IReadOnlyList<string> dependencies, List<PackageFinding> findings)
    {
        const string where = "--depends";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in dependencies)
        {
            if (string.IsNullOrWhiteSpace(dependency) || dependency.Trim() != dependency)
                findings.Add(new PackageFinding(true, where,
                    $"'{dependency}' is not a package name: a dependency names another package's manifest Name, without spaces around it."));
            else if (dependency.StartsWith('-'))
                findings.Add(new PackageFinding(true, where,
                    $"'{dependency}' is an option, not a package name: --depends needs the name of the package after it."));
            else if (dependency.IndexOfAny(['/', '\\']) >= 0
                     || dependency.EndsWith(".tpkg", StringComparison.OrdinalIgnoreCase)
                     || dependency.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                findings.Add(new PackageFinding(true, where,
                    $"'{dependency}' is a file; a dependency names the other package by its manifest Name (the --name it was packed with)."));
            else if (string.Equals(dependency, name, StringComparison.OrdinalIgnoreCase))
                findings.Add(new PackageFinding(true, where, $"the package '{name}' cannot depend on itself."));
            else if (!seen.Add(dependency))
                findings.Add(new PackageFinding(true, where, $"'{dependency}' is listed more than once."));
        }
    }

    /// <summary>
    /// Builds the package: checks first (errors refuse the build), then the Ф5.1 layout in
    /// <paramref name="outDir"/>, then the <c>.tpkg</c> zip beside it. Returns the result with
    /// the manifest and every finding.
    /// </summary>
    public static PackageResult Build(string projectDir, string name, string version, string outDir,
        IReadOnlyList<string>? entryPoints = null, Func<string, Type?>? typeResolver = null,
        IReadOnlyList<IXmlElementContribution>? extensions = null,
        IReadOnlyList<CatalogComponent>? catalog = null,
        IReadOnlyList<string>? dependencies = null,
        IReadOnlyList<IPackageCheck>? checks = null)
    {
        var result = Check(projectDir, name, version, typeResolver, extensions, catalog, dependencies, checks);
        if (result.Errors.Any())
            return result;

        // An entry DLL is packed at the package root, so the manifest names it by its file name: the loader looks it up
        // in the zip by exactly this name, and a build path ("bin/Release/net10.0/X.dll") would not be found there.
        var manifest = result.Manifest with
        {
            EntryPoints = (entryPoints ?? []).Select(e => Path.GetFileName(Path.GetFullPath(e, projectDir))).ToList(),
        };
        var stage = Path.Combine(outDir, $"{name}-{version}");
        if (Directory.Exists(stage))
            Directory.Delete(stage, recursive: true);
        Directory.CreateDirectory(stage);

        File.WriteAllText(Path.Combine(stage, "manifest.json"),
            JsonSerializer.Serialize(manifest, ManifestJson));

        // L4 — the identity stub only (эскиз 11 §1.4: secrets never ride inside the package).
        var l4Source = FindPackageConfig(projectDir, name);
        File.WriteAllText(Path.Combine(stage, $"{name}.config.json"),
            l4Source is not null
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

        foreach (var entryPoint in entryPoints ?? [])
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

    /// <summary>
    /// The package reads its L4 config from <c>config/&lt;name&gt;.config.json</c> only. When that file is absent but
    /// another <c>*.config.json</c> is there, the package name and the file disagree (a rename, a <c>--name</c>), and the
    /// build would write the default config instead: a warning naming both files.
    /// </summary>
    private static void CheckPackageConfigName(string projectDir, string name, List<PackageFinding> findings)
    {
        var configDir = Path.Combine(projectDir, ConfigDir);
        var expected = $"{name}.config.json";
        if (!Directory.Exists(configDir) || FindPackageConfig(projectDir, name) is not null)
            return;

        var found = Directory.GetFiles(configDir, "*.config.json")
            .Select(f => $"{ConfigDir}/{Path.GetFileName(f)}")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (found.Count == 0)
            return;

        findings.Add(new PackageFinding(false, $"{ConfigDir}/{expected}",
            $"the package '{name}' reads its config from {ConfigDir}/{expected}, which is missing, but {string.Join(", ", found)} "
            + $"is there; the package gets the default config (ContextName, AutoStart) instead. "
            + $"Rename the file to {expected}, or pack with the name it was made for (--name)."));
    }

    /// <summary>
    /// <c>config/&lt;name&gt;.config.json</c> with exactly this file name, letter case included, on every system. A plain
    /// <see cref="File.Exists(string)"/> ignored the case on Windows and macOS and kept it on Linux, so
    /// <c>--name orders</c> next to <c>Orders.config.json</c> packed that file on a Windows machine and the default
    /// config on a Linux build agent.
    /// </summary>
    private static string? FindPackageConfig(string projectDir, string name)
    {
        var configDir = Path.Combine(projectDir, ConfigDir);
        if (!Directory.Exists(configDir))
            return null;
        var expected = $"{name}.config.json";
        return Directory.EnumerateFiles(configDir, "*.config.json")
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), expected, StringComparison.Ordinal));
    }

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
        if (FindPackageConfig(projectDir, name) is { } l4)
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
        IReadOnlyList<CatalogComponent>? catalog = null,
        IReadOnlyList<IPackageCheck>? checks = null)
    {
        var registry = ElementRegistry.CreateDefault(extensions);
        var requiredKeys = new SortedSet<string>(StringComparer.Ordinal);
        var declaredBeans = new HashSet<string>(StringComparer.Ordinal);
        var beanDeclarations = new List<(string File, BeanDeclaration Bean)>();
        var beanMethodCalls = new List<(string File, string Bean, string Method)>();
        var referencedBeans = new List<(string File, string Name)>();
        var typedReferences = new List<TypedReference>();
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
        CheckXmlJsonFormats(projectDir, documents, findings);
        CheckXsdSchemas(projectDir, documents, findings);
        RunPackageChecks(projectDir, documents, checks, findings);

        // Elements that register objects under a name of their own (<redb><idempotentRepository
        // name>) declare that name like a <bean> does; references that must name an object of a
        // given type (<idempotentConsumer repository>) are held against it once types are known.
        var registered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (file, document) in documents)
            foreach (var element in document.Root?.Elements() ?? [])
                CollectRegistryAttributes(file, element, registry.Find(element.Name.LocalName)?.Spec, registry,
                    registered, typedReferences);
        declaredBeans.UnionWith(registered.Keys);

        // bean: calls in expressions: a syntax error is an error at the attribute; the name joins the #name references,
        // so an undeclared one is the same warning; the method is held against a declared type below, with --bin.
        var expressionBeanCalls = new List<ExpressionBeanCall>();
        foreach (var (file, document) in documents)
            if (document.Root is { } root)
                CollectExpressionBeanCalls(file, root, registry.Find(root.Name.LocalName)?.Spec, registry, expressionBeanCalls, findings);
        foreach (var call in expressionBeanCalls)
            referencedBeans.Add((call.File, call.Reference));

        // Options marked [RegistryReference] name a registry object bare as well (connectionFactory=main): the core's
        // components always, the connectors of the built output with --bin.
        var markedReferences = new RegistryReferenceCollector(WithCoreComponents(catalog), referencedBeans, typedReferences);
        foreach (var (file, document) in documents)
            foreach (var element in document.Root?.Elements() ?? [])
                CheckEndpoints(file, element, registry.Find(element.Name.LocalName)?.Spec, registry, markedReferences);

        // A scheme on the side its component lacks ([EndpointRole] on the class), in the words the runtime refuses it
        // with: the core's components always, the connectors of the built output with --bin.
        var sides = new EndpointSideChecker(WithCoreComponents(catalog), findings);
        foreach (var (file, document) in documents)
            foreach (var element in document.Root?.Elements() ?? [])
                CheckEndpoints(file, element, registry.Find(element.Name.LocalName)?.Spec, registry, sides);
        foreach (var (file, document) in documents)
            foreach (var element in document.Descendants())
                foreach (var attribute in element.Attributes())
                    markedReferences.CheckAttribute(file, element, attribute);

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
            CheckUnclosedPlaceholders(file, root, registry.Find(root.Name.LocalName)?.Spec, registry, findings);
        }

        CheckRouteBuilders(documents, typeResolver, findings);

        if (typeResolver is not null)
        {
            var instanceTypes = RunDeepBeanChecks(beanDeclarations, beanMethodCalls, typeResolver, findings);
            CheckExpressionBeanCalls(expressionBeanCalls, instanceTypes, findings);
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
    /// Every <c>&lt;routeBuilder&gt;</c> of <c>context.xml</c> names its type; with the built assemblies at hand the type
    /// exists and is one the loader accepts (a public, non-abstract <c>RouteBuilder</c> with a public parameterless
    /// constructor). The worker would refuse the package on any of these, so the build does.
    /// </summary>
    private static void CheckRouteBuilders(List<(string File, XDocument Document)> documents,
        Func<string, Type?>? typeResolver, List<PackageFinding> findings)
    {
        foreach (var (file, document) in documents)
        {
            if (document.Root?.Name.LocalName != "context")
                continue;
            foreach (var element in document.Root.Elements().Where(e => e.Name.LocalName == "routeBuilder"))
            {
                var line = (IXmlLineInfo)element;
                var at = $"{file}({line.LineNumber},{line.LinePosition})";
                var typeName = element.Attribute("type")?.Value.Trim();
                if (string.IsNullOrEmpty(typeName))
                {
                    findings.Add(new PackageFinding(true, at, "<routeBuilder> needs type=\"Namespace.Type, Assembly\"."));
                    continue;
                }
                // A type that does not resolve is reported by the generic type-attribute check.
                if (typeResolver?.Invoke(typeName) is not { } type)
                    continue;
                if (CoreElements.RouteBuilderContextContribution.Refusal(type) is { } reason)
                    findings.Add(new PackageFinding(true, at, $"<routeBuilder>: '{typeName}' {reason}"));
            }
        }
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
        if (PackagedResource(projectDir, value, out var problem) is null)
        {
            var line = (IXmlLineInfo)attribute;
            findings.Add(new PackageFinding(true, $"{file}({line.LineNumber},{line.LinePosition})",
                $"resource '{value}' referenced by <{element.Name.LocalName} {attribute.Name.LocalName}=…> {problem}."));
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
        var outside = new System.Text.StringBuilder();
        var position = 0;
        foreach (var hole in redb.Route.Expressions.TemplateScanner.Scan(value).Holes)
        {
            outside.Append(value, position, hole.Start - position).Append(' ');
            position = hole.Start + hole.Length;
        }
        outside.Append(value, position, value.Length - position);
        var text = outside.ToString();
        return Operators.Any(op => text.Contains(op, StringComparison.Ordinal));
    }

    /// <summary>
    /// A <c>${</c> that nothing closes, in an attribute the engine reads as an expression or an endpoint URI, or in
    /// a structured endpoint child (its attributes and text are URI options): the load fails on it, so the build
    /// fails on it here, with the attribute's position.
    /// </summary>
    private static void CheckUnclosedPlaceholders(string file, XElement element, ElementSpec? spec,
        ElementRegistry registry, List<PackageFinding> findings, bool endpointChild = false)
    {
        // <from> is the route's own element, not a contribution: its uri= and its endpoint child are addresses too.
        var isFrom = !endpointChild && element.Name.LocalName == "from";
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration || attribute.Name.NamespaceName.Length > 0)
                continue;
            var type = spec?.Attributes.FirstOrDefault(a =>
                string.Equals(a.Name, attribute.Name.LocalName, StringComparison.Ordinal))?.Type;
            if (endpointChild || type is AttributeType.Expression or AttributeType.Uri || (isFrom && attribute.Name.LocalName == "uri"))
                ReportUnclosed(file, attribute, attribute.Value, $"{attribute.Name.LocalName}=", findings);
        }
        if (endpointChild)
            foreach (var text in element.Nodes().OfType<XText>())
                ReportUnclosed(file, text, text.Value, $"<{element.Name.LocalName}> text", findings);

        foreach (var child in element.Elements())
        {
            if ((spec is { TakesEndpoint: true } || isFrom) && !endpointChild)
            {
                CheckUnclosedPlaceholders(file, child, null, registry, findings, endpointChild: true);
                continue;
            }
            var childSpec = spec?.Children.FirstOrDefault(c =>
                                string.Equals(c.Name, child.Name.LocalName, StringComparison.Ordinal))
                            ?? registry.Find(child.Name.LocalName)?.Spec;
            CheckUnclosedPlaceholders(file, child, childSpec, registry, findings, endpointChild);
        }
    }

    /// <summary>
    /// One <c>bean:name.Method(args)</c> call an attribute or a text of the package makes, with its position; a
    /// <c>ref:name</c> is one with no method.
    /// </summary>
    private sealed record ExpressionBeanCall(string File, string At, string What, string Reference, string? Method, int Arguments);

    /// <summary>
    /// Every <c>bean:</c> call in the package's expressions, parsed as the engine parses it: a bare one in an
    /// expression attribute, one inside a <c>${...}</c> of an expression or address attribute, of a structured
    /// endpoint child, or of an element's text. A call that does not parse is an error at its attribute.
    /// </summary>
    private static void CollectExpressionBeanCalls(string file, XElement element, ElementSpec? spec,
        ElementRegistry registry, List<ExpressionBeanCall> calls, List<PackageFinding> findings, bool endpointChild = false)
    {
        var isFrom = !endpointChild && element.Name.LocalName == "from";
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration || attribute.Name.NamespaceName.Length > 0)
                continue;
            var attributeSpec = spec?.Attributes.FirstOrDefault(a =>
                string.Equals(a.Name, attribute.Name.LocalName, StringComparison.Ordinal));
            var type = attributeSpec?.Type;
            var bare = type is AttributeType.Expression;
            if (bare || endpointChild || type is AttributeType.Uri || (isFrom && attribute.Name.LocalName == "uri"))
                CollectFrom(file, attribute, attribute.Value, $"{attribute.Name.LocalName}=", bare,
                    condition: attributeSpec is { Condition: true }, endpoint: !bare, calls, findings,
                    constantKey: attributeSpec is { ConstantKey: true });
        }
        if (endpointChild || spec is { AllowsTextContent: true })
            foreach (var text in element.Nodes().OfType<XText>())
                CollectFrom(file, text, text.Value, $"<{element.Name.LocalName}> text", bare: false,
                    condition: false, endpoint: endpointChild, calls, findings);

        foreach (var child in element.Elements())
        {
            if ((spec is { TakesEndpoint: true } || isFrom) && !endpointChild)
            {
                CollectExpressionBeanCalls(file, child, null, registry, calls, findings, endpointChild: true);
                continue;
            }
            var childSpec = spec?.Children.FirstOrDefault(c =>
                                string.Equals(c.Name, child.Name.LocalName, StringComparison.Ordinal))
                            ?? registry.Find(child.Name.LocalName)?.Spec;
            CollectExpressionBeanCalls(file, child, childSpec, registry, calls, findings, endpointChild);
        }
    }

    /// <summary>The text of <paramref name="value"/> with every hole replaced by a space.</summary>
    private static string OutsideHoles(string value, redb.Route.Expressions.TemplateScanner.ScanResult scan)
    {
        var outside = new System.Text.StringBuilder();
        var position = 0;
        foreach (var hole in scan.Holes)
        {
            outside.Append(value, position, hole.Start - position).Append(' ');
            position = hole.Start + hole.Length;
        }
        return outside.Append(value, position, value.Length - position).ToString();
    }

    private static void CollectFrom(string file, XObject at, string value, string what, bool bare, bool condition,
        bool endpoint, List<ExpressionBeanCall> calls, List<PackageFinding> findings, bool constantKey = false)
    {
        var scan = redb.Route.Expressions.TemplateScanner.Scan(value);
        var whole = bare && scan.Holes.Count == 0 && scan.Unclosed.Count == 0;
        // A key that may be a constant (<cache key="orders-all">) is the key as written when the engine reads it so.
        if (whole && constantKey && redb.Route.Expressions.ExpressionResolver.IsConstantKey(value))
            return;
        IEnumerable<string> expressions = whole ? [value] : scan.Holes.Select(h => h.InnerOf(value)).ToList();
        var line = (IXmlLineInfo)at;
        var position = $"{file}({line.LineNumber},{line.LinePosition})";

        // Each expression as the engine compiles it (findings register row 32): one that does not compile fails the
        // route at build, so it fails the package here, in the engine's words. In an endpoint, ${file:...} in any case
        // belongs to the file components, which check it themselves (row 34).
        // A bean:/ref: expression's error waits for the call check below, whose parser message is more precise.
        var deferred = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var expression in expressions.Distinct(StringComparer.Ordinal))
        {
            if (endpoint && expression.TrimStart().StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                continue;
            var (error, warning) = redb.Route.Expressions.ExpressionResolver.GateCheck(expression, placeholder: !whole, condition);
            if (error is not null && redb.Route.Expressions.ExpressionResolver.ContainsNamespacedCall(expression))
                deferred[expression] = error;
            else if (error is not null)
                findings.Add(new PackageFinding(true, position, $"{what} {error}"));
            if (warning is not null)
                findings.Add(new PackageFinding(false, position, $"{what} {warning}"));
        }

        if (!value.Contains("bean:", StringComparison.Ordinal) && !value.Contains("ref:", StringComparison.Ordinal))
        {
            foreach (var error in deferred.Values)
                findings.Add(new PackageFinding(true, position, $"{what} {error}"));
            return;
        }

        // A bare call whose arguments are placeholders (bean:svc.M(${header.x})): the ${ makes the whole text a
        // template, so it renders to text instead of calling, and in a condition that text is always true.
        var outside = bare && scan.Holes.Count > 0 ? OutsideHoles(value, scan) : null;
        if (outside is not null && redb.Route.Expressions.ExpressionResolver.ContainsNamespace(outside, "bean"))
            findings.Add(new PackageFinding(false, position,
                $"{what} holds a bean: call outside ${{...}} with ${{...}} arguments: the text is a template and renders " +
                "instead of calling (in a condition it is always true). Write the arguments bare, bean:svc.Check(header.x), " +
                "or the whole call as one placeholder, ${bean:svc.Check(${header.x})}."));

        // The same for a registry name: a ${ anywhere makes the whole text a template, so ref:x is text, not the object.
        if (outside is not null && redb.Route.Expressions.ExpressionResolver.ContainsNamespace(outside, "ref"))
            findings.Add(new PackageFinding(false, position,
                $"{what} holds a ref: name outside ${{...}} in a text with ${{...}}: the text is a template and renders " +
                "ref:name as text instead of reading the object (in a condition it is always true). Write the whole " +
                "expression bare, ref:limit > header.x, or as one placeholder, ${ref:limit > header.x}."));

        foreach (var expression in expressions.Where(redb.Route.Expressions.ExpressionResolver.ContainsNamespacedCall))
        {
            try
            {
                foreach (var call in redb.Route.Expressions.BeanCallSite.CallsIn(expression))
                    calls.Add(new ExpressionBeanCall(file, position, what, call.Reference, call.Method, call.Arguments.Count));
                foreach (var reference in redb.Route.Expressions.BeanCallSite.RefsIn(expression))
                    calls.Add(new ExpressionBeanCall(file, position, what, reference.Reference, null, 0));
            }
            catch (InvalidOperationException ex)
            {
                // The parser's words name a broken call better than the compile error recorded above.
                findings.Add(new PackageFinding(true, position, $"{what} {ex.Message}"));
                deferred.Remove(expression);
            }
        }
        foreach (var error in deferred.Values)
            findings.Add(new PackageFinding(true, position, $"{what} {error}"));
    }

    /// <summary>
    /// A call to a declared bean whose type resolves: the method exists with that many bindable parameters (the
    /// binding's own words), and it is synchronous. Both would fail the call on its first message.
    /// </summary>
    private static void CheckExpressionBeanCalls(List<ExpressionBeanCall> calls, IReadOnlyDictionary<string, Type> instanceTypes,
        List<PackageFinding> findings)
    {
        foreach (var call in calls.DistinctBy(c => (c.At, c.Reference, c.Method, c.Arguments)))
        {
            if (call.Method is null || !instanceTypes.TryGetValue(call.Reference, out var type))
                continue; // a ref: calls nothing; undeclared: the #name warning; a C#-registered bean is checked when called
            try
            {
                var bound = redb.Route.Components.Bean.BeanInvoker.ForValues(type, call.Method, call.Arguments, "#" + call.Reference);
                if (redb.Route.Components.Bean.BeanInvoker.IsAwaitable(bound.Method.ReturnType))
                    findings.Add(new PackageFinding(true, call.At,
                        $"{call.What} {redb.Route.Expressions.BeanCallSite.AwaitableRefused(call.Reference, call.Method, bound.Method.ReturnType)}"));
            }
            catch (InvalidOperationException ex)
            {
                findings.Add(new PackageFinding(true, call.At, $"{call.What} {ex.Message}"));
            }
        }
    }

    private static void ReportUnclosed(string file, XObject at, string value, string what, List<PackageFinding> findings)
    {
        if (!value.Contains("${", StringComparison.Ordinal) || redb.Route.Expressions.TemplateScanner.Scan(value).Unclosed is not [var index, ..])
            return;
        var line = (IXmlLineInfo)at;
        findings.Add(new PackageFinding(true, $"{file}({line.LineNumber},{line.LinePosition})",
            $"{what} {redb.Route.Expressions.TemplateScanner.Unclosed(value, index)}"));
    }

    /// <summary>What turns a rendered condition into a comparison the author expected to be evaluated.</summary>
    private static readonly string[] Operators =
        ["==", "!=", "=~", ">=", "<=", ">", "<", "&&", "||", " and ", " or ", " eq ", " ne ", " range "];

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
            else if (bean.ConstructorArgs.Count > 0)
            {
                // The constructor is chosen at load from the built arguments (ActivatorUtilities): each argument goes to a
                // parameter its value is assignable to, text as System.String. So the arguments are checked as built.
                var built = new List<Type?>();
                for (var i = 0; i < bean.ConstructorArgs.Count; i++)
                {
                    var value = bean.ConstructorArgs[i].Value;
                    built.Add(value switch
                    {
                        ScalarBeanValue => typeof(string),
                        RefBeanValue reference => instanceTypes.TryGetValue(reference.Bean, out var referenced) ? referenced : null,
                        NestedBeanValue nested => Check(nested.Bean),
                        ListBeanValue { ElementTypeName: { } element } => typeResolver(element)?.MakeArrayType(),
                        _ => null,
                    });
                    if (value is not NestedBeanValue)
                        CheckValue(value, null, $"constructorArg {i + 1}");
                }
                if (built.All(t => t is not null) && !type.GetConstructors().Any(c => TakesBuilt(c.GetParameters(), built!)))
                    return Error(bean.At, $"{label}: no public constructor of '{type.FullName}' takes ({BeanFactory.Signature(built!)}): "
                        + "without type= a constructorArg is passed as it is built, text as System.String."
                        + TypedHint(type, bean.ConstructorArgs.Count));
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

        /// <summary>
        /// Whether ActivatorUtilities could pass the built arguments to these parameters: every argument to its own
        /// parameter of a type it is assignable to (the parameters left over come from DI or their defaults).
        /// </summary>
        private static bool TakesBuilt(ParameterInfo[] parameters, IReadOnlyList<Type> built)
        {
            var taken = new bool[parameters.Length];
            foreach (var argument in built)
            {
                var index = -1;
                for (var i = 0; i < parameters.Length && index < 0; i++)
                {
                    if (!taken[i] && parameters[i].ParameterType.IsAssignableFrom(argument))
                        index = i;
                }
                if (index < 0)
                    return false;
                taken[index] = true;
            }
            return true;
        }

        /// <summary>The type= to write when exactly one public constructor takes that many arguments.</summary>
        private static string TypedHint(Type type, int count)
        {
            var candidates = type.GetConstructors().Where(c => c.GetParameters().Length == count).ToList();
            if (candidates.Count != 1)
                return " Name the parameter types with <constructorArg type=\"…\">.";
            var parameters = candidates[0].GetParameters();
            return " Name the parameter types: "
                   + string.Join(" ", parameters.Select(p => $"<constructorArg type=\"{p.ParameterType.FullName}\" …/>")) + ".";
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
        IEndpointVisitor checker)
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

    /// <summary>What <see cref="CheckEndpoints"/> hands each endpoint it finds to, in the URI form or the structured form.</summary>
    private interface IEndpointVisitor
    {
        void CheckUri(string file, XElement at, string uri, EndpointRole side);

        void CheckStructured(string file, XElement endpoint, EndpointRole side);
    }

    /// <summary>The components of the core (vm, direct-vm, bean, …), whose options the gate knows without <c>--bin</c>.</summary>
    private static readonly Lazy<IReadOnlyList<CatalogComponent>> CoreCatalog = new(() => ComponentCatalog.Build(
        typeof(EndpointOptions).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(redb.Route.Abstractions.IComponent).IsAssignableFrom(t))
            .Select(t => (Type: t, Constructor: t.GetConstructors().FirstOrDefault(c => c.GetParameters().All(p => p.IsOptional))))
            .Where(c => c.Constructor is not null)
            // A constructor whose parameters are all optional counts as a default one: log takes an optional logger factory.
            .Select(c => (redb.Route.Abstractions.IComponent)c.Constructor!.Invoke(
                [.. c.Constructor.GetParameters().Select(p => p.DefaultValue)]))));

    /// <summary>The catalog of the built output, and the core components it does not list itself.</summary>
    private static IReadOnlyList<CatalogComponent> WithCoreComponents(IReadOnlyList<CatalogComponent>? catalog)
    {
        if (catalog is null)
            return CoreCatalog.Value;
        var schemes = catalog.Select(c => c.Scheme).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. catalog, .. CoreCatalog.Value.Where(c => !schemes.Contains(c.Scheme))];
    }

    /// <summary>
    /// The registry names an endpoint writes into an option marked <c>[RegistryReference]</c>
    /// (<see cref="CatalogOption.Reference"/>). A bare name (<c>connectionFactory=main</c>) joins the package's
    /// references, as the position rule already adds a <c>#name</c>, so one no <c>&lt;bean&gt;</c> declares is the usual
    /// warning. A name in an option marked with a type, bare or with <c>#</c>, is held against that type, per connector
    /// (<see cref="CheckTypedReferences"/>); an option marked without one (<c>object</c>) is never. A value built from
    /// configuration or an expression (<c>{{…}}</c>, <c>${…}</c>) is not knowable here.
    /// </summary>
    private sealed class RegistryReferenceCollector(
        IReadOnlyList<CatalogComponent> catalog,
        List<(string File, string Name)> references,
        List<TypedReference> typed) : IEndpointVisitor
    {
        private readonly HashSet<XAttribute> _endpointAttributes = [];

        public void CheckUri(string file, XElement at, string uri, EndpointRole side)
        {
            if (at.Attribute("uri") is { } attribute)
                _endpointAttributes.Add(attribute);
            Read(file, at, "uri", uri);
        }

        /// <summary>
        /// Any other attribute whose value is a URI with options (a constant <c>recipientList expr="vm:a?…"</c>, a
        /// <c>routingSlip</c> or <c>dynamicRouter</c> address): read the same way, per scheme, as the gate has always
        /// read a <c>#name</c> there. The endpoint positions <see cref="CheckEndpoints"/> walks are read once, there.
        /// </summary>
        public void CheckAttribute(string file, XElement element, XAttribute attribute)
        {
            if (_endpointAttributes.Contains(attribute))
                return;
            var value = attribute.Value;
            var query = value.IndexOf('?');
            if (query < 0 || !UriScheme.IsMatch(value[..query]))
                return;
            Read(file, element, attribute.Name.LocalName, value);
        }

        private void Read(string file, XElement at, string attributeName, string uri)
        {
            if (uri.StartsWith("{{", StringComparison.Ordinal) || uri.StartsWith("${", StringComparison.Ordinal))
                return;
            var colon = uri.IndexOf(':');
            if (colon <= 0 || Find(uri[..colon]) is not { } component)
                return;
            Collect(file, at, component, EndpointUriParser.Parse(uri).RawParameters.Select(p => (p.Key, p.Value)),
                key => $"{attributeName}=…{key}");
        }

        public void CheckStructured(string file, XElement endpoint, EndpointRole side)
        {
            if (Find(endpoint.Name.LocalName) is not { } component)
                return;
            // Shape errors of the structured form are the parameter checker's to report; here they only end the walk.
            if (StructuredEndpoint.Options(endpoint, component.PathSynonym, (_, _) => { }) is { } options)
                Collect(file, endpoint, component, options.Select(o => (o.Key, o.Value)), key => key);
        }

        private void Collect(string file, XElement at, CatalogComponent component,
            IEnumerable<(string Key, string Value)> parameters, Func<string, string> attribute)
        {
            foreach (var (key, value) in parameters)
            {
                if (component.Options.FirstOrDefault(o => string.Equals(o.Name, key, StringComparison.OrdinalIgnoreCase))
                        is not { Reference: { } expected })
                    continue;
                var written = value.Trim();
                if (written.Contains("{{", StringComparison.Ordinal) || written.Contains("${", StringComparison.Ordinal))
                    continue; // known only at run time
                var bare = !written.StartsWith('#');
                if (LeadingReference.Match(bare ? "#" + written : written) is not { Success: true } match
                    || match.Length != (bare ? written.Length + 1 : written.Length))
                    continue; // empty, or not a plain registry name
                var name = match.Groups[1].Value;
                if (bare)
                    references.Add((file, name)); // a #name is read by the position rule already
                if (expected != "object")
                    typed.Add(new TypedReference(file, at, attribute(key), name, expected));
            }
        }

        private CatalogComponent? Find(string scheme)
            => catalog.FirstOrDefault(c => string.Equals(c.Scheme, scheme, StringComparison.OrdinalIgnoreCase)
                                            || c.AlternateSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Holds an endpoint's parameter names against its connector the way
    /// <c>EndpointOptions.BindFromUri</c> does when the endpoint is created: a name with no
    /// writable option is refused unless the connector is lenient. The wording is the engine's
    /// own — the checker binds the unknown names into a fresh options object and reports what
    /// the engine said, nearest option and connector hint included. Names built at run time
    /// (<c>${…}</c>, <c>{{…}}</c>) are not knowable here and are left to the engine.
    /// </summary>
    /// <summary>
    /// Refuses a scheme on the side its component does not have: <c>&lt;from uri="log:…"/&gt;</c>,
    /// <c>&lt;to&gt;&lt;timer/&gt;&lt;/to&gt;</c>. The position is the step for the URI form and the endpoint element for the
    /// structured form; the message is the runtime's (<see cref="EndpointSides.MissingSide"/>) after the step's name.
    /// </summary>
    private sealed class EndpointSideChecker(IReadOnlyList<CatalogComponent> catalog, List<PackageFinding> findings)
        : IEndpointVisitor
    {
        public void CheckUri(string file, XElement at, string uri, EndpointRole side)
        {
            var separator = uri.IndexOf(':');
            if (separator > 0)
                Check(file, at, at.Name.LocalName, uri[..separator], side);
        }

        public void CheckStructured(string file, XElement endpoint, EndpointRole side)
            => Check(file, endpoint, endpoint.Parent?.Name.LocalName ?? endpoint.Name.LocalName, endpoint.Name.LocalName, side);

        private void Check(string file, XElement at, string step, string scheme, EndpointRole side)
        {
            if (scheme.Contains("{{", StringComparison.Ordinal) || scheme.Contains("${", StringComparison.Ordinal))
                return;
            var component = catalog.FirstOrDefault(c => string.Equals(c.Scheme, scheme, StringComparison.OrdinalIgnoreCase)
                || c.AlternateSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase));
            if (component?.Role is { } only && only != side)
                findings.Add(new PackageFinding(true, Position(file, at),
                    $"<{step}>: {EndpointSides.MissingSide(scheme, only, component.RoleHint)}"));
        }
    }

    private sealed class EndpointParameterChecker(
        IReadOnlyList<CatalogComponent> catalog,
        Func<string, Type?> typeResolver,
        List<PackageFinding> findings) : IEndpointVisitor
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

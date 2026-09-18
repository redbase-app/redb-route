using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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
    /// Runs the packaging checks for a project directory without producing anything — the CI
    /// gate form (Ф5.4: «без гейта проверки будут пропускаться локально на бегу»).
    /// <paramref name="typeResolver"/> — when the built assemblies are at hand (the tool's
    /// <c>--bin</c>) — turns the bean checks DEEP: a renamed type, a non-public type, a typo in
    /// a property or a <c>method=</c> becomes a build error instead of a first-message surprise.
    /// </summary>
    public static PackageResult Check(string projectDir, string name, string version,
        Func<string, Type?>? typeResolver = null,
        IReadOnlyList<IXmlElementContribution>? extensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDir);
        var findings = new List<PackageFinding>();
        var artifacts = CollectArtifacts(projectDir, findings);
        var requiredKeys = RunChecks(projectDir, artifacts, findings, typeResolver, extensions);
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
        IReadOnlyList<IXmlElementContribution>? extensions = null)
    {
        var result = Check(projectDir, name, version, typeResolver, extensions);
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
        IReadOnlyList<IXmlElementContribution>? extensions = null)
    {
        var registry = ElementRegistry.CreateDefault(extensions);
        var requiredKeys = new SortedSet<string>(StringComparer.Ordinal);
        var declaredBeans = new HashSet<string>(StringComparer.Ordinal);
        var beanTypes = new Dictionary<string, (string File, XElement Element, string TypeName)>(StringComparer.Ordinal);
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
                {
                    declaredBeans.Add(beanName);
                    if (element.Attribute("type")?.Value is { Length: > 0 } beanType)
                        beanTypes[beanName] = (file, element, beanType);
                }

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
                    if (attribute.Name.LocalName == "file")
                    {
                        var resource = Path.Combine(projectDir, ResourcesDir, attribute.Value);
                        var direct = Path.Combine(projectDir, attribute.Value);
                        if (!File.Exists(resource) && !File.Exists(direct))
                            findings.Add(new PackageFinding(true, file,
                                $"resource '{attribute.Value}' referenced by <{element.Name.LocalName}> is not under '{ResourcesDir}/'."));
                    }
                }
            }
        }

        // The registry is one per context (Ф0 §2.1), so a #name may be declared in ANY artifact
        // of the package; what remains unresolved is either external (module code) — a warning —
        // or a typo. Without the module's code we cannot tell, so the finding names both.
        foreach (var (file, name) in referencedBeans.DistinctBy(r => r.Name))
        {
            if (!declaredBeans.Contains(name))
                findings.Add(new PackageFinding(false, file,
                    $"'#{name}' is not declared by any <bean> of this package — either module code registers it at startup (fine), or it is a dangling reference."));
        }

        if (typeResolver is not null)
            RunDeepBeanChecks(beanTypes, beanMethodCalls, typeResolver, findings);

        return [.. requiredKeys];
    }

    /// <summary>
    /// The assembly-backed half of Ф5.4: bean types resolve and are public, declared properties
    /// are writable, and every <c>bean:#x?method=M</c> names an existing public method.
    /// </summary>
    private static void RunDeepBeanChecks(
        Dictionary<string, (string File, XElement Element, string TypeName)> beanTypes,
        List<(string File, string Bean, string Method)> beanMethodCalls,
        Func<string, Type?> typeResolver,
        List<PackageFinding> findings)
    {
        var resolved = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var (name, (file, element, typeName)) in beanTypes)
        {
            var type = typeResolver(typeName);
            if (type is null)
            {
                findings.Add(new PackageFinding(true, file,
                    $"bean '{name}': type '{typeName}' was not found in the built assemblies."));
                continue;
            }
            if (!type.IsPublic && !type.IsNestedPublic)
            {
                findings.Add(new PackageFinding(true, file,
                    $"bean '{name}': type '{typeName}' exists but is not public — bean: needs a public type."));
                continue;
            }
            resolved[name] = type;
            foreach (var property in element.Elements().Where(p => p.Name.LocalName == "property"))
            {
                var key = property.Attribute("key")?.Value;
                if (key is { Length: > 0 } && type.GetProperty(key) is not { CanWrite: true })
                    findings.Add(new PackageFinding(true, file,
                        $"bean '{name}': type '{type.FullName}' has no writable public property '{key}'."));
            }
        }
        foreach (var (file, bean, method) in beanMethodCalls.DistinctBy(c => (c.Bean, c.Method)))
        {
            if (!resolved.TryGetValue(bean, out var type))
                continue; // undeclared bean — already a finding, or module code owns it
            if (!type.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                    .Any(m => m.Name == method))
                findings.Add(new PackageFinding(true, file,
                    $"bean '{bean}': type '{type.FullName}' has no public method '{method}' (bean:#{bean}?method={method})."));
        }
    }

    private static void LoadDocument(string file, string path, List<(string, XDocument)> documents, List<PackageFinding> findings)
    {
        try
        {
            documents.Add((file, XDocument.Load(path, LoadOptions.SetLineInfo)));
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

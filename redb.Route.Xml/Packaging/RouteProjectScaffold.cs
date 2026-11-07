using System.Reflection;

namespace redb.Route.Xml.Packaging;

/// <summary>
/// Scaffolds a route project (эскиз 11 §1.1: an ordinary <c>.csproj</c> — VS Code, Visual
/// Studio and Rider all open it — carrying the XML artifacts, the context document and the
/// layered configuration in OUR convention): the L4 config holds module identity ONLY, secrets
/// arrive through the merged context configuration at deploy time (the identity/context.json
/// conveyor, never the everything-in-the-package legacy style), the generated XSD wires editor
/// autocompletion with no extension of ours, and <c>redb-route-xml pack</c> turns the project
/// into the Ф5.1 package layout with the Ф5.4 checks as the gate.
/// </summary>
public static class RouteProjectScaffold
{
    /// <summary>Creates a new route project in <paramref name="parentDir"/>/<paramref name="name"/>.</summary>
    /// <param name="parentDir">Where to create the project directory.</param>
    /// <param name="name">Project and package name.</param>
    /// <param name="contextName">The route context name (defaults to the lowercased name).</param>
    /// <param name="runtimeProjectPath">
    /// A local <c>redb.Route.Xml.csproj</c> path to reference instead of the NuGet package —
    /// for working against a checkout before the packages are published.
    /// </param>
    /// <returns>The project directory.</returns>
    public static string Create(string parentDir, string name, string? contextName = null,
        string? runtimeProjectPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        contextName ??= name.ToLowerInvariant();

        var dir = Path.Combine(parentDir, name);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new InvalidOperationException($"'{dir}' already exists and is not empty.");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, RoutePackage.RoutesDir));
        Directory.CreateDirectory(Path.Combine(dir, RoutePackage.ResourcesDir));
        Directory.CreateDirectory(Path.Combine(dir, RoutePackage.ConfigDir));
        Directory.CreateDirectory(Path.Combine(dir, "schema"));
        Directory.CreateDirectory(Path.Combine(dir, ".vscode"));

        var runtimeVersion = typeof(XmlRouteLoader).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "4.0.0";

        var runtimeReference = runtimeProjectPath is null
            ? $"""<PackageReference Include="redb.Route.Xml" Version="{runtimeVersion}" />"""
            : $"""<ProjectReference Include="{runtimeProjectPath}" />""";
        File.WriteAllText(Path.Combine(dir, $"{name}.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <!-- The package identity `redb-route-xml pack` uses. -->
                <RoutePackageName>{name}</RoutePackageName>
                <RouteContextName>{contextName}</RouteContextName>
                <!-- A route project is a library: NuGet assemblies do not reach bin/ unless asked, and the
                     build-time pack gate reads connectors and markup contributions from TargetDir. -->
                <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
              </PropertyGroup>
              <ItemGroup>
                {runtimeReference}
              </ItemGroup>
              <ItemGroup>
                <None Include="routes/**/*.route.xml" />
                <None Include="context.xml" />
                <None Include="resources/**/*" />
                <None Include="config/**/*.json" />
              </ItemGroup>
              <!-- Opt-in build-time packaging: dotnet build -p:PackRouteOnBuild=true -p:Version=1.0.0
                   runs the Ф5.4 checks against the fresh build output and produces pkg/{name}-VERSION.tpkg. -->
              <Target Name="RoutePackage" AfterTargets="Build" Condition="'$(PackRouteOnBuild)' == 'true'">
                <Exec Command="redb-route-xml pack &quot;$(MSBuildProjectDirectory)&quot; --version $(Version) --bin &quot;$(TargetDir).&quot;" />
              </Target>
            </Project>
            """);

        File.WriteAllText(Path.Combine(dir, RoutePackage.ContextFile), """
            <?xml version="1.0" encoding="utf-8"?>
            <!-- Context-level document: components, context-wide beans, the one-time init
                 pipeline. Loaded before the route artifacts, so #references resolve. -->
            <context xmlns="urn:redb:route:1.0">

              <!-- Explicit component list (optional; referenced-assembly scan is the default):
              <components>
                <component type="redb.Route.Sql.SqlComponent, redb.Route.Sql"/>
              </components>
              -->

              <!-- Context-wide factories and objects; values come from configuration:
              <bean name="main-db" type="redb.Route.Sql.Connection.SqlConnectionFactory, redb.Route.Sql">
                <constructorArg>
                  <bean type="redb.Route.Sql.Connection.SqlConnectionOptions, redb.Route.Sql">
                    <property key="ConnectionString" value="{{db.main.connection}}"/>
                  </bean>
                </constructorArg>
              </bean>
              -->

              <!-- One-time init (DDL, seeding through bean:), before the routes start:
              <onInit>
                <to uri="sql:CREATE TABLE IF NOT EXISTS demo_log (id BIGINT)?dataSource=#main-db"/>
              </onInit>
              -->

            </context>
            """);

        File.WriteAllText(Path.Combine(dir, RoutePackage.RoutesDir, "main.route.xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <routes xmlns="urn:redb:route:1.0">

              <route id="{contextName}-hello" description="Replace me with a real route">
                <from uri="timer://hello?period=5000"/>
                <setBody value="hello from {name}"/>
                <to uri="log://{contextName}"/>
              </route>

            </routes>
            """);

        // L4 — module identity ONLY (эскиз 11 §1.4). Everything else arrives through the merged
        // context configuration at deploy time; secrets never ride inside the package.
        File.WriteAllText(Path.Combine(dir, RoutePackage.ConfigDir, $"{name}.config.json"), $$"""
            {
              "//": "L4 (in-package): module identity ONLY. Settings and secrets come from the merged context configuration (see config/context.sample.json).",
              "ContextName": "{{contextName}}",
              "AutoStart": true
            }
            """);

        File.WriteAllText(Path.Combine(dir, RoutePackage.ConfigDir, "context.sample.json"), $$$"""
            {
              "//": [
                "L3 deploy sample — copy as context.json NEXT TO the package on the host; never commit real secrets here.",
                "Merged configuration, later layers win:",
                "  L1 host appsettings: Contexts default section",
                "  L2 host appsettings: this context's section",
                "  L3 this file:        Contexts.default + Contexts.{{{contextName}}}",
                "  L4 in-package:       ContextName + AutoStart only (module identity)",
                "  L5 Override:         environment variables (secrets live here)",
                "Every {{key}} without a {{key:default}} in the XML must receive a value from",
                "one of these layers; `redb-route-xml pack` lists them in manifest.json",
                "under RequiredConfigKeys, and the module fails fast at init when one is missing."
              ],
              "Contexts": {
                "default": {},
                "{{{contextName}}}": {
                }
              }
            }
            """);

        File.WriteAllText(Path.Combine(dir, "schema", "redb-route-1.0.xsd"),
            XmlRouteSchema.Generate(ElementRegistry.CreateDefault()).ToString());

        File.WriteAllText(Path.Combine(dir, ".vscode", "settings.json"), """
            {
              "xml.fileAssociations": [
                { "pattern": "**/*.route.xml", "systemId": "./schema/redb-route-1.0.xsd" }
              ]
            }
            """);

        File.WriteAllText(Path.Combine(dir, "README.md"), $"""
            # {name}

            A redb.Route XML route project. The layout is the packaging convention:

            | Path | What |
            |---|---|
            | `routes/*.route.xml` | the route artifacts (explicit manifest order) |
            | `context.xml` | components, context beans, the one-time init pipeline |
            | `resources/` | XSLT, schemas — resolved from the package root |
            | `config/{name}.config.json` | L4: module identity ONLY |
            | `config/context.sample.json` | L3 sample for deployment |
            | `schema/` | generated XSD — VS Code autocompletion via the RedHat XML extension |

            Build the package (checks are the gate — a broken reference is a build error):

                redb-route-xml pack . --version 1.0.0

            Regenerate C# or a diagram from a route:

                redb-route-xml csharp routes/main.route.xml --namespace {name}.Routes
                redb-route-xml mermaid routes/main.route.xml

            Configuration is layered; secrets never ride inside the package. See
            `config/context.sample.json` for the conveyor and `manifest.json` of a built
            package for the required keys.
            """);

        return dir;
    }
}

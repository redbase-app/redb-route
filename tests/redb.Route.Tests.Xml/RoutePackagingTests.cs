using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>The bean target of the deep packaging checks.</summary>
public class DeepBeanTarget
{
    public string? Endpoint { get; set; }
    public string Handle(Abstractions.IExchange exchange) => "ok";
}

/// <summary>
/// Route-XML Ф5, the Tsak-independent half: a scaffolded project builds into the Ф5.1 package
/// layout (manifest + L4 identity config + artifacts + resources) with the Ф5.4 checks as the
/// gate — a broken reference is a build error, never a deployment surprise — and every
/// <c>{{key}}</c> without a default lands in the required-keys manifest.
/// </summary>
public class RoutePackagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-xml-pkg-" + Guid.NewGuid().ToString("N"));

    public RoutePackagingTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string NewProject(string name = "Orders", string? context = null)
        => RouteProjectScaffold.Create(_root, name, context);

    // ── the scaffold ─────────────────────────────────────────────────────────

    [Fact]
    public void Scaffold_CreatesTheWholeConvention()
    {
        var dir = NewProject("Orders", "orders");

        File.Exists(Path.Combine(dir, "Orders.csproj")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "context.xml")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "routes", "main.route.xml")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "config", "Orders.config.json")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "config", "context.sample.json")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "schema", "redb-route-1.0.xsd")).Should().BeTrue();
        File.Exists(Path.Combine(dir, ".vscode", "settings.json")).Should().BeTrue();

        // A route project is a library: without this NuGet assemblies never reach bin/, and the
        // build-time pack gate (--bin TargetDir) sees neither connectors nor markup contributions.
        File.ReadAllText(Path.Combine(dir, "Orders.csproj"))
            .Should().Contain("<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>",
                "the build-time gate reads the runtime from TargetDir");

        // The L4 config is module identity ONLY — no settings, no secrets (эскиз 11 §1.4).
        using var l4 = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "config", "Orders.config.json")));
        l4.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("//", "ContextName", "AutoStart");
        l4.RootElement.GetProperty("ContextName").GetString().Should().Be("orders");
    }

    [Fact]
    public void Scaffold_RefusesANonEmptyDirectory()
    {
        var dir = NewProject("Busy");
        var act = () => RouteProjectScaffold.Create(_root, "Busy");
        act.Should().Throw<InvalidOperationException>().WithMessage("*not empty*");
    }

    // ── check and build ──────────────────────────────────────────────────────

    [Fact]
    public void FreshScaffold_PassesTheChecks_AndBuildsThePackage()
    {
        var dir = NewProject("Orders", "orders");
        var outDir = Path.Combine(_root, "out");

        var result = RoutePackage.Build(dir, "orders", "1.2.3", outDir);

        result.Errors.Should().BeEmpty();
        result.Manifest.Artifacts.Should().Equal("routes/main.route.xml");
        result.Manifest.Context.Should().Be("orders");

        var stage = Path.Combine(outDir, "orders-1.2.3");
        File.Exists(Path.Combine(stage, "manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(stage, "orders.config.json")).Should().BeTrue();
        File.Exists(Path.Combine(stage, "routes", "main.route.xml")).Should().BeTrue();
        File.Exists(Path.Combine(stage, "context.xml")).Should().BeTrue();

        using var zip = ZipFile.OpenRead(Path.Combine(outDir, "orders-1.2.3.tpkg"));
        zip.Entries.Select(e => e.FullName.Replace('\\', '/'))
            .Should().Contain(["manifest.json", "orders.config.json", "routes/main.route.xml"]);
    }

    [Fact]
    public void RequiredConfigKeys_AreEveryPlaceholderWithoutADefault()
    {
        var dir = NewProject("Keys", "keys");
        File.WriteAllText(Path.Combine(dir, "routes", "keys.route.xml"), """
            <routes xmlns="urn:redb:route:1.0">
              <route id="keys-in">
                <from uri="http:{{api.listen:0.0.0.0:5090}}/in?inOut=true"/>
                <setHeader name="tenant" value="{{tenant.name}}"/>
                <to uri="smtp://{{mail.from}}?to={{mail.to}}&amp;connectionFactory=#corp-smtp"/>
              </route>
            </routes>
            """);

        var result = RoutePackage.Check(dir, "keys", "1.0.0");

        result.Errors.Should().BeEmpty();
        result.Manifest.RequiredConfigKeys.Should().Equal("mail.from", "mail.to", "tenant.name");
        result.Warnings.Should().Contain(w => w.Message.Contains("#corp-smtp"),
            "an undeclared #reference is named: module code may register it, or it is dangling");
    }

    [Fact]
    public void RegistryReferences_AreValuesStartingWithHash_NotSqlParameters()
    {
        // The sql connector writes parameters Camel-style, :#name (wave 17.6). A '#' inside the
        // SQL text is a parameter, not a registry reference — only a value that STARTS with '#'
        // (an option value, the path after the scheme, a whole attribute) references the registry.
        var dir = NewProject("SqlParams", "sqlparams");
        File.WriteAllText(Path.Combine(dir, "routes", "sqlparams.route.xml"), """
            <routes xmlns="urn:redb:route:1.0">
              <route id="audit">
                <from uri="timer://audit?period=1000"/>
                <to uri="sql:INSERT INTO auth_log(login, at) VALUES (:#login, :#at)?dataSource=#main-db&amp;param.login=${header.login}&amp;param.at=x"/>
                <to uri="bean:#auditor?method=Handle"/>
              </route>
            </routes>
            """);

        var result = RoutePackage.Check(dir, "sqlparams", "1.0.0");

        result.Errors.Should().BeEmpty();
        result.Warnings.Should().NotContain(w => w.Message.Contains("'#login'") || w.Message.Contains("'#at'"),
            ":#login and :#at are sql parameters, never registry references");
        result.Warnings.Should().Contain(w => w.Message.Contains("'#main-db'"),
            "an option value starting with '#' is still a reference");
        result.Warnings.Should().Contain(w => w.Message.Contains("'#auditor'"),
            "the path right after bean: is still a reference");
    }

    // ── the Ф5.4 gate: negatives ─────────────────────────────────────────────

    [Fact]
    public void BrokenMarkup_IsABuildError_WithThePosition()
    {
        var dir = NewProject("Broken");
        File.WriteAllText(Path.Combine(dir, "routes", "broken.route.xml"), """
            <routes xmlns="urn:redb:route:1.0">
              <route id="bad">
                <from uri="direct://in"/>
                <nosuch/>
              </route>
            </routes>
            """);

        var result = RoutePackage.Check(dir, "broken", "1.0.0");

        result.Errors.Should().Contain(e => e.File.StartsWith("routes/broken.route.xml(") && e.Message.Contains("nosuch"));
        RoutePackage.Build(dir, "broken", "1.0.0", Path.Combine(_root, "out2"))
            .Errors.Should().NotBeEmpty("errors refuse the build");
        File.Exists(Path.Combine(_root, "out2", "broken-1.0.0.tpkg")).Should().BeFalse();
    }

    [Fact]
    public void MissingResource_IsABuildError()
    {
        var dir = NewProject("Res");
        File.WriteAllText(Path.Combine(dir, "routes", "res.route.xml"), """
            <routes xmlns="urn:redb:route:1.0">
              <route id="res-in">
                <from uri="direct://in"/>
                <validateJsonSchema file="schemas/missing.json"/>
              </route>
            </routes>
            """);

        RoutePackage.Check(dir, "res", "1.0.0").Errors
            .Should().Contain(e => e.Message.Contains("schemas/missing.json") && e.Message.Contains("resources"));
    }

    [Fact]
    public void LiteralSecretInAUri_IsAWarning_NotSilence()
    {
        var dir = NewProject("Sec");
        File.WriteAllText(Path.Combine(dir, "routes", "sec.route.xml"), """
            <routes xmlns="urn:redb:route:1.0">
              <route id="sec-in">
                <from uri="direct://in"/>
                <to uri="smtp://mail?password=hunter2"/>
              </route>
            </routes>
            """);

        var result = RoutePackage.Check(dir, "sec", "1.0.0");

        result.Warnings.Should().Contain(w => w.Message.Contains("literal secret"));
        result.Errors.Should().BeEmpty("the heuristic warns, it does not block");
    }

    // ── deep bean checks (a type resolver = the built assemblies at hand) ───

    private static Type? TestResolver(string typeName)
        => Type.GetType(typeName) ?? typeof(RoutePackagingTests).Assembly.GetType(typeName.Split(',')[0].Trim());

    private string ProjectWithBean(string beanXml, string beanUse = "")
    {
        var dir = NewProject("Deep" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(Path.Combine(dir, "routes", "deep.route.xml"), $"""
            <routes xmlns="urn:redb:route:1.0">
              {beanXml}
              <route id="deep-in">
                <from uri="direct://in"/>
                {(beanUse.Length > 0 ? beanUse : "<removeBody/>")}
              </route>
            </routes>
            """);
        return dir;
    }

    [Fact]
    public void DeepChecks_CatchARenamedType_ANonPublicType_AndABadProperty()
    {
        var missing = ProjectWithBean("""<bean name="b" type="No.Such.Type, Nowhere"/>""");
        RoutePackage.Check(missing, "p", "1.0.0", TestResolver).Errors
            .Should().Contain(e => e.Message.Contains("was not found in the built assemblies"));

        var badProperty = ProjectWithBean($"""
            <bean name="b" type="{typeof(DeepBeanTarget).FullName}, x">
              <property key="NoSuchProperty" value="1"/>
            </bean>
            """);
        RoutePackage.Check(badProperty, "p", "1.0.0", TestResolver).Errors
            .Should().Contain(e => e.Message.Contains("no writable public property 'NoSuchProperty'"));
    }

    [Fact]
    public void DeepChecks_CatchAMissingBeanMethod_AndPassAGoodOne()
    {
        var bad = ProjectWithBean(
            $"""<bean name="b" type="{typeof(DeepBeanTarget).FullName}, x"/>""",
            """<to uri="bean:#b?method=NoSuchMethod"/>""");
        RoutePackage.Check(bad, "p", "1.0.0", TestResolver).Errors
            .Should().Contain(e => e.Message.Contains("no public method 'NoSuchMethod'"));

        var good = ProjectWithBean(
            $"""<bean name="b" type="{typeof(DeepBeanTarget).FullName}, x"/>""",
            """<to uri="bean:#b?method=Handle"/>""");
        RoutePackage.Check(good, "p", "1.0.0", TestResolver).Errors.Should().BeEmpty();
    }

    [Fact]
    public void WithoutAResolver_TheDeepChecksStayQuiet()
    {
        var dir = ProjectWithBean("""<bean name="b" type="No.Such.Type, Nowhere"/>""");
        RoutePackage.Check(dir, "p", "1.0.0").Errors
            .Should().NotContain(e => e.Message.Contains("built assemblies"),
                "without the assemblies at hand the check cannot claim the type is missing");
    }

    [Fact]
    public void PlaceholderSecret_DoesNotTriggerTheWarning()
    {
        var dir = NewProject("SecOk");
        File.WriteAllText(Path.Combine(dir, "routes", "sec.route.xml"), """
            <routes xmlns="urn:redb:route:1.0">
              <route id="sec-in">
                <from uri="direct://in"/>
                <to uri="smtp://mail?password={{mail.password}}"/>
              </route>
            </routes>
            """);

        RoutePackage.Check(dir, "secok", "1.0.0").Warnings
            .Should().NotContain(w => w.Message.Contains("literal secret"));
    }
}

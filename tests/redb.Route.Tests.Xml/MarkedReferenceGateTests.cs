using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Xml;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>
/// Row 7 part 2: an option marked <c>[RegistryReference]</c> names a registry object bare as well as with <c>#</c>, in
/// the URI form and in the structured form, so a name no <c>&lt;bean&gt;</c> declares is the usual undeclared-reference
/// warning. The core's options are known without <c>--bin</c>; a connector's come with its catalog. A value built from
/// configuration or an expression is left alone, and a bare value of an unmarked option is still text.
/// </summary>
public sealed class MarkedReferenceGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-xml-marked-" + Guid.NewGuid().ToString("N"));

    public MarkedReferenceGateTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly IReadOnlyList<CatalogComponent> Catalog = ComponentCatalog.Build([new ProbeRefComponent()]);

    private static string T<TType>() => $"{typeof(TType).FullName}, {typeof(TType).Assembly.GetName().Name}";

    private PackageResult Check(string steps, string beans = "", IReadOnlyList<CatalogComponent>? catalog = null)
    {
        var dir = RouteProjectScaffold.Create(_root, "Mrk" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), $"""
            <routes xmlns="urn:redb:route:1.0">
              {beans}
              <route id="send">
                <from uri="direct://in"/>
                {steps}
              </route>
            </routes>
            """);
        return RoutePackage.Check(dir, "p", "1.0.0", catalog: catalog);
    }

    private static IEnumerable<string> Undeclared(PackageResult result)
        => result.Warnings.Select(w => w.Message).Where(m => m.Contains("is not declared by any <bean>", StringComparison.Ordinal));

    [Fact]
    public void ABareNameInACoreMarkedOption_IsAReference_WithoutBin()
    {
        Undeclared(Check("""<to uri="vm://out?headerFilterStrategy=filter"/>"""))
            .Should().ContainSingle().Which.Should().StartWith("'#filter' is not declared by any <bean> of this package");
    }

    [Fact]
    public void ABareNameDeclaredByABean_Passes()
    {
        Undeclared(Check("""<to uri="vm://out?headerFilterStrategy=filter"/>""",
                $"""<bean name="filter" type="{T<DefaultHeaderFilterStrategy>()}"/>"""))
            .Should().BeEmpty();
    }

    [Fact]
    public void AHashName_IsReadAsBefore_Once()
    {
        Undeclared(Check("""<to uri="vm://out?headerFilterStrategy=#filter"/>""")).Should().ContainSingle();
    }

    [Theory]
    [InlineData("""<to uri="vm://out?headerFilterStrategy={{hfs.name}}"/>""")]
    [InlineData("""<to uri="vm://out?headerFilterStrategy=${header.hfs}"/>""")]
    [InlineData("""<to uri="vm://out?size=main"/>""")]
    public void PlaceholdersAndUnmarkedOptions_AreLeftAlone(string step)
    {
        Undeclared(Check(step)).Should().BeEmpty();
    }

    [Fact]
    public void AConnectorsMarkedOption_IsAReference_WithItsCatalog_InTheUriForm()
    {
        Undeclared(Check("""<to uri="proberef://q?connectionFactory=main&amp;queue=x"/>""", catalog: Catalog))
            .Should().ContainSingle().Which.Should().StartWith("'#main' is not declared");
    }

    [Fact]
    public void AConnectorsMarkedOption_IsAReference_WithItsCatalog_InTheStructuredForm()
    {
        Undeclared(Check("""<to><proberef path="q" connectionFactory="main" anything="other"/></to>""", catalog: Catalog))
            .Should().BeEquivalentTo(
                "'#main' is not declared by any <bean> of this package — either module code registers it at startup (fine), or it is a dangling reference.",
                "'#other' is not declared by any <bean> of this package — either module code registers it at startup (fine), or it is a dangling reference.");
    }

    [Fact]
    public void WithoutItsCatalog_AConnectorsBareName_IsNotKnown()
    {
        Undeclared(Check("""<to uri="proberef://q?connectionFactory=main"/>""")).Should().BeEmpty();
    }
}

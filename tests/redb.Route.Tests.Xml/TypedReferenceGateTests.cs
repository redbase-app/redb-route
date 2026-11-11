using AwesomeAssertions;
using redb.Route.Xml;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>
/// Row 7 part 3: with the built assemblies at hand, a name in an option marked <c>[RegistryReference(typeof(T))]</c>,
/// bare or with <c>#</c>, URI form or structured form, is held against <c>T</c> per connector: a bean the package
/// declares with a type that is not a <c>T</c> is an error, worded as for <c>headerFilterStrategy</c>. An option marked
/// without a type is never checked; a bean whose type does not load, or that the package does not declare, gives no
/// type error; values from configuration or expressions are left alone.
/// </summary>
public sealed class TypedReferenceGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-xml-typed-" + Guid.NewGuid().ToString("N"));

    public TypedReferenceGateTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly IReadOnlyList<CatalogComponent> Catalog = ComponentCatalog.Build([new ProbeRefComponent()]);

    private static Type? Resolve(string typeName) => Type.GetType(typeName);

    private static string T<TType>() => $"{typeof(TType).FullName}, {typeof(TType).Assembly.GetName().Name}";

    private PackageResult Check(string step, string beans, IReadOnlyList<CatalogComponent>? catalog = null)
    {
        var dir = RouteProjectScaffold.Create(_root, "Typ" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), $"""
            <routes xmlns="urn:redb:route:1.0">
              {beans}
              <route id="send">
                <from uri="direct://in"/>
                {step}
              </route>
            </routes>
            """);
        return RoutePackage.Check(dir, "p", "1.0.0", Resolve, catalog: catalog ?? Catalog);
    }

    private static IEnumerable<string> TypeErrors(PackageResult result)
        => result.Errors.Select(e => e.Message).Where(m => m.Contains("is registered as", StringComparison.Ordinal));

    private static string WrongBean => $"""<bean name="main" type="{T<DeepBeanTarget>()}"/>""";

    [Theory]
    [InlineData("""<to uri="proberef://q?connectionFactory=main"/>""", "<to uri=…connectionFactory=\"#main\">")]
    [InlineData("""<to uri="proberef://q?connectionFactory=#main"/>""", "<to uri=…connectionFactory=\"#main\">")]
    [InlineData("""<to><proberef path="q" connectionFactory="main"/></to>""", "<proberef connectionFactory=\"#main\">")]
    public void ABeanOfTheWrongType_IsAnError_InEveryForm(string step, string at)
    {
        TypeErrors(Check(step, WrongBean)).Should().ContainSingle().Which.Should().Be(
            $"{at}: 'main' is registered as {typeof(DeepBeanTarget).FullName}, which is not an {nameof(ProbeRefFactory)}.");
    }

    [Fact]
    public void ABeanOfTheExpectedType_Passes()
    {
        TypeErrors(Check("""<to uri="proberef://q?connectionFactory=main"/>""", $"""<bean name="main" type="{T<ProbeRefFactory>()}"/>"""))
            .Should().BeEmpty();
    }

    [Fact]
    public void AnUntypedMark_IsNeverTypeChecked()
    {
        TypeErrors(Check("""<to uri="proberef://q?anything=main"/>""", WrongBean)).Should().BeEmpty();
    }

    [Fact]
    public void ABeanWhoseTypeDoesNotLoad_GivesNoTypeError()
    {
        TypeErrors(Check("""<to uri="proberef://q?connectionFactory=main"/>""", """<bean name="main" type="No.Such.Type, NoSuchAssembly"/>"""))
            .Should().BeEmpty();
    }

    [Fact]
    public void AnUndeclaredBean_IsOnlyTheWarning()
    {
        var result = Check("""<to uri="proberef://q?connectionFactory=main"/>""", "");

        TypeErrors(result).Should().BeEmpty();
        result.Warnings.Should().Contain(w => w.Message.StartsWith("'#main' is not declared", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""<to uri="proberef://q?connectionFactory={{factory.name}}"/>""")]
    [InlineData("""<to uri="proberef://q?connectionFactory=${header.factory}"/>""")]
    public void AValueFromConfigurationOrAnExpression_IsLeftAlone(string step)
    {
        TypeErrors(Check(step, WrongBean)).Should().BeEmpty();
    }

    private static string WrongStrategy => $"""<bean name="x" type="{T<DeepBeanTarget>()}"/>""";

    [Theory]
    [InlineData("""<recipientList expr="vm:a?headerFilterStrategy=#x"/>""", "<recipientList expr=…headerFilterStrategy=\"#x\">")]
    [InlineData("""<routingSlip expr="vm:a?headerFilterStrategy=x"/>""", "<routingSlip expr=…headerFilterStrategy=\"#x\">")]
    public void AUriInAnyOtherAttribute_IsHeldAgainstTheType_AsBefore(string step, string at)
    {
        TypeErrors(Check(step, WrongStrategy)).Should().ContainSingle().Which.Should().Be(
            $"{at}: 'x' is registered as {typeof(DeepBeanTarget).FullName}, which is not an IHeaderFilterStrategy.");
    }

    [Fact]
    public void ABareNameInAnyOtherAttribute_IsAReference()
    {
        Check("""<recipientList expr="vm:a?headerFilterStrategy=x"/>""", "").Warnings
            .Should().ContainSingle(w => w.Message.StartsWith("'#x' is not declared", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEndpointPosition_IsReadOnce()
    {
        TypeErrors(Check("""<to uri="vm:a?headerFilterStrategy=x"/>""", WrongStrategy)).Should().ContainSingle();
    }

    [Fact]
    public void TheCheckIsPerConnector_AnOptionOfTheSameNameElsewhereIsNotHeldAgainstThisType()
    {
        // vm has no connectionFactory option; the name means nothing there and is not checked against ProbeRefFactory.
        TypeErrors(Check("""<to uri="vm://out?connectionFactory=main"/>""", WrongBean)).Should().BeEmpty();
    }

    // Row 57: ftp and sftp share RemoteFileEndpointOptions, whose connectionFactory was marked without a type, so a bean
    // of any class passed the gate. The mark now names the factories' common base.
    private static readonly IReadOnlyList<CatalogComponent> RemoteFileCatalog =
        ComponentCatalog.Build([new redb.Route.Ftp.FtpComponent(), new redb.Route.Sftp.SftpComponent()]);

    [Theory]
    [InlineData("ftp")]
    [InlineData("sftp")]
    public void ARemoteFileConnector_HoldsItsConnectionFactoryAgainstTheFactoryType(string scheme)
    {
        TypeErrors(Check($"""<to uri="{scheme}://host/out?connectionFactory=main"/>""", WrongBean, RemoteFileCatalog))
            .Should().ContainSingle().Which.Should().EndWith(
                $"'main' is registered as {typeof(DeepBeanTarget).FullName}, which is not an {nameof(redb.Route.GenericFile.RemoteFileConnectionFactory)}.");
    }

    [Theory]
    [InlineData("ftp", typeof(redb.Route.Ftp.FtpConnectionFactory))]
    [InlineData("sftp", typeof(redb.Route.Sftp.SftpConnectionFactory))]
    public void ARemoteFileConnector_AcceptsItsOwnFactory(string scheme, Type factory)
    {
        var bean = $"""<bean name="main" type="{factory.FullName}, {factory.Assembly.GetName().Name}"/>""";

        TypeErrors(Check($"""<to uri="{scheme}://host/out?connectionFactory=main"/>""", bean, RemoteFileCatalog)).Should().BeEmpty();
    }
}

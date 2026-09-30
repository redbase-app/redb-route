using FluentAssertions;
using redb.Route.RedbCore.Xml;
using redb.Route.Xml;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>
/// Repositories are found by their bare name (1782f31b): <c>&lt;bean name="dedup"&gt;</c> and
/// <c>&lt;redb&gt;&lt;idempotentRepository name="dedup"&gt;</c> both declare the key
/// <c>repository="#dedup"</c> looks up. The package gate counts both as declarations, and with
/// the built assemblies at hand holds the named object against the interface the reference needs.
/// </summary>
public sealed class RepositoryReferenceGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-xml-repo-" + Guid.NewGuid().ToString("N"));

    public RepositoryReferenceGateTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly IReadOnlyList<IXmlElementContribution> Extensions = [new RedbContextXmlContribution()];

    private static Type? Resolve(string typeName) => Type.GetType(typeName);

    private const string InMemoryIdempotent = "redb.Route.Processors.InMemoryIdempotentRepository, redb.Route";

    private string Project(string routes, string? context = null)
    {
        var dir = RouteProjectScaffold.Create(_root, "Repo" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), routes);
        if (context is not null)
            File.WriteAllText(Path.Combine(dir, RoutePackage.ContextFile), context);
        return dir;
    }

    private static string Consumer(string repositoryElement) => $"""
        <routes xmlns="urn:redb:route:1.0">
          <route id="dedup">
            <from uri="direct://in"/>
            {repositoryElement}
          </route>
        </routes>
        """;

    [Fact]
    public void RedbIdempotentRepository_DeclaresItsName_ForTheGate()
    {
        var dir = Project(
            Consumer("""<idempotentConsumer key="${header.id}" repository="#dedup"><removeBody/></idempotentConsumer>"""),
            """
            <context xmlns="urn:redb:route:1.0">
              <redb><idempotentRepository name="dedup" ttl="1.00:00:00"/></redb>
            </context>
            """);

        var result = RoutePackage.Check(dir, "p", "1.0.0", Resolve, Extensions);

        result.Warnings.Should().NotContain(w => w.Message.Contains("'#dedup' is not declared"),
            "<redb><idempotentRepository name> registers exactly that name");
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void ABeanOfTheRightType_Passes()
    {
        var dir = Project($"""
            <routes xmlns="urn:redb:route:1.0">
              <bean name="dedup" type="{InMemoryIdempotent}"/>
              <route id="dedup">
                <from uri="direct://in"/>
                <idempotentConsumer key="${"{"}header.id{"}"}" repository="#dedup"><removeBody/></idempotentConsumer>
              </route>
            </routes>
            """);

        RoutePackage.Check(dir, "p", "1.0.0", Resolve, Extensions).Errors.Should().BeEmpty();
    }

    [Fact]
    public void ABeanOfTheWrongType_IsAnError_InTheEnginesWords()
    {
        var dir = Project($"""
            <routes xmlns="urn:redb:route:1.0">
              <bean name="dedup" type="{typeof(DeepBeanTarget).FullName}, {typeof(DeepBeanTarget).Assembly.GetName().Name}"/>
              <bean name="store" type="{InMemoryIdempotent}"/>
              <route id="dedup">
                <from uri="direct://in"/>
                <idempotentConsumer key="${"{"}header.id{"}"}" repository="#dedup"><removeBody/></idempotentConsumer>
                <claimCheck operation="Push" repository="#store"/>
              </route>
            </routes>
            """);

        var errors = RoutePackage.Check(dir, "p", "1.0.0", Resolve, Extensions).Errors.ToList();

        errors.Should().Contain(e => e.File.StartsWith("routes/main.route.xml(6,")
            && e.Message.Contains($"'dedup' is registered as {typeof(DeepBeanTarget).FullName}, which is not an IIdempotentRepository"));
        errors.Should().Contain(e => e.File.StartsWith("routes/main.route.xml(7,")
            && e.Message.Contains("'store' is registered as redb.Route.Processors.InMemoryIdempotentRepository, which is not an IClaimCheckRepository"));
    }

    [Fact]
    public void WithoutTheBuiltAssemblies_TheTypeCheckStaysQuiet()
    {
        var dir = Project($"""
            <routes xmlns="urn:redb:route:1.0">
              <bean name="dedup" type="{typeof(DeepBeanTarget).FullName}, x"/>
              <route id="dedup">
                <from uri="direct://in"/>
                <idempotentConsumer key="${"{"}header.id{"}"}" repository="#dedup"><removeBody/></idempotentConsumer>
              </route>
            </routes>
            """);

        RoutePackage.Check(dir, "p", "1.0.0", extensions: Extensions).Errors
            .Should().NotContain(e => e.Message.Contains("which is not an"));
    }
}

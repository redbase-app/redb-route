using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Xml;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>
/// <c>headerFilterStrategy=#name</c> in an endpoint URI, at the package gate: a name no bean declares is the usual
/// undeclared-reference warning, and with the built assemblies at hand the named bean is held against
/// <see cref="redb.Route.Abstractions.IHeaderFilterStrategy"/>, as a typed element attribute is.
/// </summary>
public sealed class HeaderFilterStrategyReferenceGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-xml-hfs-" + Guid.NewGuid().ToString("N"));

    public HeaderFilterStrategyReferenceGateTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Type? Resolve(string typeName) => Type.GetType(typeName);

    private static string T<TType>() => $"{typeof(TType).FullName}, {typeof(TType).Assembly.GetName().Name}";

    private string Project(string beans)
    {
        var dir = RouteProjectScaffold.Create(_root, "Hfs" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), $"""
            <routes xmlns="urn:redb:route:1.0">
              {beans}
              <route id="send">
                <from uri="direct://in"/>
                <to uri="vm://out?headerFilterStrategy=#filter"/>
              </route>
            </routes>
            """);
        return dir;
    }

    [Fact]
    public void AMissingStrategy_IsTheUndeclaredReferenceWarning()
    {
        var result = RoutePackage.Check(Project(""), "p", "1.0.0", Resolve);

        result.Warnings.Should().Contain(w => w.Message.Contains("'#filter' is not declared"));
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void AStrategyBean_Passes()
    {
        var dir = Project($"""<bean name="filter" type="{T<DefaultHeaderFilterStrategy>()}"/>""");

        RoutePackage.Check(dir, "p", "1.0.0", Resolve).Errors.Should().BeEmpty();
    }

    [Fact]
    public void ABeanOfTheWrongType_IsAnError_InTheEnginesWords()
    {
        var dir = Project($"""<bean name="filter" type="{T<DeepBeanTarget>()}"/>""");

        RoutePackage.Check(dir, "p", "1.0.0", Resolve).Errors.Should().Contain(e =>
            e.File.StartsWith("routes/main.route.xml(5,")
            && e.Message.Contains($"'filter' is registered as {typeof(DeepBeanTarget).FullName}, which is not an IHeaderFilterStrategy"));
    }
}

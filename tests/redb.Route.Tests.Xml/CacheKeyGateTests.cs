using AwesomeAssertions;
using redb.Route.Cache;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>
/// The gate reads <c>&lt;cache key&gt;</c> by the cache key rule (AttributeSpec.ConstantKey): a constant with a dash,
/// a slash or a colon passes as the key it is, an expression over a root is compiled, and broken arithmetic over a root
/// is the engine's error.
/// </summary>
public sealed class CacheKeyGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-xml-cachekey-" + Guid.NewGuid().ToString("N"));

    public CacheKeyGateTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private PackageResult Check(string key)
    {
        var dir = RouteProjectScaffold.Create(_root, "Ck" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), $"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="c">
                <from uri="direct://in"/>
                <cache key="{key}"><to uri="log:x"/></cache>
              </route>
            </routes>
            """);
        return RoutePackage.Check(dir, "p", "1.0.0", extensions: [new CacheXmlContribution()]);
    }

    [Theory]
    [InlineData("orders-all")]
    [InlineData("reports/daily")]
    [InlineData("b:c")]
    [InlineData("header.customerId")]
    [InlineData("${header.k}-x")]
    public void AKeyTheEngineReads_Passes(string key)
    {
        var result = Check(key);

        result.Errors.Should().BeEmpty();
        result.Warnings.Should().NotContain(w => w.Message.Contains("key="));
    }

    [Fact]
    public void BrokenArithmeticOverARoot_IsTheEnginesError()
    {
        Check("header.a+b").Errors.Should().Contain(e => e.Message.StartsWith("key= ", StringComparison.Ordinal)
                                                         && e.Message.Contains("'b' at position 10 is not a value"));
    }
}

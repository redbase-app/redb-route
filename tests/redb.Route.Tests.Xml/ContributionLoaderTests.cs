using System.Reflection;
using System.Runtime.Loader;
using AwesomeAssertions;
using redb.Route.Http.Rest;
using redb.Route.Xml;
using redb.Route.Xml.CodeGen;

namespace redb.Route.Tests.Xml;

/// <summary>
/// <c>redb-route-xml check --bin</c> reads the connector contributions from a build output that also holds a copy of
/// redb.Route.Xml.dll. On macOS the directory listing put that copy before the connectors, the connectors bound to it,
/// their contributions were a different <see cref="IXmlElementContribution"/> type and every connector element
/// (rest, redbQuery, redbSave) was reported as an invalid child of routes.
/// </summary>
public sealed class ContributionLoaderTests : IDisposable
{
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "redb-contrib-" + Guid.NewGuid().ToString("N"));

    public ContributionLoaderTests() => Directory.CreateDirectory(_bin);

    public void Dispose()
    {
        try { Directory.Delete(_bin, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The tool runs on redb.Route and redb.Route.Xml; the connectors are only in the directory.</summary>
    private static Assembly? ToolAssemblies(string? simpleName)
        => simpleName is "redb.Route" or "redb.Route.Xml"
            ? AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == simpleName)
            : null;

    /// <summary>
    /// The order is passed explicitly: a directory listing is alphabetical on NTFS and not on ext4 or APFS, so a
    /// listing would test one order on one machine and the other one somewhere else.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheXmlCopyInTheDirectory_NeverHidesTheConnectorContributions(bool xmlCopyFirst)
    {
        var xml = typeof(IXmlElementContribution).Assembly;
        var xmlCopy = Path.Combine(_bin, "redb.Route.Xml.dll");
        var connector = Path.Combine(_bin, "redb.Route.Zz.dll");
        File.Copy(xml.Location, xmlCopy);
        File.Copy(typeof(RestXmlContribution).Assembly.Location, connector);
        string[] order = xmlCopyFirst ? [xmlCopy, connector] : [connector, xmlCopy];

        var contributions = ContributionLoader.Load(_bin, order, ToolAssemblies);

        var rest = contributions.Should()
            .ContainSingle(c => c.GetType().FullName == typeof(RestXmlContribution).FullName).Subject;
        AssemblyLoadContext.GetLoadContext(rest.GetType().Assembly).Should()
            .NotBe(AssemblyLoadContext.Default, "the connector comes from the directory, not from the test host");
        contributions.Should().NotContain(c => c.GetType().Assembly == xml,
            "the tool's own elements are built in and must not be registered a second time");
    }
}

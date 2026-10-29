using AwesomeAssertions;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>
/// The type lookup of <c>redb-route-xml check|pack --bin</c>: the build output first, then the shared framework, so markup
/// naming a framework type the project's bin does not carry (<c>onException exceptions="System.Net.Http.HttpRequestException"</c>)
/// passes as it runs on the worker.
/// </summary>
public class PackageTypeResolverTests
{
    // A real build output: this test project's own bin.
    private static readonly Func<string, Type?> Resolve = PackageTypeResolver.Create(AppContext.BaseDirectory);

    [Fact]
    public void Framework_type_outside_bin_is_found_by_its_full_name()
    {
        Resolve("System.Net.Http.HttpRequestException").Should().Be(typeof(System.Net.Http.HttpRequestException));
    }

    [Fact]
    public void Framework_type_that_moved_is_found_through_the_assembly_its_name_suggests()
    {
        Resolve("System.Net.Sockets.SocketException").Should().Be(typeof(System.Net.Sockets.SocketException));
    }

    [Fact]
    public void Framework_type_with_its_assembly_name_is_found()
    {
        Resolve("System.Net.Http.HttpRequestException, System.Net.Http")
            .Should().Be(typeof(System.Net.Http.HttpRequestException));
    }

    [Fact]
    public void Type_of_the_build_output_comes_from_the_bin()
    {
        var type = Resolve("redb.Route.Tests.Xml.DeepBeanTarget");

        type.Should().NotBeNull();
        type!.FullName.Should().Be("redb.Route.Tests.Xml.DeepBeanTarget");
        type.Assembly.GetName().Name.Should().Be("redb.Route.Tests.Xml");
    }

    [Fact]
    public void Unknown_type_is_not_found()
    {
        Resolve("System.Net.Http.NoSuchException").Should().BeNull();
        Resolve("Nowhere.At.All").Should().BeNull();
    }
}

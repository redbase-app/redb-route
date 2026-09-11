using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Validation;

/// <summary>
/// Route-XML Ф1.4: resource resolution for file-reading endpoint components. The default chain is
/// absolute path → <c>ResourceRoot</c> → <c>AppContext.BaseDirectory</c> → working directory
/// (last, preserving the pre-resolver behaviour), and a miss names every probed location.
/// </summary>
public class RouteResourceResolverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("route-res-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AbsolutePath_IsReturnedAsIs_WhenTheFileExists()
    {
        var file = Path.Combine(_root, "abs.json");
        File.WriteAllText(file, "{}");

        new RouteResourceResolver().Resolve(file).Should().Be(file);
    }

    [Fact]
    public void AbsolutePath_Missing_ResolvesToNull_AndSearchNamesOnlyThatPath()
    {
        var file = Path.Combine(_root, "missing.json");
        var resolver = new RouteResourceResolver();

        resolver.Resolve(file).Should().BeNull();
        resolver.DescribeSearch(file).Should().Be(file);
    }

    [Fact]
    public void Relative_ResourceRoot_WinsOverBaseDirectory()
    {
        var name = $"probe-{Guid.NewGuid():N}.json";
        var inRoot = Path.Combine(_root, name);
        var inBase = Path.Combine(AppContext.BaseDirectory, name);
        File.WriteAllText(inRoot, "{}");
        File.WriteAllText(inBase, "{}");
        try
        {
            var resolver = new RouteResourceResolver { ResourceRoot = _root };
            resolver.Resolve(name).Should().Be(inRoot);
        }
        finally
        {
            File.Delete(inBase);
        }
    }

    [Fact]
    public void Relative_FallsBackToBaseDirectory_WhenNotUnderResourceRoot()
    {
        var name = $"probe-{Guid.NewGuid():N}.json";
        var inBase = Path.Combine(AppContext.BaseDirectory, name);
        File.WriteAllText(inBase, "{}");
        try
        {
            var resolver = new RouteResourceResolver { ResourceRoot = _root };
            resolver.Resolve(name).Should().Be(Path.GetFullPath(inBase));
        }
        finally
        {
            File.Delete(inBase);
        }
    }

    [Fact]
    public void NotFound_DescribeSearch_ListsEveryProbedLocation()
    {
        var name = $"no-such-{Guid.NewGuid():N}.json";
        var resolver = new RouteResourceResolver { ResourceRoot = _root };

        resolver.Resolve(name).Should().BeNull();
        var search = resolver.DescribeSearch(name);
        search.Should().Contain(Path.GetFullPath(Path.Combine(_root, name)));
        search.Should().Contain(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, name)));
        search.Should().Contain(Path.GetFullPath(name));
    }

    [Fact]
    public async Task ValidatorEndpoint_FindsSchema_ThroughTheRegisteredResolver()
    {
        File.WriteAllText(Path.Combine(_root, "order.schema.json"), """{"type":"object"}""");
        await using var context = new RouteContext();
        context.AddComponent(new redb.Route.Validation.ValidatorComponent());
        context.AddService(typeof(IRouteResourceResolver), new RouteResourceResolver { ResourceRoot = _root });

        var endpoint = context.GetEndpoint("validator:order.schema.json");

        endpoint.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidatorEndpoint_MissingSchema_ErrorNamesEverySearchedPlace()
    {
        await using var context = new RouteContext();
        context.AddComponent(new redb.Route.Validation.ValidatorComponent());
        context.AddService(typeof(IRouteResourceResolver), new RouteResourceResolver { ResourceRoot = _root });

        var act = () => context.GetEndpoint($"validator:absent-{Guid.NewGuid():N}.json");

        act.Should().Throw<FileNotFoundException>()
            .WithMessage("*Schema file not found*Searched:*");
    }
}

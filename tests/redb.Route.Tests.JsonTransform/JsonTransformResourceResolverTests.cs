using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.JsonTransform;
using redb.Route.TestKit;

namespace redb.Route.Tests.JsonTransform;

/// <summary>
/// A JSONata specification named by a relative path follows the same lookup as every other file
/// reference: the context's <see cref="IRouteResourceResolver"/> first, then the package's base
/// directory. Same report as the templates one (Route-XML agent, 2026-09-24): inside a
/// <c>.tpkg</c> the specification lives in <c>resources/</c>, which only the resolver knows.
/// </summary>
public class JsonTransformResourceResolverTests
{
    private sealed class RootResolver(string root) : IRouteResourceResolver
    {
        public string? Resolve(string reference)
        {
            if (Path.IsPathRooted(reference)) return System.IO.File.Exists(reference) ? reference : null;
            var candidate = Path.GetFullPath(Path.Combine(root, reference));
            return System.IO.File.Exists(candidate) ? candidate : null;
        }

        public string DescribeSearch(string reference) => Path.Combine(root, reference);
    }

    private static async Task<string?> Transform(string root, string relativePath, object? body)
    {
        await using var ctx = new RouteContext();
        ctx.AddService(typeof(IRouteResourceResolver), new RootResolver(root));
        ctx.AddRoutes(r => r.From("direct://jres").TransformJson(relativePath));
        await ctx.Start();
        return await ctx.RequestBody<string>("direct://jres", body);
    }

    [Fact]
    public async Task A_specification_the_resolver_knows_is_found_although_the_base_directory_does_not_hold_it()
    {
        var pkg = Directory.CreateTempSubdirectory("redb-jt-pkg-");
        try
        {
            Directory.CreateDirectory(Path.Combine(pkg.FullName, "specs"));
            await System.IO.File.WriteAllTextAsync(Path.Combine(pkg.FullName, "specs", "order.jsonata"), "orderId");

            var result = await Transform(pkg.FullName, "specs/order.jsonata", """{"orderId":"A-1"}""");

            result.Should().Be("\"A-1\"");
        }
        finally
        {
            pkg.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Two_resolvers_with_one_relative_name_are_two_specifications()
    {
        var a = Directory.CreateTempSubdirectory("redb-jt-ra-");
        var b = Directory.CreateTempSubdirectory("redb-jt-rb-");
        try
        {
            await System.IO.File.WriteAllTextAsync(Path.Combine(a.FullName, "s.jsonata"), "a");
            await System.IO.File.WriteAllTextAsync(Path.Combine(b.FullName, "s.jsonata"), "b");

            (await Transform(a.FullName, "s.jsonata", """{"a":1,"b":2}""")).Should().Be("1");
            (await Transform(b.FullName, "s.jsonata", """{"a":1,"b":2}""")).Should().Be("2");
        }
        finally
        {
            a.Delete(recursive: true);
            b.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_specification_in_neither_place_names_both_of_them()
    {
        var resolverRoot = Directory.CreateTempSubdirectory("redb-jt-nr-");
        var baseDir = Directory.CreateTempSubdirectory("redb-jt-nb-");
        try
        {
            await using var ctx = new RouteContext();
            ctx.AddService(typeof(IRouteResourceResolver), new RootResolver(resolverRoot.FullName));
            ctx.UseJsonTransform(o => o.BaseDirectory = baseDir.FullName);
            ctx.AddRoutes(r => r.From("direct://jmissing").TransformJson("nowhere.jsonata"));

            var act = () => ctx.Start();

            (await act.Should().ThrowAsync<Exception>()).Which.ToString()
                .Should().Contain(resolverRoot.FullName).And.Contain(baseDir.FullName);
        }
        finally
        {
            resolverRoot.Delete(recursive: true);
            baseDir.Delete(recursive: true);
        }
    }
}

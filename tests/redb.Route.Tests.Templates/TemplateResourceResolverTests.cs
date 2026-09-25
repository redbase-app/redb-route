using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Templates;
using redb.Route.TestKit;

namespace redb.Route.Tests.Templates;

/// <summary>
/// A template named by a relative path is looked up the way every other file reference in the
/// framework is: the context's <see cref="IRouteResourceResolver"/> first, then the package's base
/// directory. Reported by the Route-XML agent 2026-09-24 — a route inside a <c>.tpkg</c> keeps its
/// template in <c>resources/</c>, which only the resolver knows about, so the step failed at start
/// while <c>validateXsd</c> and <c>xslt</c> in the same package found their files.
/// </summary>
public class TemplateResourceResolverTests
{
    /// <summary>Resolver that knows exactly one root, as the package resolver does.</summary>
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

    private static async Task<string?> Render(string root, string relativePath)
    {
        await using var ctx = new RouteContext();
        ctx.AddService(typeof(IRouteResourceResolver), new RootResolver(root));
        ctx.AddRoutes(r => r.From("direct://res").SetBodyTemplate(relativePath, MediaType.Text));
        await ctx.Start();
        return await ctx.RequestBody<string>("direct://res", null);
    }

    [Fact]
    public async Task A_template_the_resolver_knows_is_found_although_the_base_directory_does_not_hold_it()
    {
        var pkg = Directory.CreateTempSubdirectory("redb-tpl-pkg-");
        try
        {
            Directory.CreateDirectory(Path.Combine(pkg.FullName, "templates"));
            await System.IO.File.WriteAllTextAsync(Path.Combine(pkg.FullName, "templates", "order.sbn"), "FROM-PACKAGE");

            // BaseDirectory stays the worker's directory, exactly as in a .tpkg: nobody repoints it.
            (await Render(pkg.FullName, "templates/order.sbn")).Should().Be("FROM-PACKAGE");
        }
        finally
        {
            pkg.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Two_resolvers_with_one_relative_name_are_two_templates()
    {
        var a = Directory.CreateTempSubdirectory("redb-tpl-ra-");
        var b = Directory.CreateTempSubdirectory("redb-tpl-rb-");
        try
        {
            await System.IO.File.WriteAllTextAsync(Path.Combine(a.FullName, "t.sbn"), "FROM-A");
            await System.IO.File.WriteAllTextAsync(Path.Combine(b.FullName, "t.sbn"), "FROM-B");

            // The compile cache is process-wide: keyed by the name alone, a hot-reloaded module would
            // render the previous version's text.
            (await Render(a.FullName, "t.sbn")).Should().Be("FROM-A");
            (await Render(b.FullName, "t.sbn")).Should().Be("FROM-B");
        }
        finally
        {
            a.Delete(recursive: true);
            b.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_template_in_the_base_directory_is_still_found_when_the_resolver_has_none()
    {
        var baseDir = Directory.CreateTempSubdirectory("redb-tpl-base-");
        var empty = Directory.CreateTempSubdirectory("redb-tpl-empty-");
        try
        {
            await System.IO.File.WriteAllTextAsync(Path.Combine(baseDir.FullName, "t.sbn"), "FROM-BASE");

            await using var ctx = new RouteContext();
            ctx.AddService(typeof(IRouteResourceResolver), new RootResolver(empty.FullName));
            ctx.UseTemplates(o => o.BaseDirectory = baseDir.FullName);
            ctx.AddRoutes(r => r.From("direct://fallback").SetBodyTemplate("t.sbn", MediaType.Text));
            await ctx.Start();

            (await ctx.RequestBody<string>("direct://fallback", null)).Should().Be("FROM-BASE");
        }
        finally
        {
            baseDir.Delete(recursive: true);
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_template_in_neither_place_names_both_of_them()
    {
        var resolverRoot = Directory.CreateTempSubdirectory("redb-tpl-nr-");
        var baseDir = Directory.CreateTempSubdirectory("redb-tpl-nb-");
        try
        {
            await using var ctx = new RouteContext();
            ctx.AddService(typeof(IRouteResourceResolver), new RootResolver(resolverRoot.FullName));
            ctx.UseTemplates(o => o.BaseDirectory = baseDir.FullName);
            ctx.AddRoutes(r => r.From("direct://missing").SetBodyTemplate("nowhere.sbn", MediaType.Text));

            var act = () => ctx.Start();

            // Whoever reads the failure has to know where it looked — both places, as the validator
            // and the XSLT step report them.
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

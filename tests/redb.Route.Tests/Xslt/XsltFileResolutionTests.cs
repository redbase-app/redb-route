using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Xslt;

/// <summary>
/// The <c>.Xslt(file)</c> step (<c>&lt;xslt file="…"/&gt;</c> in markup) finds its stylesheet through the context's resource
/// resolver, as the <c>xslt:</c> endpoint does: a package keeps its stylesheet where only the resolver knows. Before, the
/// path was opened relative to the working directory, and a module on the Tsak worker failed with "Could not find a part
/// of the path '/app/resources/…'".
/// </summary>
public sealed class XsltFileResolutionTests : IAsyncLifetime
{
    private const string Stylesheet = """
        <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="text"/>
          <xsl:template match="/order">invoice:<xsl:value-of select="@id"/></xsl:template>
        </xsl:stylesheet>
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "xslt-res-" + Guid.NewGuid().ToString("N"));
    private readonly RouteContext _context = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "resources"));
        File.WriteAllText(Path.Combine(_dir, "resources", "order-to-invoice.xslt"), Stylesheet);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Resolves references under one directory only, as a package's resolver does.</summary>
    private sealed class PackageResolver(string root) : IRouteResourceResolver
    {
        public string? Resolve(string reference)
        {
            var candidate = Path.Combine(root, reference);
            return File.Exists(candidate) ? candidate : null;
        }

        public string DescribeSearch(string reference) => Path.Combine(root, reference);
    }

    [Fact]
    public async Task The_stylesheet_of_an_xslt_step_is_found_through_the_resource_resolver()
    {
        _context.AddService(typeof(IRouteResourceResolver), new PackageResolver(_dir));
        object? result = null;
        _context.AddRoutes(r => r.From("direct://xslt-in")
            .Xslt("resources/order-to-invoice.xslt")
            .Process(e => result = e.In.Body));
        await _context.Start();
        var producer = _context.GetEndpoint("direct://xslt-in").CreateProducer();
        await producer.Start();

        await producer.Process(new Exchange(new Message("<order id=\"o-1\"/>")));

        result.Should().Be("invoice:o-1");
    }

    [Fact]
    public async Task A_stylesheet_nobody_can_find_fails_the_start_naming_where_it_was_looked_for()
    {
        _context.AddService(typeof(IRouteResourceResolver), new PackageResolver(_dir));
        _context.AddRoutes(r => r.From("direct://xslt-missing").Xslt("resources/missing.xslt"));

        var start = () => _context.Start();

        (await start.Should().ThrowAsync<Exception>()).Which.ToString().Should().Contain("missing.xslt");
    }
}

using redb.Route.Core;
using redb.Route.Templates;
using redb.Route.TestKit;

namespace redb.Route.Tests.Templates;

/// <summary>Code review 2026-09-01: the framework's own DI-scope properties are not part of the template model.</summary>
public class TemplateModelReviewTests
{
    [Fact]
    public async Task Properties_DoNotExposeServiceScopes()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://props")
                .SetProperty("__redb_scope:x", "leak")
                .SetProperty("visible", "yes")
                .SetBodyTemplate(TextSource.Inline("{{ properties | object.has_key '__redb_scope:x' }}|{{ properties.visible }}"), MediaType.Text));
        await ctx.Start();

        var body = await ctx.RequestBody<string>("direct://props", "in");

        body.Should().Be("false|yes");
    }
}

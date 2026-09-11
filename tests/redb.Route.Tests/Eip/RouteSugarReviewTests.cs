using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Eip;

/// <summary>Code review 2026-09-01: a wildcard property mask must not touch the framework's own DI-scope bookkeeping.</summary>
public class RouteSugarReviewTests
{
    [Fact]
    public async Task RemoveProperties_Wildcard_KeepsTheServiceScopeProperties()
    {
        bool? scopeKept = null;
        bool? userGone = null;
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://rp-all")
                .SetProperty("tmp", 1)
                .SetProperty("__redb_scope:test", "scope-marker")
                .RemoveProperties("*")
                .Process(e =>
                {
                    scopeKept = e.Properties.ContainsKey("__redb_scope:test");
                    userGone = !e.Properties.ContainsKey("tmp");
                }));
        await ctx.Start();

        await ctx.SendBody("direct://rp-all", "x");

        userGone.Should().BeTrue();
        scopeKept.Should().BeTrue("scope bookkeeping is not user data; losing it leaks the DI scopes ReleaseScopes would dispose");
    }
}

using redb.Route.Core;
using redb.Route.JsonTransform;
using redb.Route.TestKit;

namespace redb.Route.Tests.JsonTransform;

/// <summary>Code review 2026-09-01 (В11): the specification cache is keyed by the resolved file, not by the relative name.</summary>
public class JsonTransformCacheKeyTests
{
    [Fact]
    public async Task CompileCache_IsKeyedByTheResolvedFile_NotByTheRelativeName()
    {
        var a = Directory.CreateTempSubdirectory("redb-jsonata-a-");
        var b = Directory.CreateTempSubdirectory("redb-jsonata-b-");
        try
        {
            await System.IO.File.WriteAllTextAsync(Path.Combine(a.FullName, "s.jsonata"), "\"FROM-A\"");
            await System.IO.File.WriteAllTextAsync(Path.Combine(b.FullName, "s.jsonata"), "\"FROM-B\"");

            (await TransformFrom(a.FullName)).Should().Be("\"FROM-A\"");
            (await TransformFrom(b.FullName)).Should().Be("\"FROM-B\"", "a different base directory is a different specification");

            await System.IO.File.WriteAllTextAsync(Path.Combine(a.FullName, "s.jsonata"), "\"EDITED-A\"");
            (await TransformFrom(a.FullName)).Should().Be("\"EDITED-A\"", "a new context compiles the edited file afresh");
        }
        finally
        {
            a.Delete(recursive: true);
            b.Delete(recursive: true);
        }

        static async Task<string?> TransformFrom(string baseDirectory)
        {
            await using var ctx = new RouteContext();
            ctx.UseJsonTransform(o => o.BaseDirectory = baseDirectory);
            ctx.AddRoutes(r => r.From("direct://k").TransformJson("s.jsonata"));
            await ctx.Start();
            return await ctx.RequestBody<string>("direct://k", "{}");
        }
    }
}

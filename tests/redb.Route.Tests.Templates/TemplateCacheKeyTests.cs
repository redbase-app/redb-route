using redb.Route.Core;
using redb.Route.Templates;
using redb.Route.TestKit;

namespace redb.Route.Tests.Templates;

/// <summary>
/// Code review 2026-09-01 (В11): the compile cache is process-wide, so it must be keyed by the resolved
/// file, not by the relative name — two base directories are two templates, and an edited file is a
/// new template for the next context that starts.
/// </summary>
public class TemplateCacheKeyTests
{
    [Fact]
    public async Task CompileCache_IsKeyedByTheResolvedFile_NotByTheRelativeName()
    {
        var a = Directory.CreateTempSubdirectory("redb-tpl-a-");
        var b = Directory.CreateTempSubdirectory("redb-tpl-b-");
        try
        {
            await System.IO.File.WriteAllTextAsync(Path.Combine(a.FullName, "t.sbn"), "FROM-A");
            await System.IO.File.WriteAllTextAsync(Path.Combine(b.FullName, "t.sbn"), "FROM-B");

            (await RenderFrom(a.FullName)).Should().Be("FROM-A");
            (await RenderFrom(b.FullName)).Should().Be("FROM-B", "a different base directory is a different template");

            await System.IO.File.WriteAllTextAsync(Path.Combine(a.FullName, "t.sbn"), "EDITED-A");
            (await RenderFrom(a.FullName)).Should().Be("EDITED-A", "a new context compiles the edited file afresh");
        }
        finally
        {
            a.Delete(recursive: true);
            b.Delete(recursive: true);
        }

        static async Task<string?> RenderFrom(string baseDirectory)
        {
            await using var ctx = new RouteContext();
            ctx.UseTemplates(o => o.BaseDirectory = baseDirectory);
            ctx.AddRoutes(r => r.From("direct://k").SetBodyTemplate("t.sbn", MediaType.Text));
            await ctx.Start();
            return await ctx.RequestBody<string>("direct://k", null);
        }
    }
}

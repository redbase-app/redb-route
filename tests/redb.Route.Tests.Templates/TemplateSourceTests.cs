using System.Xml.Linq;
using redb.Route.Core;
using redb.Route.Templates;
using redb.Route.TestKit;
using static redb.Route.Core.TextSource;

namespace redb.Route.Tests.Templates;

/// <summary>Locators, files, embedded resources, compile errors and the compile cache.</summary>
public class TemplateSourceTests
{
    [Fact]
    public async Task MissingFile_FailsStart_WithTheLocator()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://nf").SetBodyTemplate("Templates/nope.sbn", MediaType.Text));

        var act = () => ctx.Start();

        var ex = await act.Should().ThrowAsync<TemplateCompilationException>();
        ex.Which.TemplateName.Should().Be("Templates/nope.sbn");
        ex.Which.Message.Should().Contain("Templates/nope.sbn").And.Contain("not found");
    }

    [Fact]
    public async Task SyntaxError_FailsStart_WithLineAndColumn()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://se").SetBodyTemplate("Templates/broken.sbn", MediaType.Text));

        var act = () => ctx.Start();

        var ex = await act.Should().ThrowAsync<TemplateCompilationException>();
        ex.Which.Line.Should().Be(2);
        ex.Which.Message.Should().StartWith("Template 'Templates/broken.sbn' (2,");
    }

    [Fact]
    public async Task InlineSyntaxError_FailsStart_WithInlineName()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://ise").SetBodyTemplate(Inline("{{ if }}"), MediaType.Text));

        var act = () => ctx.Start();

        (await act.Should().ThrowAsync<TemplateCompilationException>()).Which.TemplateName.Should().Be("<inline>");
    }

    [Fact]
    public void Cache_SameSource_CompilesOnce()
    {
        var options = new RouteTemplateOptions();

        var first = TemplateEngine.Compile(File("Templates/order-confirm.json.sbn"), options);
        var second = TemplateEngine.Compile(File("Templates/order-confirm.json.sbn"), options);
        second.Should().BeSameAs(first, "the cache is keyed by source identity");

        // Same text, both syntaxes (the file above uses {{~ ~}} which Liquid does not have).
        var scriban = TemplateEngine.Compile(Inline("{{ headers.x }}"), options);
        var liquid = TemplateEngine.Compile(Inline("{{ headers.x }}"), new RouteTemplateOptions { Liquid = true });
        liquid.Should().NotBeSameAs(scriban, "syntax is part of the key");
    }

    [Fact]
    public async Task Cache_TwoRoutesOneFile_BothStartAndRender()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://one").SetBodyTemplate("Templates/order-confirm.json.sbn", MediaType.Json, a => a.SetValue("customer", "1"));
            b.From("direct://two").SetBodyTemplate("Templates/order-confirm.json.sbn", MediaType.Json, a => a.SetValue("customer", "2"));
        });
        await ctx.Start();
        var body = """{"items":[]}""";
        var headers = new Dictionary<string, object?> { ["orderId"] = 1 };

        (await ctx.RequestBodyAndHeaders<string>("direct://one", body, headers)).Should().Contain("\"customer\": \"1\"");
        (await ctx.RequestBodyAndHeaders<string>("direct://two", body, headers)).Should().Contain("\"customer\": \"2\"");
    }

    [Fact]
    public async Task EmbeddedLocator_Resolves()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://emb")
                .SetBodyTemplate("assembly:redb.Route.Tests.Templates/Templates/notify.xml.sbn", MediaType.Xml, a => a
                    .Set("customer", "header.customerId")
                    .SetValue("channel", "email")));
        await ctx.Start();

        var xml = await ctx.RequestBodyAndHeaders<string>("direct://emb", null,
            new Dictionary<string, object?> { ["customerId"] = "C&Co", ["note"] = "<hi>" });

        var root = XDocument.Parse(xml!).Root!;
        root.Attribute("channel")!.Value.Should().Be("email");
        root.Element("customer")!.Value.Should().Be("C&Co");
        root.Element("note")!.Value.Should().Be("<hi>");
    }

    [Fact]
    public void EmbeddedFactory_WithAssembly_Resolves()
    {
        var source = Embedded(typeof(TemplateSourceTests).Assembly, "Templates/notify.xml.sbn");

        source.Read(AppContext.BaseDirectory).Should().Contain("<notify");
    }

    [Fact]
    public async Task PlainStringIsALocator_NeverInlineText()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://loc").SetBodyTemplate("{{ headers.x }}", MediaType.Text));

        var act = () => ctx.Start();

        (await act.Should().ThrowAsync<TemplateCompilationException>()).Which.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task AbsolutePath_And_BaseDirectoryOption()
    {
        var dir = Directory.CreateTempSubdirectory("redb-templates-");
        try
        {
            var path = Path.Combine(dir.FullName, "t.sbn");
            await System.IO.File.WriteAllTextAsync(path, "hello {{ headers.who }}");

            await using var byAbsolute = new RouteContext().AddRoutes(b => b.From("direct://abs").SetBodyTemplate(path, MediaType.Text));
            await byAbsolute.Start();
            (await byAbsolute.RequestBodyAndHeaders<string>("direct://abs", null, new Dictionary<string, object?> { ["who"] = "abs" }))
                .Should().Be("hello abs");

            await using var byBase = new RouteContext();
            byBase.UseTemplates(o => o.BaseDirectory = dir.FullName);
            byBase.AddRoutes(b => b.From("direct://rel").SetBodyTemplate("t.sbn", MediaType.Text));
            await byBase.Start();
            (await byBase.RequestBodyAndHeaders<string>("direct://rel", null, new Dictionary<string, object?> { ["who"] = "rel" }))
                .Should().Be("hello rel");
        }
        finally { dir.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("assembly:OnlyName")]
    [InlineData("assembly:/no-name")]
    [InlineData("assembly:Name/")]
    public void FromLocator_MalformedAssemblyForm_Throws(string locator)
    {
        var act = () => FromLocator(locator);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromLocator_FileForm()
    {
        FromLocator("Templates/x.sbn").Name.Should().Be("Templates/x.sbn");
        FromLocator("Templates/x.sbn").CacheKey.Should().Be("file:Templates/x.sbn");
        Inline("abc").Name.Should().Be("<inline>");
    }
}

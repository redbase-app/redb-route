using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.TestKit;
using static redb.Route.Core.RouteBuilder;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// 4.0 removed the string forms <c>SetBodyExpression(string)</c> / <c>SetHeaderExpression</c> /
/// <c>SetPropertyExpression</c> / <c>TransformExpression(string)</c> (docs/V4/09-BREAKING.md §4,
/// option б). The removal was safe because the string form and <c>Expr(...)</c> already meant the same
/// thing, form by form — proved by an equivalence grid over this corpus before the deletion. What
/// survives is the invariant that grid protected: one engine, so the four verbs answer alike, and a
/// whole-string placeholder keeps the CLR type of its value.
/// </summary>
[Collection("ExpressionResolver")]
public class SetVerbExpressionFormTests
{
    /// <summary>Forms that exercise the placeholder pipeline: whole-string, mixed, arithmetic, comparison, accessors, literals, jpath, explicit locale.</summary>
    public static TheoryData<string> Templates =>
    [
        "${header.name}",
        "${header.count}",
        "${header.price}",
        "Hello ${header.name}!",
        "${header.first} ${header.last}",
        "${header.a + header.b}",
        "${header.count > 5}",
        "${header.count>5}",
        "${body}",
        "${header.Content-Type}",
        "${header.missing}",
        "${property.tenant}",
        "plain text",
        "${jpath($.id)}",
        "${format(header.price, 'N2', 'ru-RU')}",
    ];

    private static Dictionary<string, object?> Headers() => new()
    {
        ["name"] = "world",
        ["count"] = 7,
        ["price"] = 19.95m,
        ["first"] = "Ada",
        ["last"] = "Lovelace",
        ["a"] = 2,
        ["b"] = 3,
        ["Content-Type"] = "application/json",
    };

    private const string Body = """{"id": "DRV-1"}""";

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task TheFourVerbs_AgreeOnEveryForm(string template)
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://body").SetBody(Expr(template)).To("mock://body");
            b.From("direct://transform").Transform(Expr(template)).To("mock://transform");
            b.From("direct://header").SetHeader("out", Expr(template)).To("mock://header");
            b.From("direct://property").SetProperty("out", Expr(template)).To("mock://property");
        });
        await ctx.Start();

        foreach (var target in new[] { "body", "transform", "header", "property" })
            await ctx.SendBodyAndHeaders($"direct://{target}", Body, Headers());

        var fromBody = ctx.Mock("mock://body").ReceivedExchanges[0].In.Body;
        var fromTransform = ctx.Mock("mock://transform").ReceivedExchanges[0].In.Body;
        var fromHeader = ctx.Mock("mock://header").ReceivedExchanges[0].In.Headers["out"];
        var fromProperty = ctx.Mock("mock://property").ReceivedExchanges[0].Properties["out"];

        fromTransform.Should().BeEquivalentTo(fromBody, "one engine serves every position");
        fromHeader.Should().BeEquivalentTo(fromBody);
        fromProperty.Should().BeEquivalentTo(fromBody);
        fromTransform?.GetType().Should().Be(fromBody?.GetType());
        fromHeader?.GetType().Should().Be(fromBody?.GetType());
        fromProperty?.GetType().Should().Be(fromBody?.GetType());
    }

    [Fact]
    public async Task AWholeStringPlaceholder_KeepsTheClrType_AndAMixedOneRendersInvariant()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://typed").SetBody(Expr("${header.price}")).To("mock://typed");
            b.From("direct://mixed").SetBody(Expr("total ${header.price}")).To("mock://mixed");
        });
        await ctx.Start();

        await ctx.SendBodyAndHeaders("direct://typed", null, Headers());
        await ctx.SendBodyAndHeaders("direct://mixed", null, Headers());

        ctx.Mock("mock://typed").ReceivedExchanges[0].In.Body.Should().Be(19.95m, "a whole-string placeholder is the value, not its text");
        ctx.Mock("mock://mixed").ReceivedExchanges[0].In.Body.Should().Be("total 19.95", "text is invariant; ask for a locale with format(...)");
    }

    [Fact]
    public async Task APlainStringIsALiteral_TheContentNeverDecides()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://literal").SetBody("Hello ${header.name}").To("mock://literal"));
        await ctx.Start();

        await ctx.SendBodyAndHeaders("direct://literal", null, Headers());

        ctx.Mock("mock://literal").ReceivedExchanges[0].In.Body.Should().Be("Hello ${header.name}",
            "SetBody(string) is a literal body; the template form is SetBody(Expr(...))");
    }
}

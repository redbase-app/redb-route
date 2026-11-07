using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Templates;
using redb.Route.TestKit;
using static redb.Route.Core.TextSource;

namespace redb.Route.Tests.Templates;

/// <summary>A bean a payload template might try to reach.</summary>
public class TemplateGoldRules
{
    public static bool Called;

    public bool IsGold(string? name)
    {
        Called = true;
        return name == "ann";
    }
}

/// <summary>
/// Where the bean: function meets the template sandbox, as EXPRESSIONS.md section 11 states it. A template argument is
/// a compiled expression evaluated under the sandbox, so a bean: call there is refused. A payload template's
/// expr(...) reads its text as the inside of a ${...} hole (findings register rows 2 and 22, closed), so a bean: call
/// there is refused the same way. In neither case does the bean run.
/// </summary>
public sealed class BeanCallInTemplateTests
{
    private static async Task<string?> Render(string template, Action<TemplateArgs>? args = null)
    {
        await using var ctx = new RouteContext();
        ctx.AddToRegistry("rules", new TemplateGoldRules());
        ctx.AddRoutes(b => b.From("direct://in").SetBodyTemplate(Inline(template), MediaType.Text, args));
        await ctx.Start();
        return await ctx.RequestBodyAndHeaders<string>("direct://in", "ann", new Dictionary<string, object?>());
    }

    [Fact]
    public async Task ATemplateArgument_IsEvaluatedUnderTheSandbox_AndTheBeanCallIsRefused()
    {
        TemplateGoldRules.Called = false;
        var act = () => Render("{{ args.gold }}", a => a.Set("gold", "bean:rules.IsGold(body)"));

        var thrown = await act.Should().ThrowAsync<Exception>();
        Inner(thrown.Which).Should().BeOfType<ExpressionSandboxViolationException>();
        TemplateGoldRules.Called.Should().BeFalse();
    }

    [Fact]
    public async Task ExprInThePayload_IsEvaluatedUnderTheSandbox_AndTheBeanCallIsRefused()
    {
        TemplateGoldRules.Called = false;
        var act = () => Render("{{ expr('bean:rules.IsGold(body)') }}");

        var thrown = await act.Should().ThrowAsync<Exception>();
        Inner(thrown.Which).Should().BeOfType<ExpressionSandboxViolationException>();
        TemplateGoldRules.Called.Should().BeFalse();
    }

    private static Exception Inner(Exception exception)
    {
        var current = exception;
        while (current is not ExpressionSandboxViolationException && current.InnerException is { } inner)
            current = inner;
        return current;
    }
}

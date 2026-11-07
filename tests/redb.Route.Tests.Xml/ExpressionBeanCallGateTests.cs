using AwesomeAssertions;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>A bean the gate holds expression calls against.</summary>
public class GateRules
{
    public bool IsGold(string? name) => name == "ann";

    public string Badge(string name, int points) => $"{name}:{points}";

    public Task<bool> CheckAsync(string name) => Task.FromResult(name == "ann");
}

/// <summary>
/// The package gate on <c>bean:</c> calls in expressions (stage 2 step e): a call that does not parse is an error at
/// its attribute; an undeclared bean is the usual <c>#name</c> warning; with the built assemblies, a call to a declared
/// bean is held against its type: the method by name and bindable count, and synchronous.
/// </summary>
public sealed class ExpressionBeanCallGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-xml-beanfn-" + Guid.NewGuid().ToString("N"));

    public ExpressionBeanCallGateTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Type? Resolve(string typeName) => Type.GetType(typeName);

    private static string Rules => $"{typeof(GateRules).FullName}, {typeof(GateRules).Assembly.GetName().Name}";

    private string Project(string steps)
    {
        var dir = RouteProjectScaffold.Create(_root, "BeanFn" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), $"""
            <routes xmlns="urn:redb:route:1.0">
              <bean name="rules" type="{Rules}"/>
              <route id="beanfn">
                <from uri="direct://in"/>
            {steps}
              </route>
            </routes>
            """);
        return dir;
    }

    private PackageResult Check(string steps) => RoutePackage.Check(Project(steps), "p", "1.0.0", Resolve);

    [Theory]
    [InlineData("""<filter expr="bean:rules.IsGold(header.name)"><to uri="log:x"/></filter>""")]
    [InlineData("""<setBody expr="${bean:rules.Badge(header.name, ${header.points})}"/>""")]
    [InlineData("""<toD uri="log:${bean:rules.Badge(header.name, 1)}"/>""")]
    [InlineData("""<to><log path="x" message="${bean:rules.Badge('a', 2)}"/></to>""")]
    public void ACorrectCall_PassesTheGate(string step)
    {
        Check(step).Errors.Should().BeEmpty();
    }

    [Fact]
    public void ACallThatDoesNotParse_IsAnErrorAtTheAttribute()
    {
        Check("""<setHeader name="h" expr="bean:rules"/>""").Errors.Should().Contain(e =>
            e.Message == "expr= a bean: call names its method: bean:rules.Method(...) (position 1 within the expression)."
            && e.File.Contains("main.route.xml("));
    }

    [Theory]
    [InlineData("""<setBody expr="${bean:rules.Missing(header.name)}"/>""", "*'#rules' has no public method 'Missing' taking 1 argument(s)*")]
    [InlineData("""<filter expr="bean:rules.Badge(header.name)"><to uri="log:x"/></filter>""", "*'#rules' has no public method 'Badge' taking 1 argument(s)*")]
    public void AWrongMethodOrArity_OnADeclaredBean_IsAnError(string step, string message)
    {
        Check(step).Errors.Should().Contain(e => e.Message.StartsWith("expr= ", StringComparison.Ordinal)
                                                 && Matches(e.Message, message));
    }

    [Fact]
    public void AnAsyncMethod_OnADeclaredBean_IsAnError()
    {
        Check("""<filter expr="bean:rules.CheckAsync(header.name)"><to uri="log:x"/></filter>""").Errors
            .Should().Contain(e => e.Message.Contains("bean:rules.CheckAsync returns Task`1: a bean: call in an expression is synchronous"));
    }

    [Fact]
    public void AnUndeclaredBean_IsTheUsualReferenceWarning()
    {
        var result = Check("""<setBody expr="${bean:pricing.Gross(1, 2)}"/>""");

        result.Errors.Should().BeEmpty("a bean registered by module code is checked when it is called");
        result.Warnings.Should().Contain(w => w.Message.Contains("'#pricing' is not declared by any <bean> of this package"));
    }

    [Theory]
    [InlineData("""<filter expr="bean:rules.IsGold(${header.name})"><to uri="log:x"/></filter>""")]
    [InlineData("""<setBody expr="total bean:rules.Badge(${header.name}, 1)"/>""")]
    public void ABareCallWithPlaceholderArguments_IsWarned_WithBothCorrectSpellings(string step)
    {
        var result = Check(step);

        result.Warnings.Should().Contain(w =>
            w.Message.StartsWith("expr= holds a bean: call outside ${...} with ${...} arguments", StringComparison.Ordinal)
            && w.Message.Contains("bean:svc.Check(header.x)") && w.Message.Contains("${bean:svc.Check(${header.x})}")
            && w.File.Contains("main.route.xml("));
    }

    [Theory]
    [InlineData("""<filter expr="bean:rules.IsGold(header.name)"><to uri="log:x"/></filter>""")]
    [InlineData("""<setBody expr="${bean:rules.Badge(header.name, ${header.points})}"/>""")]
    [InlineData("""<setBody expr="${header.name} is the 'bean:rules' text"/>""")]
    public void TheCorrectSpellings_AreNotWarned(string step)
    {
        Check(step).Warnings.Should().NotContain(w => w.Message.Contains("outside ${...} with ${...} arguments"));
    }

    [Theory]
    [InlineData("""<setBody expr="${ref:chat.settings}"/>""")]
    [InlineData("""<filter expr="ref:chat.settings != null"><to uri="log:x"/></filter>""")]
    [InlineData("""<setBody expr="${bean:rules.IsGold(ref:chat.settings)}"/>""")]
    public void AnUndeclaredRefName_IsTheUsualReferenceWarning(string step)
    {
        var result = Check(step);

        result.Errors.Should().BeEmpty();
        result.Warnings.Should().Contain(w => w.Message.Contains("'#chat.settings' is not declared by any <bean> of this package"));
    }

    [Theory]
    [InlineData("""<filter expr="ref:rules == ${header.x}"><to uri="log:x"/></filter>""")]
    [InlineData("""<setBody expr="limit ref:rules for ${header.name}"/>""")]
    public void ARefNameOutsidePlaceholders_InATemplate_IsWarned_WithBothCorrectSpellings(string step)
    {
        var result = Check(step);

        result.Warnings.Should().Contain(w =>
            w.Message.StartsWith("expr= holds a ref: name outside ${...} in a text with ${...}", StringComparison.Ordinal)
            && w.Message.Contains("ref:limit > header.x") && w.Message.Contains("${ref:limit > header.x}")
            && w.File.Contains("main.route.xml("));
        result.Warnings.Should().NotContain(w => w.Message.Contains("holds a bean: call"));
    }

    [Theory]
    [InlineData("""<filter expr="ref:rules != null"><to uri="log:x"/></filter>""")]
    [InlineData("""<setBody expr="${ref:rules} for ${header.name}"/>""")]
    [InlineData("""<setBody expr="${header.name} is the 'ref:rules' text"/>""")]
    public void TheCorrectRefSpellings_AreNotWarned(string step)
    {
        Check(step).Warnings.Should().NotContain(w => w.Message.Contains("ref: name outside ${...}"));
    }

    // Findings register row 32: the gate compiles each expression as the engine does, so what fails the route at build
    // fails the package, in the engine's words.
    [Theory]
    [InlineData("""<filter expr="header.b == 3 garbage"><to uri="log:x"/></filter>""", "unexpected 'garbage' at position 15 within the expression")]
    [InlineData("""<setBody expr="${exception.type|message}"/>""", "unexpected '|' at position 15 within the expression")]
    [InlineData("""<setBody expr="total ${header.a 7}"/>""", "unexpected '7' at position 10 within the expression")]
    [InlineData("""<setBody expr="${FILE:NAME}"/>""", "${FILE:NAME}: file keys are lowercase: ${file:name}.")]
    [InlineData("""<setBody expr="${file:nosuch}"/>""", "'nosuch' is not a file key")]
    [InlineData("""<toD uri="log:${header.a ==}"/>""", "Unexpected token: Eof")]
    public void AnExpressionTheEngineRefuses_IsAnError(string step, string detail)
    {
        var result = Check(step);

        result.Errors.Should().Contain(e => e.Message.Contains(detail, StringComparison.Ordinal) && e.File.Contains("main.route.xml("));
        result.Warnings.Should().NotContain(w => w.Message.Contains("ignored at run time"));
    }

    [Theory]
    [InlineData("""<filter expr="header.b 3"><to uri="log:x"/></filter>""", "filter", "header.b 3", "'3' at position 10")]
    [InlineData("""<setBody expr="exception.Message was"/>""", "setBody", "exception.Message was", "'was' at position 19")]
    public void ABareValueThatBecomesText_IsWarned(string step, string _, string expression, string rest)
    {
        var result = Check(step);

        result.Errors.Should().BeEmpty();
        result.Warnings.Should().Contain(w => w.Message ==
            $"expr= \"{expression}\" reads as the text itself, not as an expression: {rest} does not continue it; in a condition the text is always true.");
    }

    [Theory]
    [InlineData("""<setBody expr="header.b == 3"/>""")]
    [InlineData("""<setBody expr="${header.a} and ${header.b}"/>""")]
    [InlineData("""<setBody expr="plain text"/>""")]
    [InlineData("""<toD uri="file://out?fileName=${file:name.noext}.done"/>""")]
    [InlineData("""<to uri="file://in?moveTo=done/${FILE:NAME}"/>""")]
    [InlineData("""<toD uri="log:${env:REGION}"/>""")]
    [InlineData("""<toD uri="log:${jsonpath:$.order.id}"/>""")]
    [InlineData("""<setBody expr="cost ${header.total:F4}"/>""")]
    [InlineData("""<setBody expr="${header.Content-Type}"/>""")]
    [InlineData("""<setBody expr="${header.user.Manager.Name}"/>""")]
    public void ACompleteExpression_OrAComponentSpelling_PassesTheGate(string step)
    {
        var result = Check(step);

        result.Errors.Should().BeEmpty();
        result.Warnings.Should().NotContain(w => w.Message.Contains("reads as the text itself"));
    }

    [Fact]
    public void ADeclaredRefName_PassesWithoutAWarning()
    {
        var result = Check("""<setBody expr="${ref:rules}"/>""");

        result.Errors.Should().BeEmpty();
        result.Warnings.Should().NotContain(w => w.Message.Contains("'#rules'"));
    }

    [Fact]
    public void ARefWithAParenthesis_IsAnErrorAtTheAttribute()
    {
        Check("""<setBody expr="${ref:rules()}"/>""").Errors.Should().Contain(e =>
            e.Message.StartsWith("expr= ref:rules names a registry object and calls nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void BeanInsideAQuotedLiteral_IsNotACall()
    {
        Check("""<setBody expr="${'bean:rules'}"/>""").Errors.Should().BeEmpty();
    }

    private static bool Matches(string text, string pattern)
        => System.Text.RegularExpressions.Regex.IsMatch(text,
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$");
}

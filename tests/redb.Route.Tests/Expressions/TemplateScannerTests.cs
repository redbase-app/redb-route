using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// The shared <c>${...}</c> reader: balanced holes, quoted braces, the shapes that read as before, and the unclosed
/// <c>${</c> that is an error.
/// </summary>
public sealed class TemplateScannerTests
{
    private static (int, int)[] Spans(string template)
        => [.. TemplateScanner.Scan(template).Holes.Select(h => (h.Start, h.Length))];

    [Theory]
    [InlineData("${body}", 0, 7)]
    [InlineData("a ${header.x} b", 2, 11)]
    [InlineData("${bean:svc.Check(${body})}", 0, 26)]
    [InlineData("${header.a == '}'}", 0, 18)]
    [InlineData("${concat(\"}\", 'x')}", 0, 19)]
    [InlineData("${header.O'Brien}", 0, 17)]
    [InlineData("${ {a} }", 0, 6)]
    [InlineData("$${body}", 1, 7)]
    public void OneHole(string template, int start, int length)
    {
        Spans(template).Should().Equal((start, length));
        TemplateScanner.Scan(template).Unclosed.Should().BeEmpty();
    }

    [Fact]
    public void AdjacentHoles_AreRead_InOrder()
    {
        Spans("${header.a}${header.b} and ${body}").Should().Equal((0, 11), (11, 11), (27, 7));
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("${}")]
    [InlineData("{{key}}")]
    [InlineData("$ {body}")]
    public void NoHole(string template)
    {
        TemplateScanner.Scan(template).Holes.Should().BeEmpty();
        TemplateScanner.HasPlaceholder(template).Should().BeFalse();
    }

    [Theory]
    [InlineData("text ${", 6)]
    [InlineData("text ${body", 6)]
    [InlineData("${a ${b}", 1)]
    [InlineData("${body} and ${", 13)]
    public void Unclosed_IsAPositionedError(string template, int position)
    {
        TemplateScanner.HasPlaceholder(template).Should().BeTrue("a started placeholder is a template, and fails where it compiles");
        var act = () => TemplateScanner.Holes(template);

        act.Should().Throw<ExpressionCompilationException>()
            .WithMessage($"unclosed '${{' at position {position} of \"{template}\": the placeholder has no closing '}}'.");
    }

    [Fact]
    public void Unclosed_FailsTheStringExpression_AndTheTemplate()
    {
        var expression = () => new StringExpression("total ${header.amount");
        var template = () => ExpressionResolver.ProcessTemplate("total ${header.amount", new Exchange());

        expression.Should().Throw<ExpressionCompilationException>().WithMessage("unclosed '${' at position 7*");
        template.Should().Throw<ExpressionCompilationException>().WithMessage("unclosed '${' at position 7*");
    }

    [Fact]
    public void QuotedBrace_ComparesInsteadOfBeingCut()
    {
        var exchange = new Exchange();
        exchange.In.Headers["a"] = "}";

        new StringExpression("${header.a == '}'}").Evaluate<bool>(exchange).Should().BeTrue();
    }

    [Fact]
    public void DocumentedReadings_HoldAsTheReadmeStatesThem()
    {
        var exchange = new Exchange();
        exchange.In.Headers["id"] = 42;
        exchange.In.Headers["O'Brien"] = "obrien";

        ExpressionResolver.ProcessTemplate("${concat('a', '}', 'b')}", exchange).Should().Be("a}b");
        ExpressionResolver.ProcessTemplate("{${header.id}}", exchange).Should().Be("{42}");
        ExpressionResolver.ProcessTemplate("${}", exchange).Should().Be("${}");
        ExpressionResolver.ProcessTemplate("${header.O'Brien}", exchange).Should().Be("obrien");
    }

    [Theory]
    [InlineData("\\${", "${")]
    [InlineData("echo \\${HOME}", "echo ${HOME}")]
    [InlineData("\\${not} and ${header.id}", "${not} and 42")]
    [InlineData("${header.id} costs \\${x}", "42 costs ${x}")]
    [InlineData("a \\${b \\${c", "a ${b ${c")]
    public void AnEscape_IsALiteralPlaceholderOpener(string template, string rendered)
    {
        // Findings register row 16: \${ writes ${ and opens nothing, so it is never unclosed.
        var exchange = new Exchange();
        exchange.In.Headers["id"] = 42;

        TemplateScanner.Scan(template).Unclosed.Should().BeEmpty();
        TemplateScanner.HasPlaceholder(template).Should().BeTrue("an escape makes the text a template, which renders it");
        ExpressionResolver.ProcessTemplate(template, exchange).Should().Be(rendered);
        new StringExpression(template).Evaluate<string>(exchange).Should().Be(rendered);
    }

    [Fact]
    public void AnEscape_IsNotAHole()
    {
        TemplateScanner.Scan("\\${header.x} ${body}").Holes.Should().ContainSingle().Which.Start.Should().Be(13);
        TemplateScanner.Scan("\\${header.x}").Escapes.Should().Equal(0);
        ExpressionResolver.TryGetSinglePlaceholder("\\${header.x}", out _).Should().BeFalse();
    }

    [Fact]
    public void SinglePlaceholder_IsTheWholeBalancedHole()
    {
        ExpressionResolver.TryGetSinglePlaceholder("${header.a == '}'}", out var inner).Should().BeTrue();
        inner.Should().Be("header.a == '}'");
        ExpressionResolver.TryGetSinglePlaceholder("${a} ${b}", out _).Should().BeFalse();
        ExpressionResolver.TryGetSinglePlaceholder("${a ${b}", out _).Should().BeFalse("an unclosed placeholder is no single one");
    }

    [Fact]
    public void EndpointOption_WithAnUnclosedPlaceholder_IsRefusedWhenBound()
    {
        var options = new ProbeOptions();
        var act = () => options.BindFromUri(new Dictionary<string, string> { ["value"] = "a ${header.b" });

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("unclosed '${' at position 3");
    }

    private sealed class ProbeOptions : EndpointOptions
    {
        public DynamicValue<string> Value { get; set; }

        public override void Validate() { }
    }
}

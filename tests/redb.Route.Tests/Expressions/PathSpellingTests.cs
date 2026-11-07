using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Apache Camel's <c>${jsonpath:...}</c> and <c>${xpath:...}</c> (findings register row 34 point 2): spellings of
/// <c>jpath('...')</c> and <c>xpath('...')</c> as a whole placeholder. Until 4.7.0 they rendered empty.
/// </summary>
public sealed class PathSpellingTests
{
    private static Exchange Json() => new(new Message("""{"order":{"id":"A-1","amount":12.5,"items":["a","b"]}}"""));

    private static Exchange Xml() => new(new Message("<order><id>A-1</id><line>a</line><line>b</line></order>"));

    [Theory]
    [InlineData("${jsonpath:$.order.id}", "A-1")]
    [InlineData("${jsonpath: $.order.id }", "A-1")]
    [InlineData("id=${jsonpath:$.order.id}", "id=A-1")]
    public void Jsonpath_IsJpath(string template, string expected)
    {
        ExpressionResolver.ProcessTemplate(template, Json()).Should().Be(expected);
        ExpressionResolver.ProcessTemplate(template.Replace("jsonpath:$.order.id", "jpath('$.order.id')").Replace("jsonpath: $.order.id ", "jpath('$.order.id')"), Json())
            .Should().Be(expected, "the spelling and the function agree");
    }

    [Fact]
    public void Jsonpath_AWholePlaceholderKeepsTheValue_AndTakesAFormat()
    {
        new StringExpression("${jsonpath:$.order.amount}").Evaluate<object>(Json()).Should()
            .Be(new StringExpression("${jpath('$.order.amount')}").Evaluate<object>(Json()));
        ExpressionResolver.ProcessTemplate("${jsonpath:$.order.amount:F2}", Json()).Should().Be("12.50");
    }

    [Fact]
    public void Xpath_IsXpath()
    {
        ExpressionResolver.ProcessTemplate("${xpath://order/id}", Xml())
            .Should().Be(ExpressionResolver.ProcessTemplate("${xpath('//order/id')}", Xml()));
        ExpressionResolver.ProcessTemplate("id=${xpath://order/id/text()}", Xml()).Should().StartWith("id=A-1");
    }

    [Theory]
    [InlineData("${jsonpath:}")]
    [InlineData("${xpath: }")]
    public void ASpellingWithoutAPath_IsAnError(string template)
    {
        var act = () => ExpressionResolver.ProcessTemplate(template, Json());

        act.Should().Throw<ExpressionCompilationException>().WithMessage("*names no path*");
    }
}

using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// A dotted path splits at its separators only (findings register row 33): a dot inside a method's parentheses, in a
/// quoted argument or in square brackets belongs to the argument, so <c>list.contains(5.0)</c> is one method call.
/// </summary>
public sealed class DottedPathArgumentTests
{
    private static Exchange Message()
    {
        var exchange = new Exchange(new Message("x"));
        exchange.In.Headers["ints"] = new List<int> { 3 };
        exchange.In.Headers["doubles"] = new List<double> { 2.5 };
        exchange.In.Headers["names"] = new List<string> { "a.b" };
        exchange.In.Headers["s"] = "x.a.b";
        return exchange;
    }

    [Theory]
    [InlineData("${header.ints.contains(3.0)}", true)]
    [InlineData("${header.ints.contains(5.0)}", false)]
    [InlineData("${header.doubles.contains(2.5)}", true)]
    [InlineData("${header.doubles.contains(7.5)}", false)]
    [InlineData("${header.names.contains('a.b')}", true)]
    [InlineData("${header.names.contains('a.c')}", false)]
    [InlineData("${header.s.contains('a.b')}", true)]
    [InlineData("${header.s.contains(\"a.c\")}", false)]
    public void AMethodArgument_KeepsItsDots(string template, bool expected)
    {
        new StringExpression(template).Evaluate<object>(Message()).Should().Be(expected);
    }

    [Fact]
    public void ThePathStillSplitsAtItsSeparators()
    {
        var exchange = Message();
        exchange.In.Headers["user"] = new Dictionary<string, object> { ["Name"] = "Ann" };

        new StringExpression("${header.user.Name.length()}").Evaluate<object>(exchange).Should().Be(3);
    }
}

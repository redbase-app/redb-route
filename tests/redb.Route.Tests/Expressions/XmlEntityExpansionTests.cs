using System.Text;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// A message body is somebody else's XML, whatever transport carried it. Evaluating an XPath over one
/// used to parse it with the expanding loader, so a 452-byte body with nested internal entities cost
/// the worker hundreds of thousands of characters — and that is the small version. Reported while
/// preparing the AS4 connector, 2026-09-25.
/// </summary>
public class XmlEntityExpansionTests
{
    private static string BillionLaughs(int levels = 5, int fanOut = 10)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\">");
        for (var i = 1; i <= levels; i++)
        {
            sb.Append($"<!ENTITY lol{i} \"");
            for (var j = 0; j < fanOut; j++) sb.Append(i == 1 ? "&lol;" : $"&lol{i - 1};");
            sb.Append("\">");
        }
        return sb.Append($"]><lolz>&lol{levels};</lolz>").ToString();
    }

    private static IExchange WithBody(object body) => new Exchange(new Message(body));

    [Fact]
    public void An_xpath_over_a_body_with_a_dtd_is_refused_not_expanded()
    {
        var exchange = WithBody(BillionLaughs());

        var act = () => new XPathExpression("/lolz").Evaluate<string>(exchange);

        // Refused, and refused loudly: a route that cannot read the body must not quietly carry on
        // with an empty value either.
        act.Should().Throw<Exception>().Which.ToString().Should().Contain("DTD");
    }

    [Fact]
    public void An_ordinary_xml_body_still_evaluates()
    {
        var exchange = WithBody("<order><id>7</id></order>");

        new XPathExpression("/order/id").Evaluate<string>(exchange).Should().Be("7");
    }
}

using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>A declared bean the expression function calls from markup.</summary>
public class XmlLoyalty
{
    public string Gold { get; set; } = "ann";

    public bool IsGold(string? name) => name == Gold;

    public string Badge(string name, int points) => $"{name}:{points}";
}

/// <summary>
/// The <c>bean:</c> expression function in route markup: a bare call in a condition, a call inside a <c>${...}</c>
/// value, against a bean the document itself declares.
/// </summary>
public sealed class XmlBeanFunctionTests : IAsyncLifetime
{
    private readonly RouteContext _context = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task FilterAndSetBody_CallTheDeclaredBean()
    {
        _context.AddXmlRoutesFromContent($$"""
            <routes xmlns="urn:redb:route:1.0">
              <bean name="loyalty" type="{{typeof(XmlLoyalty).FullName}}, {{typeof(XmlLoyalty).Assembly.GetName().Name}}">
                <property key="Gold" value="ann"/>
              </bean>
              <route id="xml-bean-fn">
                <from uri="direct://xml-bean-fn"/>
                <filter expr="bean:loyalty.IsGold(header.name)">
                  <setBody expr="${bean:loyalty.Badge(header.name, ${header.points})}"/>
                  <to uri="mock://gold"/>
                </filter>
              </route>
            </routes>
            """);
        await _context.Start();
        var producer = _context.GetEndpoint("direct://xml-bean-fn").CreateProducer();
        await producer.Start();
        var mock = (redb.Route.Components.MockEndpoint)_context.GetEndpoint("mock://gold");

        foreach (var name in new[] { "ann", "bob" })
        {
            var exchange = new Exchange(new Message("in"));
            exchange.In.Headers["name"] = name;
            exchange.In.Headers["points"] = 7;
            await producer.Process(exchange);
        }

        mock.ReceivedExchanges.Select(e => e.In.Body).Should().Equal("ann:7");
    }
}

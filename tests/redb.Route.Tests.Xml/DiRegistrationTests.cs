using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Extensions;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// Route-XML Ф2, DI surface: XML routes registered inside <c>AddRedbRoute(...)</c> load through
/// the standard <see cref="IRouteContextConfigurator"/> hook — the way an ordinary ASP.NET host
/// runs them, no Tsak involved.
/// </summary>
public class DiRegistrationTests
{
    [Fact]
    public async Task AddXmlRoutesFromContent_InsideAddRedbRoute_LoadsAtContextStart()
    {
        var services = new ServiceCollection();
        services.AddRedbRoute(route => route.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="di-route">
                <from uri="direct://di-in"/>
                <setHeader name="via" value="di"/>
              </route>
            </routes>
            """, "di.xml"));

        await using var provider = services.BuildServiceProvider();
        var context = provider.GetRequiredService<RouteContext>();
        // The hosted service applies configurators before Start; the test does the same by hand.
        foreach (var configurator in provider.GetServices<IRouteContextConfigurator>())
            configurator.Configure(context);
        await context.Start();
        try
        {
            var producer = context.GetEndpoint("direct://di-in").CreateProducer();
            await producer.Start();
            var exchange = new Exchange(new Message("x"));
            await producer.Process(exchange);

            context.Routes.Select(r => r.RouteId).Should().Contain("di-route");
            exchange.In.Headers["via"].Should().Be("di");
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    [Fact]
    public async Task BrokenXml_RegisteredInDi_FailsWhenConfiguratorsApply()
    {
        var services = new ServiceCollection();
        services.AddRedbRoute(route => route.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="di-broken">
                <from uri="direct://di-broken-in"/>
                <nosuch/>
              </route>
            </routes>
            """, "di-broken.xml"));

        await using var provider = services.BuildServiceProvider();
        var context = provider.GetRequiredService<RouteContext>();
        try
        {
            var act = () =>
            {
                foreach (var configurator in provider.GetServices<IRouteContextConfigurator>())
                    configurator.Configure(context);
            };

            act.Should().Throw<XmlRouteException>().WithMessage("*di-broken.xml*nosuch*");
        }
        finally
        {
            await context.DisposeAsync();
        }
    }
}

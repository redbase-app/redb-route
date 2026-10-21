using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.TestKit;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// <c>&lt;routeConfiguration&gt;</c>, the markup form of a route configuration (Apache Camel's Route Configuration):
/// <c>&lt;onException&gt;</c> blocks shared by the routes of the context — in a routes file, where they reach the routes
/// of the other files too, and in <c>context.xml</c>. A route names configurations with <c>routeConfigurationId</c>;
/// the configuration without an id is the default, for the routes that name none.
/// </summary>
public class RouteConfigurationXmlTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AFileOfHandlers_ReachesTheRoutesOfAnotherFile()
    {
        // The layout a container-level <onException> no longer serves: one file of handlers for all the others.
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <routeConfiguration>
                <onException exceptions="System.InvalidOperationException" handled="true">
                  <to uri="mock://xml-cfg-rescued"/>
                </onException>
              </routeConfiguration>
            </routes>
            """, "handlers.route.xml");
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-cfg-orders">
                <from uri="direct://xml-cfg-orders"/>
                <throwException type="System.InvalidOperationException" message="boom"/>
              </route>
            </routes>
            """, "orders.route.xml");
        await _context.Start();

        await _context.SendBody("direct://xml-cfg-orders", "order-1");

        await _context.Mock("mock://xml-cfg-rescued").ExpectBodies("order-1").AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task InContextXml_IsTheContextsSharedErrorHandling()
    {
        _context.AddXmlContextFromContent("""
            <context xmlns="urn:redb:route:1.0">
              <routeConfiguration>
                <onException exceptions="System.InvalidOperationException" handled="true">
                  <to uri="mock://xml-cfg-context"/>
                </onException>
              </routeConfiguration>
            </context>
            """, "context.xml");
        _context.AddRoutes(r => r.From("direct://xml-cfg-code").Process(_ => throw new InvalidOperationException("boom")));
        await _context.Start();

        await _context.SendBody("direct://xml-cfg-code", "from-code");

        await _context.Mock("mock://xml-cfg-context").ExpectBodies("from-code").AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task AnId_IsForTheRoutesThatNameIt()
    {
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <routeConfiguration id="audit">
                <onException exceptions="System.InvalidOperationException" handled="true">
                  <to uri="mock://xml-cfg-audit"/>
                </onException>
              </routeConfiguration>
              <route id="xml-cfg-named" routeConfigurationId="audit">
                <from uri="direct://xml-cfg-named"/>
                <throwException type="System.InvalidOperationException" message="boom"/>
              </route>
              <route id="xml-cfg-plain">
                <from uri="direct://xml-cfg-plain"/>
                <throwException type="System.InvalidOperationException" message="boom"/>
              </route>
            </routes>
            """, "<routes>");
        await _context.Start();

        await _context.SendBody("direct://xml-cfg-named", "named");
        var plain = async () => await _context.SendBody("direct://xml-cfg-plain", "plain");

        await plain.Should().ThrowAsync<InvalidOperationException>("the route named no configuration");
        await _context.Mock("mock://xml-cfg-audit").ExpectBodies("named").AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public void TheDefinitionsItBuilds()
    {
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <routeConfiguration id="retry">
                <onException exceptions="System.IO.IOException" handled="true" maximumRedeliveries="3"/>
                <onException exceptions="System.TimeoutException"/>
              </routeConfiguration>
              <route id="xml-cfg-shape" routeConfigurationId="retry,audit*">
                <from uri="direct://xml-cfg-shape"/>
              </route>
            </routes>
            """, "<routes>");

        var builder = _context.RouteBuilders.Single();
        var configuration = builder.RouteConfigurations.Should().ContainSingle().Subject;
        configuration.Id.Should().Be("retry");
        configuration.ExceptionDefinitions.Select(d => d.ExceptionTypes.Single())
            .Should().Equal(typeof(IOException), typeof(TimeoutException));
        builder.ExceptionDefinitions.Should().BeEmpty("a configuration's handlers are not the builder's own");
        builder.Definitions.Single().GetRouteConfigurationId().Should().Be("retry,audit*");
    }

    [Theory]
    [InlineData("""<routeConfiguration><setBody value="x"/></routeConfiguration>""")]
    [InlineData("""<routeConfiguration/>""")]
    [InlineData("""<routeConfiguration id=""><onException exceptions="System.Exception"/></routeConfiguration>""")]
    public void WhatIsNotAConfiguration_IsAPositionedError(string element)
    {
        var act = () => _context.AddXmlRoutesFromContent(
            $"""<routes xmlns="urn:redb:route:1.0">{element}</routes>""", "<routes>");

        act.Should().Throw<XmlRouteException>();
        _context.RouteBuilders.Should().BeEmpty("a document with errors adds nothing");
    }

    [Fact]
    public void InARoute_IsAPositionedError()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-cfg-misplaced">
                <from uri="direct://xml-cfg-misplaced"/>
                <routeConfiguration>
                  <onException exceptions="System.Exception"/>
                </routeConfiguration>
              </route>
            </routes>
            """, "<routes>");

        act.Should().Throw<XmlRouteException>();
    }

    [Fact]
    public void AContextXmlWithAnError_AddsNoConfiguration()
    {
        var act = () => _context.AddXmlContextFromContent("""
            <context xmlns="urn:redb:route:1.0">
              <routeConfiguration>
                <onException exceptions="No.Such.Exception" handled="true"/>
              </routeConfiguration>
            </context>
            """, "context.xml");

        act.Should().Throw<XmlRouteException>();
        _context.RouteBuilders.Should().BeEmpty();
    }

    [Fact]
    public void TheGeneratedCode_DeclaresTheConfigurationOnce_AndNamesItOnTheRoute()
    {
        var code = XmlCodeGenerator.Generate(System.Xml.Linq.XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <routeConfiguration id="retry">
                <onException exceptions="System.IO.IOException" handled="true"/>
                <onException exceptions="System.TimeoutException"/>
              </routeConfiguration>
              <routeConfiguration>
                <onException exceptions="System.Exception"/>
              </routeConfiguration>
              <route id="xml-cfg-code" routeConfigurationId="retry">
                <from uri="direct://xml-cfg-code"/>
              </route>
            </routes>
            """), "Configured", "Tests.Generated", sourceName: "<inline>");

        code.Should().Contain("= RouteConfiguration(\"retry\");");
        code.Should().Contain("= RouteConfiguration();");
        System.Text.RegularExpressions.Regex.Matches(code, @"RouteConfiguration\(""retry""\)").Should().HaveCount(1,
            "an id is unique: both handlers hang on the one configuration");
        System.Text.RegularExpressions.Regex.Matches(code, @"configuration\d+\.OnException\(").Should().HaveCount(3);
        code.Should().Contain(".RouteConfigurationId(\"retry\");");
    }

    [Fact]
    public void TheSchema_KnowsTheElementAndTheAttribute()
    {
        var schema = XmlRouteSchema.Generate(ElementRegistry.CreateDefault()).ToString();

        schema.Should().Contain("name=\"routeConfiguration\"");
        schema.Should().Contain("ref=\"r:routeConfiguration\"");
        schema.Should().Contain("name=\"routeConfigurationId\"");

        // And it validates: a document with the element and the attribute has no schema error.
        XmlRouteSchema.Validate(System.Xml.Linq.XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <routeConfiguration id="retry">
                <onException exceptions="System.IO.IOException"/>
              </routeConfiguration>
              <route id="xml-cfg-schema" routeConfigurationId="retry">
                <from uri="direct://xml-cfg-schema"/>
              </route>
            </routes>
            """, System.Xml.Linq.LoadOptions.SetLineInfo), XmlRouteSchema.CoreSet).Should().BeEmpty();
    }
}

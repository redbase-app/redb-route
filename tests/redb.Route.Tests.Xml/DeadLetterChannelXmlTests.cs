using FluentAssertions;
using redb.Route.Core;
using redb.Route.ErrorHandling;
using redb.Route.TestKit;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>
/// <c>&lt;deadLetterChannel uri="..."/&gt;</c> in <c>context.xml</c> — the context's dead letter channel, Apache Camel's
/// <c>errorHandlerRef</c> on <c>&lt;camelContext&gt;</c>: a context-level element only, and one error handler per context.
/// </summary>
public class DeadLetterChannelXmlTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task InContext_EveryRouteSendsItsUntakenFailureThere()
    {
        _context.AddXmlContextFromContent("""
            <context xmlns="urn:redb:route:1.0">
              <deadLetterChannel uri="mock://xml-dlc-errors"/>
            </context>
            """, "<context>");
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-dlc-orders">
                <from uri="direct://xml-dlc-orders"/>
                <throwException type="System.InvalidOperationException" message="boom"/>
              </route>
            </routes>
            """, "<routes>");
        await _context.Start();

        _context.ErrorHandler.Should().BeOfType<DeadLetterErrorHandler>()
            .Which.DeadLetterUri.Should().Be("mock://xml-dlc-errors");
        try { await _context.SendBody("direct://xml-dlc-orders", "x"); }
        catch (InvalidOperationException) { }

        await _context.Mock("mock://xml-dlc-errors").ExpectBodies("x").AssertIsSatisfiedAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void InARoute_IsAPositionedError()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="xml-dlc-misplaced">
                <from uri="direct://xml-dlc-misplaced"/>
                <deadLetterChannel uri="mock://x"/>
              </route>
            </routes>
            """, "<routes>");

        act.Should().Throw<XmlRouteException>();
    }

    [Fact]
    public void OverAnErrorHandlerAlreadySet_IsAnError()
    {
        _context.ErrorHandler = new DeadLetterErrorHandler(_context, "mock://first");

        var act = () => _context.AddXmlContextFromContent("""
            <context xmlns="urn:redb:route:1.0">
              <deadLetterChannel uri="mock://second"/>
            </context>
            """, "<context>");

        act.Should().Throw<XmlRouteException>().WithMessage("*already has an error handler*");
    }
}

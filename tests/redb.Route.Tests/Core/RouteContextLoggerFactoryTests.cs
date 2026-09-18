using FluentAssertions;
using Microsoft.Extensions.Logging;
using redb.Route.Components;
using redb.Route.Core;
using Xunit;

namespace redb.Route.Tests.Core;

/// <summary>
/// A logger factory registered after the context is constructed must actually reach components — otherwise
/// consumer failures pass with no log line. Covers back-fill onto already-registered components and
/// forward-fill onto components added later.
/// </summary>
public class RouteContextLoggerFactoryTests
{
    [Fact]
    public void AddService_LoggerFactoryAfterConstruction_BackfillsExistingComponentLoggers()
    {
        var ctx = new RouteContext(); // constructed with no logger factory
        ctx.GetComponent<DirectComponent>("direct")!.Logger.Should().BeNull("no factory at construction");

        using var factory = LoggerFactory.Create(_ => { });
        ctx.AddService(typeof(ILoggerFactory), factory);

        ctx.GetComponent<DirectComponent>("direct")!.Logger
            .Should().NotBeNull("a factory registered after construction must reach built-in components");
        ctx.GetService<ILogger>().Should().NotBeNull("the context adopts the late-registered factory");
    }

    [Fact]
    public void AddComponent_AfterLoggerFactoryRegistered_GetsLogger()
    {
        var ctx = new RouteContext();
        using var factory = LoggerFactory.Create(_ => { });
        ctx.AddService(typeof(ILoggerFactory), factory);

        var comp = new DirectComponent();
        ctx.AddComponent(comp);

        comp.Logger.Should().NotBeNull("components added after the factory is registered get a logger");
    }
}

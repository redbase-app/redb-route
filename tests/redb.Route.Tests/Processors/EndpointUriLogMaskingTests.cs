using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Processors.LoadBalancer;
using redb.Route.TestKit;
using redb.Route.Tests.Cluster;

namespace redb.Route.Tests.Processors;

/// <summary>
/// An endpoint URI that a processor resolves per message or holds from configuration is logged masked: a password in
/// the user-info (<c>user:pass@</c>) or in a <c>password=</c> parameter never reaches the log or an error text.
/// </summary>
public class EndpointUriLogMaskingTests
{
    private const string Secret = "s3cret-XYZ";
    private const string UserInfoUri = "mock://user:" + Secret + "@target";

    private static (CapturingLoggerProvider Capture, ILoggerFactory Factory) Logging()
    {
        var capture = new CapturingLoggerProvider();
        return (capture, LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace)));
    }

    private static void ShouldNotLeak(CapturingLoggerProvider capture, string expectedFragment)
    {
        var messages = capture.Entries.Select(e => e.Message).ToList();
        messages.Should().Contain(m => m.Contains(expectedFragment, StringComparison.Ordinal),
            "the processor logged the line under test");
        messages.Should().NotContain(m => m.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DynamicRouter_HopLine_IsMasked()
    {
        var (capture, factory) = Logging();
        using (factory)
        {
            await using var context = new RouteContext(loggerFactory: factory);
            var hops = 0;
            context.AddRoutes(r => r.From("direct:dr").DynamicRouter(_ => hops++ == 0 ? UserInfoUri : null));
            await context.Start();
            await context.SendBody("direct:dr", "x");
        }
        ShouldNotLeak(capture, "DynamicRouter: hop");
    }

    [Fact]
    public async Task RoutingSlip_HopAndInvalidEndpointLines_AreMasked()
    {
        var (capture, factory) = Logging();
        using (factory)
        {
            await using var context = new RouteContext(loggerFactory: factory);
            context.AddRoutes(r => r.From("direct:slip").RoutingSlip(
                _ => new[] { "nosuchscheme://user:" + Secret + "@x?password=" + Secret, UserInfoUri },
                ignoreInvalidEndpoints: true));
            await context.Start();
            await context.SendBody("direct:slip", "x");
        }
        ShouldNotLeak(capture, "Routing Slip: hop");
        capture.Entries.Should().Contain(e => e.Message.StartsWith("Routing Slip: ignoring invalid endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScatterGather_ResponseLine_IsMasked()
    {
        var (capture, factory) = Logging();
        using (factory)
        {
            await using var context = new RouteContext(loggerFactory: factory);
            context.AddRoutes(r => r.From("direct:sg").ScatterGather((_, latest) => latest, UserInfoUri));
            await context.Start();
            await context.SendBody("direct:sg", "x");
        }
        ShouldNotLeak(capture, "Scatter-gather: received response from");
    }

    [Fact]
    public async Task LoadBalancer_SentLine_IsMasked()
    {
        var (capture, factory) = Logging();
        using (factory)
        {
            await using var context = new RouteContext(loggerFactory: factory);
            context.AddRoutes(r => r.From("direct:lb").LoadBalance(new RoundRobinStrategy(), UserInfoUri));
            await context.Start();
            await context.SendBody("direct:lb", "x");
        }
        ShouldNotLeak(capture, "Load balancer: sent to");
    }

    [Fact]
    public async Task DeadLetterChannel_Lines_AreMasked()
    {
        var (capture, factory) = Logging();
        using (factory)
        {
            await using var context = new RouteContext(loggerFactory: factory);
            await context.Start();
            var failing = new DelegateProcessor(_ => throw new InvalidOperationException("boom"));
            var dlc = new DeadLetterChannelProcessor(context, failing, UserInfoUri + "?password=" + Secret, rethrow: false,
                logger: factory.CreateLogger<DeadLetterChannelProcessor>());
            await dlc.Process(new Exchange(new Message("x")));
        }
        ShouldNotLeak(capture, "Dead-lettering exchange to");
    }

    [Fact]
    public void ConfigurationErrors_DoNotCarryTheSecret()
    {
        var badFormat = () => EndpointUriParser.Parse("nocolon?password=" + Secret);
        badFormat.Should().Throw<ArgumentException>().Which.Message.Should().NotContain(Secret);

        var badWeight = () => new WeightedStrategy([(UserInfoUri, 0)]);
        badWeight.Should().Throw<ArgumentException>().Which.Message.Should().NotContain(Secret).And.Contain("must be positive");
    }
}

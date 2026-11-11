using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Core;

/// <summary>
/// At context start a route's consumer starts before its policy is asked. A policy that refuses marks the route
/// Errored, and the consumer that already started is stopped: an Errored route does not go on taking messages.
/// </summary>
public sealed class RoutePolicyStartFailureTests
{
    private sealed class RefusingPolicy : IRoutePolicy
    {
        public Task OnStart(IRouteContext context, CompiledRoute route, CancellationToken ct)
            => throw new InvalidOperationException("policy refuses");
    }

    [Fact]
    public async Task A_policy_that_refuses_at_context_start_stops_the_consumer_it_follows()
    {
        await using var context = new RouteContext(options: new RouteEngineOptions
        {
            ShutdownTimeout = TimeSpan.FromMilliseconds(500),
        });
        var consumer = Substitute.For<IConsumer>();
        consumer.Start(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        consumer.Stop(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        context.AddConsumer(consumer);
        context.AddRoute(new CompiledRoute("refused-at-start", "direct://refused-at-start", new RouteDefinition(),
            new PipelineProcessor(), consumer, Substitute.For<IEndpoint>()) { Policy = new RefusingPolicy() });

        await context.Start();

        context.GetRoute("refused-at-start")!.Status.Should().Be(RouteStatus.Errored);
        consumer.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IConsumer.Stop))
            .Should().Be(1, "an Errored route must not go on taking messages");
    }
}

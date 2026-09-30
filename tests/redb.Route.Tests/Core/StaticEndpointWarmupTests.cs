using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors.LoadBalancer;

namespace redb.Route.Tests.Core;

/// <summary>
/// The endpoint of a static target — <c>To</c>, <c>WireTap</c>, a load balancer's targets — is created when the route
/// starts, as Camel resolves a static <c>to(...)</c> at route startup: an unknown scheme, an unknown URI parameter or a
/// missing <c>{{key}}</c> stops the start instead of failing the first message hours later. A dynamic target
/// (<c>ToD</c>, a recipient list) stays lazy, and no producer is created or started before a message needs it — unlike
/// Camel, which starts its producers too: that would open a connection to every broker at startup.
/// </summary>
public class StaticEndpointWarmupTests
{
    private sealed class CountingComponent : ComponentBase
    {
        public int Producers;
        public override string Scheme => "counting";
        public override IEndpoint CreateEndpoint(EndpointUri uri) => new CountingEndpoint(uri, this);

        private sealed class CountingEndpoint(EndpointUri uri, CountingComponent owner) : IEndpoint
        {
            public EndpointUri Uri { get; } = uri;
            public IComponent Component => owner;
            public IProducer CreateProducer()
            {
                Interlocked.Increment(ref owner.Producers);
                throw new InvalidOperationException("no producer is created at startup");
            }
            public IConsumer CreateConsumer(IProcessor processor) => throw new NotSupportedException();
            public Task Start(CancellationToken ct = default) => Task.CompletedTask;
            public Task Stop(CancellationToken ct = default) => Task.CompletedTask;
        }
    }

    private static RouteContext Context(out CountingComponent counting)
    {
        var context = new RouteContext($"warmup-{Guid.NewGuid():N}");
        context.AddComponent(new DirectComponent());
        context.AddComponent(counting = new CountingComponent());
        return context;
    }

    private static bool Created(RouteContext context, string fragment)
        => context.GetEndpoints().Any(e => e.Uri.NormalizedKey.Contains(fragment, StringComparison.Ordinal));

    [Fact]
    public async Task A_To_with_an_unknown_scheme_stops_the_start()
    {
        await using var context = Context(out _);
        context.AddRoutes(r => r.From("direct://warm-unknown").To("nosuchscheme://target"));

        var act = () => context.Start();

        await act.Should().ThrowAsync<Exception>().WithMessage("*nosuchscheme*");
    }

    [Fact]
    public async Task A_To_with_an_unknown_URI_parameter_stops_the_start()
    {
        await using var context = Context(out _);
        context.AddComponent(new LogComponent());
        context.AddRoutes(r => r.From("direct://warm-param").To("log:warm-target?noSuchOption=1"));

        var act = () => context.Start();

        await act.Should().ThrowAsync<Exception>().WithMessage("*noSuchOption*");
    }

    [Fact]
    public async Task A_To_with_a_missing_configuration_placeholder_stops_the_start()
    {
        await using var context = Context(out _);
        context.AddRoutes(r => r.From("direct://warm-placeholder").To("direct://{{warmup.missing.key}}"));

        var act = () => context.Start();

        await act.Should().ThrowAsync<Exception>().WithMessage("*warmup.missing.key*");
    }

    [Fact]
    public async Task Static_targets_exist_after_the_start_without_a_message_and_no_producer_was_made()
    {
        await using var context = Context(out var counting);
        context.AddRoutes(r => r.From("direct://warm-static")
            .To("counting://to-target")
            .WireTap("counting://tap-target")
            .LoadBalance(new RoundRobinStrategy(), "counting://lb-a", "counting://lb-b"));

        await context.Start();

        Created(context, "to-target").Should().BeTrue();
        Created(context, "tap-target").Should().BeTrue();
        Created(context, "lb-a").Should().BeTrue();
        Created(context, "lb-b").Should().BeTrue();
        counting.Producers.Should().Be(0, "a producer is created when a message needs it");
    }

    [Fact]
    public async Task Static_enrich_poll_enrich_and_scatter_gather_targets_exist_after_the_start()
    {
        await using var context = Context(out var counting);
        context.AddRoutes(r => r.From("direct://warm-enrich")
            .Enrich("counting://enrich-target")
            .PollEnrich("counting://poll-target")
            .ScatterGather(static (acc, next) => next, "counting://sg-a", "counting://sg-b"));

        await context.Start();

        Created(context, "enrich-target").Should().BeTrue();
        Created(context, "poll-target").Should().BeTrue();
        Created(context, "sg-a").Should().BeTrue();
        Created(context, "sg-b").Should().BeTrue();
        counting.Producers.Should().Be(0);
    }

    [Fact]
    public async Task Dynamic_enrich_and_poll_enrich_stay_lazy()
    {
        await using var context = Context(out _);
        context.AddRoutes(r => r.From("direct://warm-lazy-enrichers")
            .Enrich(_ => "counting://enrich-dynamic")
            .PollEnrich(_ => "counting://poll-dynamic"));

        await context.Start();

        Created(context, "enrich-dynamic").Should().BeFalse();
        Created(context, "poll-dynamic").Should().BeFalse();
    }

    [Fact]
    public async Task Dynamic_targets_stay_lazy()
    {
        await using var context = Context(out _);
        context.AddRoutes(r => r.From("direct://warm-dynamic")
            .ToD("counting://tod-${header.name}")
            .RecipientList(_ => ["counting://recipient-target"]));

        await context.Start();

        Created(context, "tod-").Should().BeFalse();
        Created(context, "recipient-target").Should().BeFalse();
    }
}

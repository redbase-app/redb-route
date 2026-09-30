using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// A producer the template creates is stopped — flushed and closed — when the template stops or is disposed, and when
/// the context stops with the template still open, as in Camel, where stopping a <c>ProducerTemplate</c> stops its
/// producer cache. The template used to drop its producers from the cache and nothing more: a Kafka producer behind it
/// lost what it had not sent yet when the host stopped.
/// </summary>
public class ProducerTemplateLifecycleTests
{
    private sealed class CountingProducer : IProducer
    {
        public int Stops;
        public Task Start(CancellationToken ct = default) => Task.CompletedTask;
        public Task Stop(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Stops);
            return Task.CompletedTask;
        }
        public Task Process(IExchange exchange, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CountingEndpoint(EndpointUri uri, IComponent component, List<CountingProducer> created) : IEndpoint
    {
        public EndpointUri Uri { get; } = uri;
        public IComponent Component { get; } = component;
        public IProducer CreateProducer()
        {
            var producer = new CountingProducer();
            lock (created) created.Add(producer);
            return producer;
        }
        public IConsumer CreateConsumer(IProcessor processor) => throw new NotSupportedException();
        public Task Start(CancellationToken ct = default) => Task.CompletedTask;
        public Task Stop(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CountingComponent : ComponentBase
    {
        public List<CountingProducer> Created { get; } = [];
        public override string Scheme => "counting";
        public override IEndpoint CreateEndpoint(EndpointUri uri) => new CountingEndpoint(uri, this, Created);
    }

    private static (RouteContext Context, CountingComponent Component) Context()
    {
        var context = new RouteContext($"pt-lifecycle-{Guid.NewGuid():N}");
        var component = new CountingComponent();
        context.AddComponent(component);
        return (context, component);
    }

    [Fact]
    public async Task Disposing_the_template_stops_its_producers()
    {
        var (context, component) = Context();
        var template = new ProducerTemplate(context);
        template.Start();
        await template.SendAsync("counting:a", "x");
        await template.SendAsync("counting:b", "x");

        template.Dispose();

        component.Created.Should().HaveCount(2).And.OnlyContain(p => p.Stops == 1);
        await context.DisposeAsync();
    }

    [Fact]
    public async Task Stopping_the_template_stops_its_producers_and_a_restart_makes_new_ones()
    {
        var (context, component) = Context();
        var template = new ProducerTemplate(context);
        template.Start();
        await template.SendAsync("counting:a", "x");

        template.Stop();
        component.Created.Single().Stops.Should().Be(1);

        template.Start();
        await template.SendAsync("counting:a", "x");
        component.Created.Should().HaveCount(2, "a stopped producer is not reused");
        template.Dispose();
        await context.DisposeAsync();
    }

    [Fact]
    public async Task Stopping_the_context_stops_the_producers_of_a_template_still_open()
    {
        var (context, component) = Context();
        await context.Start();
        var template = new ProducerTemplate(context);
        template.Start();
        await template.SendAsync("counting:a", "x");

        await context.Stop();

        component.Created.Single().Stops.Should().Be(1, "the host stopping must flush what the template sent");
        template.Dispose();
        component.Created.Single().Stops.Should().Be(1, "a producer is stopped once");
        await context.DisposeAsync();
    }

    [Fact]
    public async Task A_disposed_template_leaves_nothing_for_the_context_to_stop()
    {
        var (context, component) = Context();
        await context.Start();
        var template = new ProducerTemplate(context);
        template.Start();
        await template.SendAsync("counting:a", "x");
        template.Dispose();

        await context.Stop();

        component.Created.Single().Stops.Should().Be(1);
        await context.DisposeAsync();
    }
}

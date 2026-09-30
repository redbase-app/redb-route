using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;

namespace redb.Route.Tests.Processors;

/// <summary>
/// An aggregation group outlives the calls that fed it, so it holds exchanges of its own, as Camel copies an exchange
/// into its aggregation repository: the body of a message waiting in a group stays open until the group is done, the
/// completed group runs on in a DI scope of its own, and everything the group held is released once, after it has gone
/// on down the route. The aggregator used to keep the caller's exchange itself, which the caller disposes as soon as
/// the aggregator returns: a group completing later went on with closed streams and a released scope.
/// </summary>
public class AggregatorOwnershipTests
{
    private sealed class TrackedStream() : MemoryStream("payload"u8.ToArray())
    {
        public int Disposals;
        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref Disposals);
            base.Dispose(disposing);
        }
    }

    private sealed class ScopedProbe
    {
        public bool Used;
    }

    private sealed record Outcome(int FirstBodyDisposalsWhenCompleted, bool ScopeWorked);

    private static async Task<(RouteContext Context, ConcurrentQueue<Outcome> Completed)> Route()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedProbe>();
        var provider = services.BuildServiceProvider();
        var context = new RouteContext($"aggregator-owner-{Guid.NewGuid():N}");
        context.SetServiceProvider(provider);
        context.AddComponent(new DirectComponent());
        var completed = new ConcurrentQueue<Outcome>();
        context.AddRoutes(r => r.From("direct://owner-in")
            .Aggregate(_ => "g", static (acc, next) =>
                {
                    var bodies = acc.In.Body as List<object?> ?? [acc.In.Body];
                    bodies.Add(next.In.Body);
                    acc.In.Body = bodies;
                    return acc;
                },
                e => e.In.Body is List<object?> { Count: 2 })
            .Process(e =>
            {
                var first = (TrackedStream)((List<object?>)e.In.Body!)[0]!;
                var scopeWorked = false;
                try
                {
                    if (e.ServiceProvider is { } services)
                    {
                        services.GetRequiredService<ScopedProbe>().Used = true;
                        scopeWorked = true;
                    }
                }
                catch (ObjectDisposedException) { }   // a released scope: the assertion reports it
                completed.Enqueue(new Outcome(first.Disposals, scopeWorked));
            }));
        await context.Start();
        return (context, completed);
    }

    private static async Task Send(RouteContext context, object body)
    {
        var template = new ProducerTemplate(context);
        template.Start();
        await template.SendAsync("direct://owner-in", body);   // the caller disposes its exchange right after
        await template.DisposeAsync();
    }

    [Fact]
    public async Task A_body_waiting_in_a_group_stays_open_until_the_group_is_done_and_is_then_disposed_once()
    {
        var (context, completed) = await Route();
        var first = new TrackedStream();

        await Send(context, first);
        await Send(context, "second");

        completed.Single().FirstBodyDisposalsWhenCompleted.Should().Be(0, "the group still carries it");
        first.Disposals.Should().Be(1, "and releases it once it has gone on down the route");
        await context.DisposeAsync();
    }

    [Fact]
    public async Task A_completed_group_runs_on_in_a_live_DI_scope()
    {
        var (context, completed) = await Route();

        await Send(context, new TrackedStream());
        await Send(context, "second");

        completed.Single().ScopeWorked.Should().BeTrue();
        await context.DisposeAsync();
    }
}

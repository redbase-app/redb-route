using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Tests.Cluster;

namespace redb.Route.Tests.Processors;

/// <summary>
/// An aggregation group still open when the context stops is completed (Camel <c>forceCompletionOnStop</c>) once the
/// consumers have stopped — no more arrivals, producers still alive for what the completion sends on — or, without the
/// option, dropped with a warning naming how many, as Camel drops an in-memory repository on stop. It used to be dropped
/// without a word: the next start compiles a new aggregator and the old one's groups were simply gone.
/// </summary>
public class AggregatorStopTests
{
    private static async Task<(RouteContext Context, ConcurrentQueue<object?> Completed, CapturingLoggerProvider Logs)> Route(bool force)
    {
        var logs = new CapturingLoggerProvider();
        var context = new RouteContext($"aggregator-stop-{Guid.NewGuid():N}");
        context.AddService(typeof(ILoggerFactory), LoggerFactory.Create(b => b.AddProvider(logs)));
        context.AddComponent(new DirectComponent());
        var completed = new ConcurrentQueue<object?>();
        context.AddRoutes(r =>
        {
            var aggregate = r.From("direct://aggregate-in")
                .Aggregate(e => e.In.GetHeader<string>("group") ?? "", static (acc, next) =>
                    {
                        acc.In.Body = $"{acc.In.Body}+{next.In.Body}";
                        return acc;
                    },
                    e => ((string?)e.In.Body)?.Split('+').Length >= 3);
            if (force) aggregate.ForceCompletionOnStop();
            aggregate.Process(e => completed.Enqueue(e.In.Body));
        });
        await context.Start();
        return (context, completed, logs);
    }

    private static async Task Send(RouteContext context, string body, string group)
    {
        var template = new ProducerTemplate(context);
        template.Start();
        var message = new Message(body);
        message.Headers["group"] = group;
        await template.SendAsync("direct://aggregate-in", message);
        await template.DisposeAsync();
    }

    [Fact]
    public async Task With_forceCompletionOnStop_an_open_group_completes_when_the_context_stops()
    {
        var (context, completed, _) = await Route(force: true);
        await Send(context, "a", "g1");
        await Send(context, "b", "g1");
        completed.Should().BeEmpty("the group is not complete yet");

        await context.Stop();

        completed.Should().Equal("a+b");
        await context.DisposeAsync();
    }

    [Fact]
    public async Task Without_it_an_open_group_is_dropped_on_stop_with_a_warning_naming_how_many()
    {
        var (context, completed, logs) = await Route(force: false);
        await Send(context, "a", "g1");
        await Send(context, "b", "g2");

        await context.Stop();

        completed.Should().BeEmpty();
        logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("2 aggregation group"));
        await context.DisposeAsync();
    }
}

using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Processors;

/// <summary>
/// <c>OnRedelivery</c>, <c>OnExceptionOccurred</c> and <c>OnPrepareFailure</c> take a processor beside the delegate, as
/// Apache Camel's do: the markup form of a route can only name a bean from the registry, and a bean is an
/// <see cref="IProcessor"/>. Both places a handler can be declared — in the route and on the builder — pass them on.
/// </summary>
public class OnExceptionCallbackProcessorTests
{
    private sealed class Counting(List<string> calls, string name) : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            calls.Add(name);
            return Task.CompletedTask;
        }
    }

    private static async Task<IProducer> Start(RouteContext context, string uri)
    {
        await context.Start();
        var producer = context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        return producer;
    }

    [Fact]
    public async Task A_route_level_handler_calls_all_three_processors()
    {
        var calls = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://cb-route")
            .OnException<InvalidOperationException>()
                .MaximumRedeliveries(2)
                .RedeliveryDelay(TimeSpan.FromMilliseconds(1))
                .Handled()
                .OnExceptionOccurred(new Counting(calls, "occurred"))
                .OnRedelivery(new Counting(calls, "redelivery"))
                .OnPrepareFailure(new Counting(calls, "prepare"))
            .End()
            .Process(_ => throw new InvalidOperationException("boom")));
        var producer = await Start(context, "direct://cb-route");

        await producer.Process(new Exchange(new Message("x")));

        calls.Should().Equal(
            "occurred", "redelivery",   // first failure, then the first retry
            "occurred", "redelivery",   // second failure, then the second retry
            "occurred", "prepare");     // third failure: retries exhausted, the handler is about to run
    }

    [Fact]
    public async Task A_builder_level_handler_calls_all_three_processors()
    {
        var calls = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(b =>
        {
            b.OnException<InvalidOperationException>()
                .MaximumRedeliveries(1)
                .RedeliveryDelay(TimeSpan.FromMilliseconds(1))
                .Handled()
                .OnExceptionOccurred(new Counting(calls, "occurred"))
                .OnRedelivery(new Counting(calls, "redelivery"))
                .OnPrepareFailure(new Counting(calls, "prepare"));

            b.From("direct://cb-builder")
                .Process(_ => throw new InvalidOperationException("boom"));
        });
        var producer = await Start(context, "direct://cb-builder");

        await producer.Process(new Exchange(new Message("x")));

        calls.Should().Equal("occurred", "redelivery", "occurred", "prepare");
    }

    [Fact]
    public async Task The_delegate_form_still_runs_beside_the_processor_form()
    {
        var calls = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://cb-both")
            .OnException<InvalidOperationException>()
                .Handled()
                .OnExceptionOccurred(_ => calls.Add("delegate"))
                .OnExceptionOccurred(new Counting(calls, "processor"))
            .End()
            .Process(_ => throw new InvalidOperationException("boom")));
        var producer = await Start(context, "direct://cb-both");

        await producer.Process(new Exchange(new Message("x")));

        calls.Should().Equal("delegate", "processor");
    }
}

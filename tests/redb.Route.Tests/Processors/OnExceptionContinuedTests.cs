using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Processors;

/// <summary>
/// <c>OnException ... Continued()</c> is Apache Camel's <c>continued(true)</c>: the failure is suppressed and routing
/// picks up at the step after the one that failed, where <c>Handled()</c> ends the route. Camel gets it for free — its
/// error handler wraps every step, so the pipeline simply moves on; here the handler wraps the route, so every pipeline
/// the failure unwound through remembers where it stopped and the handler replays the rest.
/// </summary>
public class OnExceptionContinuedTests
{
    private static async Task<IProducer> Start(RouteContext context, string uri)
    {
        await context.Start();
        var producer = context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        return producer;
    }

    [Fact]
    public async Task Continued_picks_the_route_up_after_the_step_that_failed()
    {
        var steps = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://continued-plain")
            .OnException<InvalidOperationException>()
                .Continued()
                .Process(_ => steps.Add("handler"))
            .End()
            .Process(_ => steps.Add("before"))
            .Process(_ => throw new InvalidOperationException("boom"))
            .Process(_ => steps.Add("after")));
        var producer = await Start(context, "direct://continued-plain");

        var exchange = new Exchange(new Message("x"));
        await producer.Process(exchange);

        steps.Should().Equal("before", "handler", "after");
        exchange.Exception.Should().BeNull("a continued failure is suppressed, as a handled one is");
        exchange.EndedInFailure().Should().BeFalse();
    }

    [Fact]
    public async Task Handled_ends_the_route_at_the_step_that_failed()
    {
        var steps = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://handled-plain")
            .OnException<InvalidOperationException>()
                .Handled()
                .Process(_ => steps.Add("handler"))
            .End()
            .Process(_ => throw new InvalidOperationException("boom"))
            .Process(_ => steps.Add("after")));
        var producer = await Start(context, "direct://handled-plain");

        await producer.Process(new Exchange(new Message("x")));

        steps.Should().Equal("handler");
    }

    [Fact]
    public async Task Continued_picks_up_inside_the_scope_that_failed_and_then_outside_it()
    {
        var steps = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://continued-nested")
            .OnException<InvalidOperationException>()
                .Continued()
            .End()
            .Filter(_ => true, b => b
                .Process(_ => throw new InvalidOperationException("boom"))
                .Process(_ => steps.Add("rest of the scope")))
            .Process(_ => steps.Add("after the scope")));
        var producer = await Start(context, "direct://continued-nested");

        await producer.Process(new Exchange(new Message("x")));

        steps.Should().Equal("rest of the scope", "after the scope");
    }

    [Fact]
    public async Task Continued_does_not_replay_a_block_whose_transaction_rolled_back()
    {
        var steps = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://continued-tx")
            .OnException<InvalidOperationException>()
                .Continued()
            .End()
            .Transacted()
                .Process(_ => steps.Add("work"))
                .Process(_ => throw new InvalidOperationException("boom"))
                .Process(_ => steps.Add("rest of the block"))
            .End()
            .Process(_ => steps.Add("after the block")));
        var producer = await Start(context, "direct://continued-tx");

        await producer.Process(new Exchange(new Message("x")));

        steps.Should().Equal("work", "after the block");
    }

    [Fact]
    public async Task A_builder_level_handler_resumes_the_route_too()
    {
        var steps = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(b =>
        {
            b.OnException<InvalidOperationException>()
                .Continued()
                .Process(_ => steps.Add("handler"));

            b.From("direct://continued-builder")
                .Process(_ => throw new InvalidOperationException("boom"))
                .Process(_ => steps.Add("after"));
        });
        var producer = await Start(context, "direct://continued-builder");

        await producer.Process(new Exchange(new Message("x")));

        // A handler declared on the builder wraps the route from the outside; the route's own pipeline still recorded
        // where it stopped, so the resume works from there too.
        steps.Should().Equal("handler", "after");
    }

    [Fact]
    public async Task A_failure_in_the_resumed_steps_goes_through_the_handlers_again()
    {
        var steps = new List<string>();
        await using var context = new RouteContext();
        context.AddRoutes(r => r
            .From("direct://continued-twice")
            .OnException<InvalidOperationException>()
                .Continued()
                .Process(_ => steps.Add("handler"))
            .End()
            .Process(_ => throw new InvalidOperationException("first"))
            .Process(_ => throw new InvalidOperationException("second"))
            .Process(_ => steps.Add("last")));
        var producer = await Start(context, "direct://continued-twice");

        await producer.Process(new Exchange(new Message("x")));

        steps.Should().Equal("handler", "handler", "last");
    }
}

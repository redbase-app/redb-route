using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Components.Bean;
using redb.Route.Core;
using redb.Route.Validation;
using redb.Route.Xslt;

namespace redb.Route.Tests.Core;

/// <summary>
/// A component declares the only side it has with <c>[EndpointRole]</c> on its class (Apache Camel's
/// <c>producerOnly</c> / <c>consumerOnly</c>); the context refuses the missing side in one message.
/// </summary>
public sealed class EndpointSidesTests
{
    [Fact]
    public void The_core_components_declare_their_only_side()
    {
        EndpointSides.Of(new LogComponent()).Should().Be(EndpointRole.Producer);
        EndpointSides.Of(new BeanComponent()).Should().Be(EndpointRole.Producer);
        EndpointSides.Of(new XsltComponent()).Should().Be(EndpointRole.Producer);
        EndpointSides.Of(new ValidatorComponent()).Should().Be(EndpointRole.Producer);
        EndpointSides.Of(new TimerComponent()).Should().Be(EndpointRole.Consumer);
        EndpointSides.Of(new DirectComponent()).Should().BeNull("direct has both sides");
        EndpointSides.Of(new SedaComponent()).Should().BeNull();
    }

    [Fact]
    public void The_message_names_the_scheme_its_missing_side_and_the_hint()
    {
        EndpointSides.MissingSide("smtp", EndpointRole.Producer, "Use imap: or pop3: to receive mail.")
            .Should().Be("smtp has no consumer side: a route cannot start from it (smtp is producer-only). " +
                         "Use imap: or pop3: to receive mail.");
        EndpointSides.MissingSide("timer", EndpointRole.Consumer, null)
            .Should().Be("timer has no producer side: a route cannot send to it (timer is consumer-only).");
    }

    [Fact]
    public async Task A_route_from_a_producer_only_scheme_is_refused_at_start()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r.From("log:x").To("mock:out"));

        var act = () => ctx.Start();

        (await act.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should()
            .StartWith("log has no consumer side: a route cannot start from it (log is producer-only).");
    }

    [Fact]
    public async Task A_static_target_on_a_consumer_only_scheme_fails_the_route_build()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r.From("direct:in")
            .Choice()
                .When(_ => false, branch => branch.To("timer:t"))
            .EndChoice());

        var act = () => ctx.Start();

        (await act.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should()
            .StartWith("timer has no producer side: a route cannot send to it (timer is consumer-only).");
    }

    [Fact]
    public async Task A_dynamic_target_on_a_consumer_only_scheme_is_refused_on_the_exchange()
    {
        await using var ctx = new RouteContext();
        ctx.AddRoutes(r => r.From("direct:in").ToD("timer:t"));
        await ctx.Start();
        var template = new ProducerTemplate(ctx);
        template.Start();
        var exchange = new Exchange(new Message("m"));

        Exception? thrown = null;
        try { await template.SendAsync("direct:in", exchange); }
        catch (Exception ex) { thrown = ex; }

        (thrown ?? exchange.Exception).Should().BeOfType<NotSupportedException>().Which.Message.Should()
            .StartWith("timer has no producer side: a route cannot send to it (timer is consumer-only).");
    }

    [Fact]
    public async Task A_producer_template_send_to_a_consumer_only_scheme_is_refused()
    {
        await using var ctx = new RouteContext();
        await ctx.Start();
        var template = new ProducerTemplate(ctx);
        template.Start();

        var act = () => template.SendAsync("timer:t", "m");

        (await act.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should()
            .StartWith("timer has no producer side");
    }

    [Fact]
    public async Task A_consumer_template_receive_from_a_producer_only_scheme_is_refused()
    {
        await using var ctx = new RouteContext();
        await ctx.Start();
        var template = new ConsumerTemplate(ctx);
        template.Start();

        var act = () => template.Receive("log:x", TimeSpan.FromMilliseconds(100));

        (await act.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should()
            .StartWith("log has no consumer side");
    }

    [Fact]
    public void A_component_with_both_sides_is_not_refused()
    {
        var endpoint = new DirectComponent().CreateEndpoint(EndpointUriParser.Parse("direct:a"));

        var act = () =>
        {
            EndpointSides.Refuse(endpoint, EndpointRole.Consumer);
            EndpointSides.Refuse(endpoint, EndpointRole.Producer);
        };

        act.Should().NotThrow();
    }
}

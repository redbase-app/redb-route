using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// <c>stats(target, metric)</c> — endpoint measurements as values in the expression language
/// (METRICS_IN_ROUTE_PLAN, П2), so a route can branch on them: turn traffic away from a degraded
/// endpoint, alarm on a cancellation storm, log its own counters without capturing the context.
/// Built on П1 (<see cref="IExchange.Context"/>); every failure is an authoring error and says so
/// loudly rather than routing messages on a silent null.
/// </summary>
[Collection("ExpressionResolver")]
public sealed class StatsFunctionTests
{
    private static async Task<IProducer> Started(RouteContext context, string uri)
    {
        await context.Start();
        var producer = context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        return producer;
    }

    // ── Value position, both engine branches through the template ──

    [Fact]
    public async Task Reads_a_named_endpoints_counter_in_a_template()
    {
        await using var context = new RouteContext();
        string? rendered = null;
        context.AddRoutes(r => r.From("direct:st-value").Process(e =>
            rendered = ExpressionResolver.ProcessTemplate("${stats('direct:st-value', 'messagesIn')}", e)));

        var producer = await Started(context, "direct:st-value");
        await producer.Process(new Exchange(new Message("m")));

        rendered.Should().Be("1", "the exchange being processed has already been counted in");
    }

    [Fact]
    public async Task Current_means_the_route_processing_the_exchange()
    {
        await using var context = new RouteContext();
        string? health = null;
        context.AddRoutes(r => r.From("direct:st-current").RouteId("st-current").Process(e =>
            health = ExpressionResolver.ProcessTemplate("${stats('current', 'health')}", e)));

        var producer = await Started(context, "direct:st-current");
        await producer.Process(new Exchange(new Message("m")));

        health.Should().Be("Healthy");
    }

    // ── Condition position: the point of the exercise ──

    [Fact]
    public async Task A_route_can_turn_traffic_away_from_a_degraded_target()
    {
        await using var context = new RouteContext();
        var reached = new List<string>();

        context.AddRoutes(r =>
        {
            r.From("direct:st-target").RouteId("st-target")
                .Process(e =>
                {
                    if (Equals(e.In.Body, "boom")) throw new InvalidOperationException("down");
                });
            r.From("direct:st-guarded")
                .Filter("stats('direct:st-target', 'errors') == 0")
                    .Process(_ => reached.Add("sent"))
                .EndFilter();
        });

        var guarded = await Started(context, "direct:st-guarded");
        var target = context.GetEndpoint("direct:st-target").CreateProducer();
        await target.Start();

        await guarded.Process(new Exchange(new Message("m")));

        var act = () => target.Process(new Exchange(new Message("boom")));
        await act.Should().ThrowAsync<InvalidOperationException>();

        await guarded.Process(new Exchange(new Message("m")));

        reached.Should().Equal("sent");
    }

    [Fact]
    public async Task The_interpreting_engine_branch_evaluates_stats_too()
    {
        // The format() lesson: a function that lives only in the compiled branch is a documented
        // lie. The template and Filter paths above compile; this drives AstNode.Evaluate directly.
        await using var context = new RouteContext();
        object? result = null;
        context.AddRoutes(r => r.From("direct:st-interp").RouteId("st-interp").Process(e =>
        {
            var tokens = new redb.Route.Expressions.Ast.Tokenizer("stats('current', 'messagesIn')").GetAllTokens();
            var ast = new redb.Route.Expressions.Ast.Parser(tokens).Parse();
            result = ast.Evaluate(e);
        }));

        var producer = await Started(context, "direct:st-interp");
        await producer.Process(new Exchange(new Message("m")));

        result.Should().Be(1L);
    }

    // ── Authoring errors are loud ──

    [Fact]
    public async Task An_unknown_metric_written_as_a_literal_fails_when_the_route_is_built()
    {
        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct:st-bad")
            .Filter("stats('current', 'erors') > 0")
                .Process(_ => { })
            .EndFilter());

        var act = () => context.Start();

        (await act.Should().ThrowAsync<Exception>())
            .WithMessage("*unknown statistic 'erors'*",
                "a typo in a metric name must fail the build with the list of known names, not the first message");
    }

    [Fact]
    public void An_exchange_that_never_entered_a_route_cannot_read_stats()
    {
        var act = () => ExpressionResolver.ProcessTemplate(
            "${stats('direct:x', 'errors')}", new Exchange(new Message("m")));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*carries no route context*");
    }
}

using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Validation;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Route-XML Ф1.1: step identity and label in message history. <c>Id("...")</c> replaces the
/// generated node id, <c>Description("...")</c> replaces the type-derived label; neither shifts
/// the generated ids of neighbouring nodes, and a duplicate step id fails route validation.
/// </summary>
public class StepIdentityTests
{
    private static async Task<IExchange> RunAsync(RouteContext context, string fromUri, IExchange exchange)
    {
        await context.Start();
        var producer = context.GetEndpoint(fromUri).CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        return exchange;
    }

    [Fact]
    public async Task StepId_BecomesNodeId_AndNeighbourCounterDoesNotShift()
    {
        await using var context = new RouteContext();
        context.AddRoutes(r =>
            r.From("direct://si-id").MessageHistory()
                .Process(_ => { }).Id("validate-input")
                .Process(_ => { }));

        var exchange = await RunAsync(context, "direct://si-id", new Exchange(new Message("x")));
        var entries = MessageHistory.GetEntries(exchange);

        entries.Should().HaveCount(2);
        entries[0].NodeId.Should().Be("validate-input");
        entries[0].Label.Should().Be("processAction", "Id() names the node, the label stays type-derived");
        // The named node still consumes the counter, so its neighbour keeps the id it had without Id().
        entries[1].NodeId.Should().Be("processAction2");
    }

    [Fact]
    public async Task StepDescription_ReplacesLabel_NodeIdUnchanged()
    {
        await using var context = new RouteContext();
        context.AddRoutes(r =>
            r.From("direct://si-descr").MessageHistory()
                .Process(_ => { }).Description("Validate the order")
                .Process(_ => { }));

        var exchange = await RunAsync(context, "direct://si-descr", new Exchange(new Message("x")));
        var entries = MessageHistory.GetEntries(exchange);

        entries.Should().HaveCount(2);
        entries[0].Label.Should().Be("Validate the order");
        entries[0].NodeId.Should().Be("processAction1", "Description() must not touch node identity");
        entries[1].NodeId.Should().Be("processAction2");
        entries[1].Label.Should().Be("processAction");
    }

    [Fact]
    public async Task IdAndDescription_Together_NameAndLabelTheSameNode()
    {
        await using var context = new RouteContext();
        context.AddRoutes(r =>
            r.From("direct://si-both").MessageHistory()
                .Process(_ => { }).Id("enrich-step").Description("Enrich with CRM data"));

        var exchange = await RunAsync(context, "direct://si-both", new Exchange(new Message("x")));
        var entry = MessageHistory.GetEntries(exchange).Should().ContainSingle().Subject;

        entry.NodeId.Should().Be("enrich-step");
        entry.Label.Should().Be("Enrich with CRM data");
    }

    [Fact]
    public async Task DuplicateStepId_FailsRouteValidation_AtStart()
    {
        await using var context = new RouteContext();
        context.AddRoutes(r =>
            r.From("direct://si-dup")
                .Process(_ => { }).Id("dup-step")
                .Process(_ => { }).Id("DUP-STEP"));

        var act = () => context.Start();

        (await act.Should().ThrowAsync<RouteValidationException>())
            .WithMessage("*Duplicate step id 'DUP-STEP'*");
    }

    [Fact]
    public async Task Description_BeforeAnyStep_DescribesTheRouteItself()
    {
        IRouteDefinition? captured = null;
        await using var context = new RouteContext();
        context.AddRoutes(r => captured = r.From("direct://si-route").Description("Order intake"));
        await context.Start(); // builder lambdas are deferred and run at Start()

        ((ProcessorDefinition)captured!).StepDescription.Should().Be("Order intake");
    }
}

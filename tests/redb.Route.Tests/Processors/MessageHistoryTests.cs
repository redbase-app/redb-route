using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Tests for the Message History EIP: per-node recording, enablement (global + route override),
/// ordering, on-throw capture, and formatting.
/// </summary>
public class MessageHistoryTests
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
    public async Task MessageHistory_RouteEnabled_RecordsEachNodeInOrder()
    {
        await using var context = new RouteContext();
        context.AddRoutes(r =>
            r.From("direct://mh-order").MessageHistory()
                .Process(_ => { })
                .Process(_ => { }));

        var exchange = await RunAsync(context, "direct://mh-order", new Exchange(new Message("x")));
        var entries = MessageHistory.GetEntries(exchange);

        entries.Should().HaveCount(2);
        entries[0].NodeId.Should().Be("processAction1");
        entries[1].NodeId.Should().Be("processAction2");
        entries.Should().OnlyContain(e => e.Label == "processAction" && e.ElapsedMs >= 0 && e.RouteId.Length > 0);
    }

    [Fact]
    public async Task MessageHistory_DisabledByDefault_RecordsNothing()
    {
        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct://mh-off").Process(_ => { }));

        var exchange = await RunAsync(context, "direct://mh-off", new Exchange(new Message("x")));

        MessageHistory.GetEntries(exchange).Should().BeEmpty();
    }

    [Fact]
    public async Task MessageHistory_GlobalOption_Enables()
    {
        await using var context = new RouteContext(options: new RouteEngineOptions { EnableMessageHistory = true });
        context.AddRoutes(r => r.From("direct://mh-global").Process(_ => { }));

        var exchange = await RunAsync(context, "direct://mh-global", new Exchange(new Message("x")));

        MessageHistory.GetEntries(exchange).Should().ContainSingle();
    }

    [Fact]
    public async Task MessageHistory_RouteOverride_DisablesWhenGlobalOn()
    {
        await using var context = new RouteContext(options: new RouteEngineOptions { EnableMessageHistory = true });
        context.AddRoutes(r => r.From("direct://mh-override").MessageHistory(false).Process(_ => { }));

        var exchange = await RunAsync(context, "direct://mh-override", new Exchange(new Message("x")));

        MessageHistory.GetEntries(exchange).Should().BeEmpty();
    }

    [Fact]
    public async Task MessageHistory_RecordsEntry_EvenWhenNodeThrows()
    {
        await using var context = new RouteContext();
        context.AddRoutes(r =>
            r.From("direct://mh-throw").MessageHistory()
                .Process(_ => throw new InvalidOperationException("boom")));

        await context.Start();
        var producer = context.GetEndpoint("direct://mh-throw").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("x"));

        // The throwing node still records its history entry (recorded in a finally).
        try { await producer.Process(exchange); } catch (InvalidOperationException) { /* expected */ }

        var entries = MessageHistory.GetEntries(exchange);
        entries.Should().ContainSingle().Which.Label.Should().Be("processAction");
    }

    [Fact]
    public async Task MessageHistory_Format_ProducesTable()
    {
        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct://mh-fmt").MessageHistory().Process(_ => { }));

        var exchange = await RunAsync(context, "direct://mh-fmt", new Exchange(new Message("x")));

        var dump = MessageHistory.Format(exchange);
        dump.Should().Contain("Message History");
        dump.Should().Contain("processAction");
    }

    [Fact]
    public async Task MessageHistory_Format_EmptyWhenNoHistory()
    {
        MessageHistory.Format(new Exchange(new Message("x"))).Should().BeEmpty();
    }
}

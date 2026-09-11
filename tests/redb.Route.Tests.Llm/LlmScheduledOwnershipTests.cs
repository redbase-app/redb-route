using redb.Route.Abstractions;
using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm;

/// <summary>
/// Statistics ownership for the scheduled agent (<c>From("llm://...?schedule=...")</c>):
/// the route pipeline is wrapped by the core's StatisticsProcessor, so the consumer must not
/// self-record what the core already counts. It records only what the core cannot see —
/// a tick that failed before the exchange reached the pipeline.
/// </summary>
public sealed class LlmScheduledOwnershipTests
{
    [Fact]
    public async Task ScheduledTick_IsCountedByTheCore_NotByTheConsumer()
    {
        var fake = new FakeProvider().EnqueueText("tick!");
        await using var host = LiveLlmHost.Build();
        host.AddFactory("fake", new LlmConnectionFactory
        {
            Name = "fake",
            Provider = "fake",
            ModelId = fake.ModelId,
            PrebuiltProvider = fake
        });

        const string uri = "llm://fake?schedule=200ms&initialBodyRef=go";
        await host.StartAsync(r => r.From(uri).To("mock:llm-ticks"));

        var sink = host.Mock("mock:llm-ticks");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (sink.ReceivedCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        sink.ReceivedCount.Should().BeGreaterThan(0, "хотя бы один тик должен доехать до маршрута");

        var ep = (IEndpointStatistics)host.Context.GetEndpoint(uri);
        // The pipeline leg is the core's: MessagesIn per tick from StatisticsProcessor.
        ep.MessagesIn.Should().BeGreaterThan(0, "MessagesIn пишет StatisticsProcessor вокруг From()");
        // Nothing produces THROUGH the scheduled endpoint - self-recording a MessagesOut per
        // tick doubled the picture (core wrote its own success number on the same endpoint).
        ep.MessagesOut.Should().Be(0, "MessagesOut — продюсерский счётчик, тик им не является");
        ep.Errors.Should().Be(0, "успешные тики не должны приносить ошибок");
    }
}

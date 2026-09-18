using System.Collections.Concurrent;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm;

/// <summary>
/// The scheduled agent (<c>From("llm://...?schedule=...")</c>) resolves its prompt references with the tick's
/// own exchange. A redb-backed template registry needs it to resolve a scope of that tick instead of the one
/// shared instance every concurrent tick would land on. The lookup runs inside the block that owns the
/// exchange, so a failing template releases it.
/// </summary>
public sealed class LlmConsumerPromptExchangeTests
{
    /// <summary>Records every lookup: the exchange it was made with and whether that exchange had a DI scope.</summary>
    private sealed class RecordingTemplates(Func<string, PromptTemplate?> lookup) : IPromptTemplateRegistry
    {
        private readonly ConcurrentQueue<(IExchange? Exchange, bool HadScope)> _lookups = new();

        public (IExchange? Exchange, bool HadScope)[] Lookups => _lookups.ToArray();

        public Task<PromptTemplate?> GetAsync(string name, string? version = null, IExchange? exchange = null,
            CancellationToken ct = default)
        {
            _lookups.Enqueue((exchange, exchange?.ServiceProvider is not null));
            return Task.FromResult(lookup(name));
        }

        public Task SetAsync(PromptTemplate template, IExchange? exchange = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<PromptTemplate>> ListAsync(IExchange? exchange = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PromptTemplate>>([]);
    }

    private static LiveLlmHost Host(FakeProvider fake, IPromptTemplateRegistry templates)
    {
        var host = LiveLlmHost.Build();
        host.AddFactory("fake", new LlmConnectionFactory
        {
            Name = "fake",
            Provider = "fake",
            ModelId = fake.ModelId,
            PrebuiltProvider = fake
        });
        host.Context.AddService(typeof(IPromptTemplateRegistry), templates);
        return host;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    [Fact]
    public async Task ScheduledTick_ResolvesPromptsWithItsExchange()
    {
        var calls = new ConcurrentQueue<(string? SystemPrompt, string? UserText)>();
        var fake = new FakeProvider
        {
            OnCall = (request, _) =>
            {
                var userText = request.Messages.LastOrDefault()?.Content.OfType<LlmTextBlock>().FirstOrDefault()?.Text;
                calls.Enqueue((request.SystemPrompt, userText));
                return Task.CompletedTask;
            }
        }.EnqueueText("tick!");
        var templates = new RecordingTemplates(name => new PromptTemplate
        {
            Name = name,
            Version = "1",
            Body = name == "go" ? "status of order 42?" : "Reply tersely."
        });

        await using var host = Host(fake, templates);
        await host.StartAsync(r => r.From("llm://fake?schedule=200ms&initialBodyRef=#go&systemPromptRef=#terse")
            .To("mock:llm-ticks"));

        var sink = host.Mock("mock:llm-ticks");
        await WaitUntilAsync(() => sink.ReceivedCount > 0);
        sink.ReceivedCount.Should().BeGreaterThan(0, "at least one tick has to reach the route");

        var lookups = templates.Lookups;
        lookups.Should().NotBeEmpty();
        lookups.Should().OnlyContain(l => l.Exchange != null,
            "a lookup without the tick's exchange lands every concurrent tick on one shared redb instance");
        lookups.Should().OnlyContain(l => l.HadScope, "the exchange carries the tick's DI scope when the prompts resolve");

        var first = calls.Should().NotBeEmpty().And.Subject.First();
        first.SystemPrompt.Should().Be("Reply tersely.");
        first.UserText.Should().Be("status of order 42?", "the resolved initial body is still what the model is asked");
    }

    [Fact]
    public async Task ScheduledTick_TemplateFailure_ReleasesTheExchange()
    {
        var fake = new FakeProvider();
        var templates = new RecordingTemplates(_ => throw new InvalidOperationException("template store is down"));

        await using var host = Host(fake, templates);
        await host.StartAsync(r => r.From("llm://fake?schedule=200ms&initialBodyRef=#go").To("mock:llm-ticks"));

        await WaitUntilAsync(() => templates.Lookups.Length > 0);
        var lookup = templates.Lookups.Should().NotBeEmpty().And.Subject.First();
        lookup.Exchange.Should().NotBeNull("the prompt is resolved with the tick's exchange");
        lookup.HadScope.Should().BeTrue("the exchange owned a DI scope when the lookup failed");

        // The failing lookup runs inside the block that owns the exchange, so the tick's scope is released.
        await WaitUntilAsync(() => lookup.Exchange!.ServiceProvider is null);
        lookup.Exchange!.ServiceProvider.Should().BeNull("a failed template lookup must not leak the tick's DI scope");
        fake.CallCount.Should().Be(0, "the tick failed before the model was called");
    }
}

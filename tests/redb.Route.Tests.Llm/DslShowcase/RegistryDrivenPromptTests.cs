using redb.Route.Llm.Engine.Storage;
using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// The <c>#name</c> registry-ref is a framework-wide convention in redb.Route:
/// connection factories, processors, and now prompts use the same shape.
/// <para>
/// A leading <c>#</c> on <c>?systemPromptRef=</c> or <c>?initialBodyRef=</c> turns
/// the URI parameter into a registry lookup. Resolution order:
/// <list type="number">
///   <item><see cref="IPromptTemplateRegistry"/> (latest version of the named template)</item>
///   <item><see cref="IRouteContext"/>'s named-object registry (a plain string)</item>
/// </list>
/// Plain values are still treated as literal prompts.
/// </para>
/// <para>
/// The point: any other route — running on its own schedule — can rewrite the
/// referenced key, and the next agent invocation picks up the fresh prompt
/// without a route redeploy. Decoupling source-of-truth from caller is the
/// same trick we already use for Kafka/S3 connection factories.
/// </para>
/// We use the scripted <see cref="FakeProvider"/> here so the assertions can
/// focus on <i>which system prompt arrived at the provider</i> — that is the
/// behavior under test, not the model's reply quality.
/// </summary>
[Trait("Category", "LiveLlm")]
[Collection("LiveLlmSerial")]
public sealed class RegistryDrivenPromptTests
{
    [Fact]
    public async Task SystemPromptRef_HashName_ResolvesFromRouteContextRegistry()
    {
        var fake = new FakeProvider().EnqueueText("ok").EnqueueText("ok");

        await using var host = LiveLlmHost.Build()
            .AddFactory("scripted", new LlmConnectionFactory
            {
                Provider = "fake",
                ModelId = fake.ModelId,
                PrebuiltProvider = fake
            });

        // Step 1 — populate the prompt by name. Any route, processor, or
        // bootstrap step in the application could do this.
        host.Context.AddToRegistry("style.terse", "Reply in fewer than 5 words.");

        await host.StartAsync(r =>
        {
            r.From("direct:chat")
                .To("llm://scripted?systemPromptRef=#style.terse")
                .To("mock:done");
        });

        await host.SendAsync("direct:chat", "Hi.");

        fake.CapturedRequests[0].SystemPrompt.Should().Be("Reply in fewer than 5 words.");

        // Step 2 — a different actor mutates the same key; the next call sees
        // the new value with no changes to the LLM route.
        host.Context.AddToRegistry("style.terse", "Reply in French only.");

        await host.SendAsync("direct:chat", "Hi again.");

        fake.CapturedRequests[1].SystemPrompt.Should().Be("Reply in French only.");
    }

    [Fact]
    public async Task SystemPromptRef_HashName_PrefersPromptTemplateRegistry()
    {
        var fake = new FakeProvider().EnqueueText("ok");

        var templates = new InMemoryPromptTemplateRegistry();
        await templates.SetAsync(new PromptTemplate
        {
            Name = "watchdog",
            Version = "v1",
            Body = "You are a watchdog. Respond with PASS or FAIL only."
        });

        await using var host = LiveLlmHost.Build()
            .AddFactory("scripted", new LlmConnectionFactory
            {
                Provider = "fake",
                ModelId = fake.ModelId,
                PrebuiltProvider = fake
            });

        host.Context.AddService(typeof(IPromptTemplateRegistry), templates);

        // Decoy in the generic registry — must be *ignored* in favour of the
        // versioned IPromptTemplateRegistry hit. This is what makes the prompt
        // ref versionable and replayable in eval runs.
        host.Context.AddToRegistry("watchdog", "WRONG — should not be used.");

        await host.StartAsync(r =>
        {
            r.From("direct:chat")
                .To("llm://scripted?systemPromptRef=#watchdog")
                .To("mock:done");
        });

        await host.SendAsync("direct:chat", "ping");

        fake.CapturedRequests[0].SystemPrompt.Should().Be(
            "You are a watchdog. Respond with PASS or FAIL only.");
    }

    [Fact]
    public async Task SystemPromptRef_PlainValue_StaysLiteral()
    {
        var fake = new FakeProvider().EnqueueText("ok");

        await using var host = LiveLlmHost.Build()
            .AddFactory("scripted", new LlmConnectionFactory
            {
                Provider = "fake",
                ModelId = fake.ModelId,
                PrebuiltProvider = fake
            });

        // Even though "literal" exists in the registry, the URI ref has no
        // leading '#' — so it must arrive unchanged at the provider.
        host.Context.AddToRegistry("literal", "WRONG.");

        await host.StartAsync(r =>
        {
            r.From("direct:chat")
                .To("llm://scripted?systemPromptRef=literal")
                .To("mock:done");
        });

        await host.SendAsync("direct:chat", "ping");

        fake.CapturedRequests[0].SystemPrompt.Should().Be("literal");
    }
}

using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm;

public sealed class LlmProducerTests
{
    private static (RouteContext ctx, LlmEndpoint endpoint) Build(
        FakeProvider provider,
        string factoryName = "fake",
        string uri = "llm://fake",
        Action<LlmConnectionFactory>? configure = null)
    {
        var ctx = new RouteContext();
        var component = new LlmComponent();
        ctx.AddComponent(component);

        var factory = new LlmConnectionFactory
        {
            Name = factoryName,
            Provider = "fake",
            ModelId = provider.ModelId,
            PrebuiltProvider = provider
        };
        configure?.Invoke(factory);
        ctx.AddToRegistry(factoryName, factory);
        ctx.AddService(typeof(IAgentEngine), new AgentEngine());

        var endpoint = (LlmEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(uri));
        return (ctx, endpoint);
    }

    [Fact]
    public void Ctor_NullEndpoint_Throws()
    {
        var act = () => new LlmProducer(null!, new LlmEndpointOptions());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        var (_, endpoint) = Build(new FakeProvider());
        var act = () => new LlmProducer(endpoint, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Process_BeforeStart_Throws()
    {
        var (_, endpoint) = Build(new FakeProvider());
        var producer = new LlmProducer(endpoint, new LlmEndpointOptions());

        var act = () => producer.Process(new Exchange(new Message("hi")));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not been started*");
    }

    [Fact]
    public async Task Process_WritesAssistantTextAndHeaders()
    {
        var fake = new FakeProvider().EnqueueText("hello back", tokensIn: 3, tokensOut: 4);
        var (_, endpoint) = Build(fake);

        var producer = new LlmProducer(endpoint, new LlmEndpointOptions());
        await producer.Start();

        var exchange = new Exchange(new Message("hi"));
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().Be("hello back");
        exchange.Out.Headers[LlmHeaders.ProviderId].Should().Be("fake");
        exchange.Out.Headers[LlmHeaders.ModelId].Should().Be("fake-model");
        exchange.Out.Headers[LlmHeaders.TokensIn].Should().Be(3);
        exchange.Out.Headers[LlmHeaders.TokensOut].Should().Be(4);
        exchange.Out.Headers[LlmHeaders.ToolIterations].Should().Be(1);
        exchange.Out.Headers[LlmHeaders.StopReason].Should().Be(nameof(LlmStopReason.EndTurn));
    }

    [Fact]
    public async Task Process_RecordsBytesInForUserPrompt()
    {
        var fake = new FakeProvider().EnqueueText("ok");
        var (_, endpoint) = Build(fake);
        var producer = (LlmProducer)endpoint.CreateProducer();
        await producer.Start();

        var prompt = "abcdefghij"; // 10 bytes
        await producer.Process(new Exchange(new Message(prompt)));

        endpoint.BytesIn.Should().Be(prompt.Length);
    }

    [Fact]
    public async Task Process_MissingFactory_Throws()
    {
        var ctx = new RouteContext();
        var component = new LlmComponent();
        ctx.AddComponent(component);
        ctx.AddService(typeof(IAgentEngine), new AgentEngine());

        var endpoint = (LlmEndpoint)component.CreateEndpoint(EndpointUriParser.Parse("llm://nope"));
        var producer = (LlmProducer)endpoint.CreateProducer();
        await producer.Start();

        var act = () => producer.Process(new Exchange(new Message("hi")));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*nope*not registered*");
    }

    [Fact]
    public async Task Process_MissingEngine_Throws()
    {
        var ctx = new RouteContext();
        var component = new LlmComponent();
        ctx.AddComponent(component);
        ctx.AddToRegistry("fake", new LlmConnectionFactory
        {
            Name = "fake",
            Provider = "stub",
            ModelId = "x"
        });

        var endpoint = (LlmEndpoint)component.CreateEndpoint(EndpointUriParser.Parse("llm://fake"));
        var producer = (LlmProducer)endpoint.CreateProducer();
        await producer.Start();

        var act = () => producer.Process(new Exchange(new Message("hi")));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IAgentEngine*");
    }

    [Fact]
    public async Task Process_PassesSystemPromptFromHeader()
    {
        var fake = new FakeProvider().EnqueueText("ok");
        var (_, endpoint) = Build(fake);
        var producer = (LlmProducer)endpoint.CreateProducer();
        await producer.Start();

        var msg = new Message("translate me");
        msg.Headers[LlmHeaders.SystemPrompt] = "you are a translator";
        await producer.Process(new Exchange(msg));

        fake.CapturedRequests.Single().SystemPrompt.Should().Be("you are a translator");
    }
}

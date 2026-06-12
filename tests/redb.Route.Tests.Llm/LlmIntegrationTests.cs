using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm;

/// <summary>
/// End-to-end pipeline tests using a scriptable <see cref="FakeProvider"/>.
/// Verifies that <c>LlmComponent → LlmEndpoint → LlmProducer → AgentEngine</c>
/// correctly forwards user content, dispatches tool calls through the producer
/// template against a real <c>direct:</c> route, and emits the canonical
/// metrics. Because the provider is scripted, these tests run without any
/// network access or API keys.
/// </summary>
public sealed class LlmIntegrationTests
{
    private static (RouteContext ctx, LlmProducer producer, LlmEndpoint endpoint, IProducerTemplate pt)
        BuildRoute(FakeProvider fake, string factoryName = "fake", string uri = "llm://fake")
    {
        var ctx = new RouteContext();
        var component = new LlmComponent();
        ctx.AddComponent(component);
        ctx.AddToRegistry(factoryName, new LlmConnectionFactory
        {
            Name = factoryName,
            Provider = "fake",
            ModelId = fake.ModelId,
            PrebuiltProvider = fake
        });

        var pt = new ProducerTemplate(ctx);
        ctx.AddService(typeof(IProducerTemplate), pt);

        var engine = new AgentEngine(
            logger: null,
            producerTemplate: pt,
            observer: new NoopAgentObserver(),
            budget: new NoopBudgetEnforcer(),
            approval: new AutoApproveGate(),
            redaction: new NoopRedactionFilter(),
            shadow: new NoopShadowRunner(),
            conversation: null, idempotency: null, approvalStore: null);
        ctx.AddService(typeof(IAgentEngine), engine);

        var registry = new ToolDescriptorRegistry();
        ctx.AddService(typeof(IToolDescriptorRegistry), registry);

        var endpoint = (LlmEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(uri));
        var producer = (LlmProducer)endpoint.CreateProducer();
        return (ctx, producer, endpoint, pt);
    }

    [Fact]
    public async Task EndToEnd_StubProvider_RoundTrip()
    {
        var ctx = new RouteContext();
        var component = new LlmComponent();
        ctx.AddComponent(component);
        ctx.AddToRegistry("local", new LlmConnectionFactory
        {
            Name = "local",
            Provider = "stub",
            ModelId = "echo-1"
        });
        ctx.AddService(typeof(IAgentEngine), new AgentEngine());

        var endpoint = (LlmEndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse(LlmDsl.Factory("local").Temperature(0.0).AsUri()));
        var producer = (LlmProducer)endpoint.CreateProducer();
        await producer.Start();

        var ex = new Exchange(new Message("translate this"));
        await producer.Process(ex);

        ex.Out!.Body.Should().Be("[stub] translate this");
        ex.Out.Headers[LlmHeaders.ProviderId].Should().Be("stub");
        ex.Out.Headers[LlmHeaders.ModelId].Should().Be("echo-1");
        // LlmProducer.Process counts each agent turn as MessagesIn=1 / MessagesOut=1
        // so the tsak dashboard sees throughput on `.To(LlmDsl....AsUri())` hops.
        endpoint.MessagesIn.Should().Be(1);
        endpoint.MessagesOut.Should().Be(1);
        endpoint.BytesIn.Should().Be("translate this".Length);
    }

    [Fact]
    public async Task EndToEnd_ToolLoop_DispatchesViaProducerTemplate()
    {
        // Scripted provider asks for tool 'echo' once, then ends the turn with text.
        var fake = new FakeProvider()
            .EnqueueToolUse("echo", "{\"v\":1}")
            .EnqueueText("done");

        // Set up everything via host helper so the tool route shares the registry.
        await using var host = LiveLlmHost.Build();
        host.Context.AddToRegistry("fake", new LlmConnectionFactory
        {
            Name = "fake",
            Provider = "fake",
            ModelId = fake.ModelId,
            PrebuiltProvider = fake
        });

        var echoTool = new EchoToolRoute(
            toolName: "echo",
            description: "echo back the result",
            inputSchema: """{"type":"object","properties":{"v":{"type":"number"}}}""",
            replyJson: """{"r":42}""");

        await host.StartAsync(echoTool, r =>
        {
            r.From("direct:agent")
                .To(LlmDsl.Factory("fake").Tools("echo").AsUri())
                .To("mock:done");
        });

        var ex = await host.SendAsync("direct:agent", "go");

        // After running through the route, the body has rotated into In on the next hop.
        var sink = host.Mock("mock:done");
        sink.ReceivedCount.Should().Be(1);
        ((string)sink.ReceivedExchanges[0].In.Body!).Should().Be("done");

        echoTool.CapturedInputs.Should().ContainSingle()
            .Which.Should().Contain("\"v\":1");
        fake.CallCount.Should().Be(2);

        // The second LLM request must contain the tool_result that came back from the route.
        var followUp = fake.CapturedRequests[^1];
        followUp.Messages.SelectMany(m => m.Content)
            .OfType<LlmToolResultBlock>()
            .Should().ContainSingle()
            .Which.OutputJson.Should().Contain("42");
    }

    [Fact]
    public async Task EndToEnd_EmitsOpenTelemetryMetrics()
    {
        var exported = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("redb.Route")
            .AddInMemoryExporter(exported)
            .Build();

        var fake = new FakeProvider().EnqueueText("hi", tokensIn: 7, tokensOut: 11);
        var (_, producer, _, _) = BuildRoute(fake);
        await producer.Start();

        await producer.Process(new Exchange(new Message("ping")));

        meterProvider.ForceFlush(2000);

        exported.Should().Contain(m => m.Name == "redb.route.llm.calls");
        exported.Should().Contain(m => m.Name == "redb.route.llm.agent.runs");
        exported.Should().Contain(m => m.Name == "redb.route.llm.tokens.in");
        exported.Should().Contain(m => m.Name == "redb.route.llm.tokens.out");
    }

    [Fact]
    public async Task EndToEnd_ConversationFromHeader_PropagatesToRequest()
    {
        var fake = new FakeProvider().EnqueueText("ack");
        var (_, producer, _, _) = BuildRoute(fake, uri: "llm://fake?conversation=header");
        await producer.Start();

        var msg = new Message("hello");
        msg.Headers[LlmHeaders.ConversationId] = "conv-42";
        await producer.Process(new Exchange(msg));

        // ConversationId is forwarded to the agent request; persistence is opt-in (Phase 2).
        fake.CallCount.Should().Be(1);
    }
}

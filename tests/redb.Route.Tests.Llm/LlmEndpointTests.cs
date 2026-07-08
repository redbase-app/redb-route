namespace redb.Route.Tests.Llm;

public sealed class LlmEndpointTests
{
    private static LlmEndpoint CreateEndpoint(string uriStr)
    {
        var component = new LlmComponent();
        var uri = EndpointUriParser.Parse(uriStr);
        return (LlmEndpoint)component.CreateEndpoint(uri);
    }

    [Fact]
    public void ConnectionFactoryName_FromPath()
    {
        CreateEndpoint("llm://claude").ConnectionFactoryName.Should().Be("claude");
    }

    [Fact]
    public void CreateProducer_ReturnsLlmProducer()
    {
        CreateEndpoint("llm://c").CreateProducer().Should().BeOfType<LlmProducer>();
    }

    [Fact]
    public void CreateConsumer_ReturnsLlmConsumer()
    {
        var ep = CreateEndpoint("llm://c");
        var processor = Substitute.For<IProcessor>();
        ep.CreateConsumer(processor).Should().BeOfType<LlmConsumer>();
    }

    [Fact]
    public void Uri_PreservesSchemeAndPath()
    {
        var ep = CreateEndpoint("llm://my-factory?temperature=0.1");
        ep.Uri.Scheme.Should().Be("llm");
        ep.Uri.Path.Should().Be("my-factory");
    }

    [Fact]
    public void Component_IsLlmComponent()
    {
        CreateEndpoint("llm://c").Component.Should().BeOfType<LlmComponent>();
    }

    [Fact]
    public void Statistics_AreInitialized()
    {
        var ep = CreateEndpoint("llm://c");
        ep.MessagesIn.Should().Be(0);
        ep.MessagesOut.Should().Be(0);
        ep.Errors.Should().Be(0);
        ep.BytesIn.Should().Be(0);
    }
}

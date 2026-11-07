namespace redb.Route.Tests.Llm;

public sealed class LlmComponentTests
{
    [Fact]
    public void Scheme_IsLlm()
    {
        new LlmComponent().Scheme.Should().Be("llm");
    }

    [Fact]
    public void CreateEndpoint_ParsesPathAsFactoryName()
    {
        var component = new LlmComponent();
        var uri = EndpointUriParser.Parse("llm://claude?temperature=0.2");
        var endpoint = (LlmEndpoint)component.CreateEndpoint(uri);

        endpoint.ConnectionFactoryName.Should().Be("claude");
        endpoint.Uri.RawParameters["temperature"].Should().Be("0.2");
    }

    [Fact]
    public void CreateEndpoint_FactoryNameViaQuery_Works()
    {
        var component = new LlmComponent();
        var uri = EndpointUriParser.Parse("llm://?connectionFactory=local");
        var endpoint = (LlmEndpoint)component.CreateEndpoint(uri);
        endpoint.ConnectionFactoryName.Should().Be("local");
    }

    [Fact]
    public void CreateEndpoint_NoFactoryName_Throws()
    {
        var component = new LlmComponent();
        var uri = EndpointUriParser.Parse("llm://?temperature=0.1");

        var act = () => component.CreateEndpoint(uri);

        act.Should().Throw<InvalidOperationException>().WithMessage("*connection factory*");
    }

    [Fact]
    public void CreateEndpoint_InvalidTemperature_Throws()
    {
        var component = new LlmComponent();
        var uri = EndpointUriParser.Parse("llm://c?temperature=5.0");

        var act = () => component.CreateEndpoint(uri);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var component = new LlmComponent();
        var act = () => component.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

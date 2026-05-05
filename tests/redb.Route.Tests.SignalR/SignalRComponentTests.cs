using redb.Route.Abstractions;
using redb.Route.SignalR;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// Unit tests for SignalRComponent: URI parsing, endpoint creation, hub path extraction.
/// </summary>
public class SignalRComponentTests
{
    private readonly SignalRComponent _component = new();

    // ── Scheme ──

    [Fact]
    public void Scheme_IsSignalr()
    {
        _component.Scheme.Should().Be("signalr");
    }

    // ── ParseHostPort ──

    [Fact]
    public void ParseHostPort_HostAndPort()
    {
        var opts = new SignalREndpointOptions();
        SignalRComponent.ParseHostPort("/0.0.0.0:5000/chatHub", opts);

        opts.Host.Should().Be("0.0.0.0");
        opts.Port.Should().Be(5000);
    }

    [Fact]
    public void ParseHostPort_HostOnly_KeepsDefaultPort()
    {
        var opts = new SignalREndpointOptions();
        SignalRComponent.ParseHostPort("/myhost", opts);

        opts.Host.Should().Be("myhost");
        opts.Port.Should().Be(5000); // default
    }

    [Fact]
    public void ParseHostPort_HostPortNoPath()
    {
        var opts = new SignalREndpointOptions();
        SignalRComponent.ParseHostPort("/192.168.1.1:9090", opts);

        opts.Host.Should().Be("192.168.1.1");
        opts.Port.Should().Be(9090);
    }

    // ── ExtractHubPath ──

    [Theory]
    [InlineData("/0.0.0.0:5000/chatHub", "/chatHub")]
    [InlineData("/0.0.0.0:5000/api/hub", "/api/hub")]
    [InlineData("/0.0.0.0:5000", "/")]
    [InlineData("/localhost:3000/", "/")]
    public void ExtractHubPath_VariousInputs(string uriPath, string expected)
    {
        SignalRComponent.ExtractHubPath(uriPath).Should().Be(expected);
    }

    // ── CreateEndpoint ──

    [Fact]
    public void CreateEndpoint_ReturnsSignalREndpoint()
    {
        var uri = new EndpointUri("signalr", "/127.0.0.1:5000/hub",
            "signalr://127.0.0.1:5000/hub", new Dictionary<string, string>());

        var endpoint = _component.CreateEndpoint(uri);

        endpoint.Should().BeOfType<SignalREndpoint>();
    }

    [Fact]
    public void CreateEndpoint_ParsesOptions()
    {
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "Send",
            ["inOut"] = "true",
            ["mode"] = "Server",
            ["defaultGroup"] = "lobby"
        };
        var uri = new EndpointUri("signalr", "/0.0.0.0:7000/chatHub",
            "signalr://0.0.0.0:7000/chatHub", parameters);

        var endpoint = (SignalREndpoint)_component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Host.Should().Be("0.0.0.0");
        endpoint.EndpointOptions.Port.Should().Be(7000);
        endpoint.EndpointOptions.Method.Should().Be("Send");
        endpoint.EndpointOptions.InOut.Should().BeTrue();
        endpoint.EndpointOptions.Mode.Should().Be(SignalRMode.Server);
        endpoint.EndpointOptions.DefaultGroup.Should().Be("lobby");
        endpoint.HubPath.Should().Be("/chatHub");
    }

    [Fact]
    public void CreateEndpoint_BuildClientUrl_Http()
    {
        var uri = new EndpointUri("signalr", "/api.example.com:5000/chatHub",
            "signalr://api.example.com:5000/chatHub", new Dictionary<string, string>());

        var endpoint = (SignalREndpoint)_component.CreateEndpoint(uri);

        endpoint.BuildClientUrl().Should().Be("http://api.example.com:5000/chatHub");
    }

    [Fact]
    public void CreateEndpoint_BuildClientUrl_Https()
    {
        var parameters = new Dictionary<string, string> { ["ssl"] = "true" };
        var uri = new EndpointUri("signalr", "/api.example.com:5000/chatHub",
            "signalr://api.example.com:5000/chatHub", parameters);

        var endpoint = (SignalREndpoint)_component.CreateEndpoint(uri);

        endpoint.BuildClientUrl().Should().Be("https://api.example.com:5000/chatHub");
    }

    // ── Consumer registry ──

    [Fact]
    public void ConsumerRegistry_RegisterAndGet()
    {
        var processor = Substitute.For<IProcessor>();
        var uri = new EndpointUri("signalr", "/127.0.0.1:5000/hub",
            "signalr://127.0.0.1:5000/hub", new Dictionary<string, string>());
        var endpoint = (SignalREndpoint)_component.CreateEndpoint(uri);
        var consumer = new SignalRConsumer(endpoint, processor, endpoint.EndpointOptions);

        _component.RegisterConsumer("key1", consumer);
        _component.GetConsumer("key1").Should().BeSameAs(consumer);
    }

    [Fact]
    public void ConsumerRegistry_Unregister()
    {
        var processor = Substitute.For<IProcessor>();
        var uri = new EndpointUri("signalr", "/127.0.0.1:5000/hub",
            "signalr://127.0.0.1:5000/hub", new Dictionary<string, string>());
        var endpoint = (SignalREndpoint)_component.CreateEndpoint(uri);
        var consumer = new SignalRConsumer(endpoint, processor, endpoint.EndpointOptions);

        _component.RegisterConsumer("key1", consumer);
        _component.UnregisterConsumer("key1");
        _component.GetConsumer("key1").Should().BeNull();
    }

    [Fact]
    public void ConsumerRegistry_GetNonExistent_ReturnsNull()
    {
        _component.GetConsumer("nonexistent").Should().BeNull();
    }
}

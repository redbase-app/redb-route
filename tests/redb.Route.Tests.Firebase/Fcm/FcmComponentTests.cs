using redb.Route.Abstractions;
using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

public sealed class FcmComponentTests
{
    [Fact]
    public void Scheme_IsFcm()
    {
        var component = new FcmComponent();
        component.Scheme.Should().Be("fcm");
    }

    [Fact]
    public void CreateEndpoint_ValidToken_ReturnsEndpoint()
    {
        var component = new FcmComponent();
        var uri = new EndpointUri("fcm", "send", "fcm://send",
            new Dictionary<string, string>
            {
                ["credentialPath"] = "/sa.json",
                ["messageType"] = "Token",
                ["token"] = "tok"
            });

        var endpoint = component.CreateEndpoint(uri);
        endpoint.Should().NotBeNull();
        endpoint.Should().BeOfType<FcmEndpoint>();
    }

    [Fact]
    public void CreateEndpoint_CreateConsumer_ThrowsNotSupported()
    {
        var component = new FcmComponent();
        var uri = new EndpointUri("fcm", "send", "fcm://send",
            new Dictionary<string, string>
            {
                ["credentialPath"] = "/sa.json",
                ["messageType"] = "Token",
                ["token"] = "tok"
            });

        var endpoint = component.CreateEndpoint(uri);
        var act = () => endpoint.CreateConsumer(Substitute.For<IProcessor>());
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void CreateEndpoint_CreateProducer_ReturnsFcmProducer()
    {
        var component = new FcmComponent();
        var uri = new EndpointUri("fcm", "send", "fcm://send",
            new Dictionary<string, string>
            {
                ["credentialPath"] = "/sa.json",
                ["messageType"] = "Token",
                ["token"] = "tok"
            });

        var endpoint = component.CreateEndpoint(uri);
        var producer = endpoint.CreateProducer();
        producer.Should().NotBeNull();
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var component = new FcmComponent();
        var act = () => component.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

using redb.Route.Abstractions;
using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

public sealed class FirestoreComponentTests
{
    private static EndpointUri MakeUri(string path = "orders", Dictionary<string, string>? extra = null)
    {
        var parameters = new Dictionary<string, string> { ["credentialPath"] = "/sa.json" };
        if (extra is not null)
            foreach (var (k, v) in extra)
                parameters[k] = v;
        return new EndpointUri("fstore", path, $"fstore://{path}", parameters);
    }
    [Fact]
    public void Scheme_IsFirestore()
    {
        var component = new FirestoreComponent();
        component.Scheme.Should().Be("fstore");
    }

    [Fact]
    public void CreateEndpoint_ValidOptions_ReturnsEndpoint()
    {
        var component = new FirestoreComponent();
        var endpoint = component.CreateEndpoint(MakeUri());
        endpoint.Should().NotBeNull();
        endpoint.Should().BeOfType<FirestoreEndpoint>();
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var component = new FirestoreComponent();
        var act = () => component.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_CollectionPath_ParsedFromUri()
    {
        var component = new FirestoreComponent();
        var endpoint = (FirestoreEndpoint)component.CreateEndpoint(
            MakeUri("users/uid/orders"));
        endpoint.CollectionPath.Should().Be("users/uid/orders");
    }

    [Fact]
    public void CreateEndpoint_CreateProducer_ReturnsProducer()
    {
        var component = new FirestoreComponent();
        var endpoint = component.CreateEndpoint(MakeUri());
        var producer = endpoint.CreateProducer();
        producer.Should().NotBeNull();
    }

    [Fact]
    public void CreateEndpoint_CreateConsumer_ReturnsConsumer()
    {
        var component = new FirestoreComponent();
        var endpoint = component.CreateEndpoint(MakeUri());
        var consumer = endpoint.CreateConsumer(Substitute.For<IProcessor>());
        consumer.Should().NotBeNull();
    }
}

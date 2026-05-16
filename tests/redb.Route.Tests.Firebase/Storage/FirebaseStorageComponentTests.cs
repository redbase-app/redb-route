using redb.Route.Abstractions;
using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

public sealed class FirebaseStorageComponentTests
{
    private static EndpointUri MakeUri(string path = "my-bucket", Dictionary<string, string>? extra = null)
    {
        var parameters = new Dictionary<string, string> { ["credentialPath"] = "/sa.json" };
        if (extra is not null)
            foreach (var (k, v) in extra)
                parameters[k] = v;
        return new EndpointUri("fbstorage", path, $"fbstorage://{path}", parameters);
    }

    [Fact]
    public void Scheme_IsFirebaseStorage()
    {
        var component = new FirebaseStorageComponent();
        component.Scheme.Should().Be("fbstorage");
    }

    [Fact]
    public void CreateEndpoint_ValidOptions_ReturnsEndpoint()
    {
        var component = new FirebaseStorageComponent();
        var endpoint = component.CreateEndpoint(MakeUri());
        endpoint.Should().NotBeNull();
        endpoint.Should().BeOfType<FirebaseStorageEndpoint>();
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var component = new FirebaseStorageComponent();
        var act = () => component.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_BucketName_ParsedFromUri()
    {
        var component = new FirebaseStorageComponent();
        var endpoint = (FirebaseStorageEndpoint)component.CreateEndpoint(MakeUri());
        endpoint.BucketName.Should().Be("my-bucket");
    }

    [Fact]
    public void CreateEndpoint_BucketWithPrefix_Parsed()
    {
        var component = new FirebaseStorageComponent();
        var endpoint = (FirebaseStorageEndpoint)component.CreateEndpoint(
            MakeUri("my-bucket/uploads/"));
        endpoint.BucketName.Should().Be("my-bucket");
        endpoint.ObjectPrefix.Should().Be("uploads/");
    }

    [Fact]
    public void CreateEndpoint_CreateProducer_ReturnsProducer()
    {
        var component = new FirebaseStorageComponent();
        var endpoint = component.CreateEndpoint(MakeUri());
        var producer = endpoint.CreateProducer();
        producer.Should().NotBeNull();
    }

    [Fact]
    public void CreateEndpoint_CreateConsumer_ReturnsConsumer()
    {
        var component = new FirebaseStorageComponent();
        var endpoint = component.CreateEndpoint(MakeUri());
        var consumer = endpoint.CreateConsumer(Substitute.For<IProcessor>());
        consumer.Should().NotBeNull();
    }
}

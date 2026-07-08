using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc;

namespace redb.Route.Tests.Grpc;

public class GrpcComponentTests
{
    [Fact]
    public void Scheme_IsGrpc()
    {
        var component = new GrpcComponent();
        component.Scheme.Should().Be("grpc");
    }

    [Fact]
    public void CreateEndpoint_ReturnsGrpcEndpoint()
    {
        var component = new GrpcComponent();
        var uri = new EndpointUri("grpc", "/localhost:50051", "grpc:localhost:50051",
            new Dictionary<string, string>());
        var endpoint = component.CreateEndpoint(uri);
        endpoint.Should().BeOfType<GrpcEndpoint>();
    }

    [Fact]
    public void CreateEndpoint_DefaultOptions()
    {
        var component = new GrpcComponent();
        var uri = new EndpointUri("grpc", "/localhost:50051", "grpc:localhost:50051",
            new Dictionary<string, string>());
        var endpoint = (GrpcEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Deadline.Should().Be(30_000);
        endpoint.EndpointOptions.Plaintext.Should().BeTrue();
        endpoint.EndpointOptions.Port.Should().Be(50051);
        endpoint.EndpointOptions.Host.Should().Be("localhost");
        endpoint.EndpointOptions.Ssl.Should().BeFalse();
        endpoint.EndpointOptions.InOut.Should().BeTrue();
        endpoint.EndpointOptions.MaxSendMessageSize.Should().Be(4 * 1024 * 1024);
        endpoint.EndpointOptions.MaxReceiveMessageSize.Should().Be(4 * 1024 * 1024);
        endpoint.EndpointOptions.MaxRequestMessageSize.Should().Be(4 * 1024 * 1024);
    }

    [Fact]
    public void CreateEndpoint_BindsUriParameters()
    {
        var component = new GrpcComponent();
        var parameters = new Dictionary<string, string>
        {
            ["deadline"] = "5000",
            ["plaintext"] = "false",
            ["port"] = "9999",
            ["host"] = "127.0.0.1",
            ["inOut"] = "false",
            ["maxSendMessageSize"] = "1048576",
            ["maxReceiveMessageSize"] = "2097152"
        };
        var uri = new EndpointUri("grpc", "/localhost:9999", "grpc:localhost:9999", parameters);
        var endpoint = (GrpcEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Deadline.Should().Be(5000);
        endpoint.EndpointOptions.Plaintext.Should().BeFalse();
        endpoint.EndpointOptions.Port.Should().Be(9999);
        endpoint.EndpointOptions.Host.Should().Be("127.0.0.1");
        endpoint.EndpointOptions.InOut.Should().BeFalse();
        endpoint.EndpointOptions.MaxSendMessageSize.Should().Be(1048576);
        endpoint.EndpointOptions.MaxReceiveMessageSize.Should().Be(2097152);
    }

    [Fact]
    public void CreateEndpoint_InvalidPort_Throws()
    {
        var component = new GrpcComponent();
        var parameters = new Dictionary<string, string> { ["port"] = "70000" };
        var uri = new EndpointUri("grpc", "/localhost:70000", "grpc:localhost:70000", parameters);
        var act = () => component.CreateEndpoint(uri);
        act.Should().Throw<ArgumentException>().WithMessage("*Port*");
    }

    [Fact]
    public void CreateEndpoint_NegativeDeadline_Throws()
    {
        var component = new GrpcComponent();
        var parameters = new Dictionary<string, string> { ["deadline"] = "-1" };
        var uri = new EndpointUri("grpc", "/localhost:50051", "grpc:localhost:50051", parameters);
        var act = () => component.CreateEndpoint(uri);
        act.Should().Throw<ArgumentException>().WithMessage("*Deadline*");
    }

    [Fact]
    public void CreateEndpoint_SslWithoutCert_Throws()
    {
        var component = new GrpcComponent();
        var parameters = new Dictionary<string, string> { ["ssl"] = "true" };
        var uri = new EndpointUri("grpc", "/localhost:50051", "grpc:localhost:50051", parameters);
        var act = () => component.CreateEndpoint(uri);
        act.Should().Throw<ArgumentException>().WithMessage("*SslCertPath*");
    }

    [Fact]
    public void BuildProducerAddress_Plaintext_ReturnsHttp()
    {
        var component = new GrpcComponent();
        var parameters = new Dictionary<string, string> { ["plaintext"] = "true" };
        var uri = new EndpointUri("grpc", "/localhost:50051", "grpc:localhost:50051", parameters);
        var endpoint = (GrpcEndpoint)component.CreateEndpoint(uri);

        endpoint.BuildProducerAddress().Should().Be("http://localhost:50051");
    }

    [Fact]
    public void BuildProducerAddress_NoPlaintext_ReturnsHttps()
    {
        var component = new GrpcComponent();
        var parameters = new Dictionary<string, string> { ["plaintext"] = "false" };
        var uri = new EndpointUri("grpc", "/localhost:50051", "grpc:localhost:50051", parameters);
        var endpoint = (GrpcEndpoint)component.CreateEndpoint(uri);

        endpoint.BuildProducerAddress().Should().Be("https://localhost:50051");
    }

    [Fact]
    public void CreateProducer_ReturnsGrpcProducer()
    {
        var component = new GrpcComponent();
        var uri = new EndpointUri("grpc", "/localhost:50051", "grpc:localhost:50051",
            new Dictionary<string, string>());
        var endpoint = component.CreateEndpoint(uri);
        var producer = endpoint.CreateProducer();
        producer.Should().BeOfType<GrpcProducer>();
    }

    [Fact]
    public void CreateConsumer_ReturnsGrpcConsumer()
    {
        var component = new GrpcComponent();
        var uri = new EndpointUri("grpc", "/localhost:50051", "grpc:localhost:50051",
            new Dictionary<string, string>());
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<IProcessor>();
        var consumer = endpoint.CreateConsumer(processor);
        consumer.Should().BeOfType<GrpcConsumer>();
    }

    [Fact]
    public void CreateConsumer_NullProcessor_Throws()
    {
        var component = new GrpcComponent();
        var uri = new EndpointUri("grpc", "/localhost:50051", "grpc:localhost:50051",
            new Dictionary<string, string>());
        var endpoint = component.CreateEndpoint(uri);
        var act = () => endpoint.CreateConsumer(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

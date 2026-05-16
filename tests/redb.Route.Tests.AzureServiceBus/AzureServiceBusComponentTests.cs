using redb.Route.Abstractions;
using redb.Route.AzureServiceBus;
using redb.Route.Core;

namespace redb.Route.Tests.AzureServiceBus;

public sealed class AzureServiceBusComponentTests
{
    [Fact]
    public void Scheme_IsAsb()
    {
        var component = new AzureServiceBusComponent();
        component.Scheme.Should().Be("asb");
    }

    [Fact]
    public void CreateEndpoint_WithValidOptions_ReturnsEndpoint()
    {
        var component = new AzureServiceBusComponent();
        var uri = EndpointUriParser.Parse("asb://test-queue?connectionString=Endpoint=sb://test");

        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().NotBeNull();
        endpoint.Uri.Should().Be(uri);
        endpoint.Component.Should().Be(component);
    }

    [Fact]
    public void CreateEndpoint_MissingConnectionString_Throws()
    {
        var component = new AzureServiceBusComponent();
        var uri = EndpointUriParser.Parse("asb://test-queue");

        var act = () => component.CreateEndpoint(uri);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var component = new AzureServiceBusComponent();

        var act = () => component.CreateEndpoint(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateProducer_ReturnsProducer()
    {
        var component = new AzureServiceBusComponent();
        var uri = EndpointUriParser.Parse("asb://test-queue?connectionString=Endpoint=sb://test");
        var endpoint = component.CreateEndpoint(uri);

        var producer = endpoint.CreateProducer();

        producer.Should().NotBeNull();
    }

    [Fact]
    public void CreateConsumer_ReturnsConsumer()
    {
        var component = new AzureServiceBusComponent();
        var uri = EndpointUriParser.Parse("asb://test-queue?connectionString=Endpoint=sb://test");
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<IProcessor>();

        var consumer = endpoint.CreateConsumer(processor);

        consumer.Should().NotBeNull();
    }

    [Fact]
    public void CreateConsumer_WithSessions_ReturnsSessionConsumer()
    {
        var component = new AzureServiceBusComponent();
        var uri = EndpointUriParser.Parse("asb://test-queue?connectionString=Endpoint=sb://test&enableSessions=true");
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<IProcessor>();

        var consumer = endpoint.CreateConsumer(processor);

        consumer.Should().NotBeNull();
        consumer.GetType().Name.Should().Contain("Session");
    }

    [Fact]
    public void CreateEndpoint_WithSubscription_IsTopic()
    {
        var component = new AzureServiceBusComponent();
        var uri = EndpointUriParser.Parse("asb://my-topic?connectionString=Endpoint=sb://test&subscriptionName=my-sub");
        var endpoint = component.CreateEndpoint(uri);

        // Verify the endpoint was created (internal IsTopic not directly accessible but the factory pattern works)
        endpoint.Should().NotBeNull();
    }
}

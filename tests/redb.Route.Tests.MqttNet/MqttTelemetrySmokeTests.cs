using System.Diagnostics;
using MQTTnet;
using MQTTnet.Protocol;
using OpenTelemetry;
using OpenTelemetry.Trace;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.MqttNet;
using redb.Route.MqttNet.Connection;
using redb.Route.Telemetry;

namespace redb.Route.Tests.MqttNet;

/// <summary>Smoke test for the P1 transport span opened by <see cref="MqttProducer"/>.</summary>
public sealed class MqttTelemetrySmokeTests
{
    [Fact]
    public async Task MqttProducer_EmitsTransportSpanWithMessagingMqttTags()
    {
        var component = new MqttComponent();
        var pars = new Dictionary<string, string>
        {
            ["mode"] = "Publish",
            ["broker"] = "main"
        };
        var uri = new EndpointUri("mqtt", "out/topic", "mqtt:out/topic", pars);
        var endpoint = (MqttEndpoint)component.CreateEndpoint(uri);

        var mockClient = Substitute.For<IMqttClient>();
        mockClient.IsConnected.Returns(true);
        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)));
        var mockFactory = Substitute.For<IMqttClientFactory>();
        mockFactory.CreateConnectedClientAsync(
                Arg.Any<MqttBrokerOptions>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockClient));
        var mockRegistry = Substitute.For<IMqttBrokerRegistry>();
        mockRegistry.GetOptions("main").Returns(new MqttBrokerOptions { Server = "localhost" });
        endpoint.MqttComponent.ClientFactory = mockFactory;
        endpoint.MqttComponent.BrokerRegistry = mockRegistry;

        var producer = (MqttProducer)endpoint.CreateProducer();
        await producer.Start();

        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(RouteActivitySource.SourceName)
            .AddInMemoryExporter(activities)
            .Build()!;

        await producer.Process(new Exchange(new Message("hello")));
        await producer.Stop();

        tracer.ForceFlush(1000);
        activities.Should().NotBeEmpty();
        var activity = activities.First();
        activity.Source.Name.Should().Be(RouteActivitySource.SourceName);
        activity.Kind.Should().Be(ActivityKind.Producer);
        activity.GetTagItem("messaging.system").Should().Be("mqtt");
        activity.GetTagItem("messaging.operation").Should().Be("publish");
        activity.GetTagItem("messaging.destination.name").Should().Be("out/topic");
        activity.GetTagItem("redb.route.endpoint").Should().NotBeNull();
    }
}

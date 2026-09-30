using System.Diagnostics;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Formatter;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.MqttNet;
using redb.Route.MqttNet.Connection;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.MqttNet;

/// <summary>
/// The trace context travels in MQTT 5 user properties, and only on a connection that speaks v5: the producer writes it
/// (replacing a <c>traceparent</c> copied from an earlier hop), the consumer reads it and parents its span on it. Under
/// 3.1.1 nothing is written — MQTTnet refuses user properties there — and the receive span is a root. Without a context
/// the span is a root, never a child of the activity the consumer inherited. A failed route marks it red; our own stop
/// does not. <c>EnableTelemetry=false</c> opens no span on either side. Broker: route-mosquitto on localhost:11883.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MqttTraceTests
{
    private const string Server = "localhost";
    private const int Port = 11883;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _topic = $"test/trace/{Guid.NewGuid():N}";

    private string SubscribeUri => $"mqtt:{_topic}?mode=Subscribe&server={Server}&port={Port}&qos=1";
    private string PublishUri => $"mqtt:{_topic}?mode=Publish&server={Server}&port={Port}&qos=1";

    private RouteTelemetryProbe Spans(ActivityKind? kind = null)
        => new(a => (kind is null || a.Kind == kind)
                    && RouteTelemetryProbe.Tag(a, "messaging.destination.name") == _topic);

    private static async Task<RouteContext> Start(Action<InlineRouteBuilder> routes, bool telemetry = true,
        MqttProtocolVersion version = MqttProtocolVersion.V500)
    {
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new MqttComponent { ClientFactory = new VersionedClientFactory(version) });
        ctx.AddRoutes(routes);
        using (new Activity("host startup").SetIdFormat(ActivityIdFormat.W3C).Start())
            await ctx.Start();
        return ctx;
    }

    private static async Task<IMqttClient> RawClient()
    {
        var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(Server, Port)
            .WithClientId($"test-raw-{Guid.NewGuid():N}")
            .WithCleanSession()
            .Build());
        return client;
    }

    private async Task Publish(string payload, string? traceparent = null)
    {
        using var client = await RawClient();
        var builder = new MqttApplicationMessageBuilder().WithTopic(_topic).WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);
        if (traceparent is not null)
            builder.WithUserProperty("traceparent", traceparent);
        await client.PublishAsync(builder.Build());
        await client.DisconnectAsync();
    }

    private async Task<(IMqttClient Client, ChannelReader<MqttApplicationMessage> Messages)> RawSubscriber()
    {
        var client = await RawClient();
        var messages = Channel.CreateUnbounded<MqttApplicationMessage>();
        client.ApplicationMessageReceivedAsync += e =>
        {
            messages.Writer.TryWrite(e.ApplicationMessage);
            return Task.CompletedTask;
        };
        await client.SubscribeAsync(_topic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);
        return (client, messages.Reader);
    }

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

#pragma warning disable CS0618 // MQTTnet marks Value as obsolete in favor of ValueBuffer
    private static string[] UserProperty(MqttApplicationMessage message, string name)
        => message.UserProperties?.Where(p => p.Name == name).Select(p => p.Value).ToArray() ?? [];
#pragma warning restore CS0618

    [Fact]
    public async Task Under_v5_the_context_travels_from_the_send_to_the_receive()
    {
        using var probe = Spans();

        await using (var ctx = await Start(r =>
                     {
                         r.From("direct:send").To(PublishUri);
                         r.From(SubscribeUri).Process(_ => { });
                     }))
        {
            await Task.Delay(300);
            using var template = new ProducerTemplate(ctx);
            template.Start();
            using (new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start())
                await template.SendAsync("direct:send", "hello");
            await Until(() => probe.Activities.Any(a => a.Kind == ActivityKind.Consumer));
        }

        var send = probe.Activities.Should().ContainSingle(a => a.Kind == ActivityKind.Producer).Subject;
        var receive = probe.Activities.Should().ContainSingle(a => a.Kind == ActivityKind.Consumer).Subject;
        receive.TraceId.Should().Be(send.TraceId);
        receive.ParentSpanId.Should().Be(send.SpanId, "the receive span is a child of the send span that carried the context");
    }

    [Fact]
    public async Task The_send_replaces_a_traceparent_copied_from_an_earlier_hop()
    {
        using var probe = Spans(ActivityKind.Producer);
        var (subscriber, messages) = await RawSubscriber();
        using var _ = subscriber;
        var stale = $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01";

        await using (var ctx = await Start(r => r.From("direct:send").To(PublishUri)))
        {
            using var template = new ProducerTemplate(ctx);
            template.Start();
            var message = new Message("hello");
            message.Headers[MqttHeaders.UserProperties] = new Dictionary<string, string> { ["traceparent"] = stale, ["k"] = "v" };
            await template.SendAsync("direct:send", message);
        }

        using var cts = new CancellationTokenSource(Wait);
        var received = await messages.ReadAsync(cts.Token);
        var send = probe.Activities.Should().ContainSingle().Subject;
        UserProperty(received, "traceparent").Should().Equal($"00-{send.TraceId}-{send.SpanId}-01");
        UserProperty(received, "k").Should().Equal("v");
    }

    [Fact]
    public async Task Each_message_without_a_context_opens_a_root_span_even_under_an_ambient_activity()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await Start(r => r.From(SubscribeUri).Process(_ => { })))
        {
            await Publish("a");
            await Publish("b");
            await Until(() => probe.Activities.Count >= 2);
        }

        probe.Activities.Should().HaveCount(2, "one span per routed message");
        probe.Activities.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "a message without a context starts a trace rather than joining the host's");
    }

    [Fact]
    public async Task Nothing_arriving_opens_no_span()
    {
        using var probe = Spans();

        await using (await Start(r => r.From(SubscribeUri).Process(_ => { })))
            await Task.Delay(800);

        probe.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_route_marks_the_span_red()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await Start(r => r.From(SubscribeUri).Process(_ => throw new InvalidOperationException("route failed"))))
        {
            await Publish("fail");
            await Until(() => probe.Activities.Count >= 1);
        }

        probe.Activities.Should().NotBeEmpty();
        probe.Activities.Should().OnlyContain(a => a.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Our_stop_cancelling_the_route_does_not_mark_the_span()
    {
        using var probe = Spans(ActivityKind.Consumer);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ci.Arg<CancellationToken>());
            });
        var component = new MqttComponent { ClientFactory = new DefaultMqttClientFactory() };
        var endpoint = (MqttEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(SubscribeUri));
        var consumer = endpoint.CreateConsumer(processor);
        using var stop = new CancellationTokenSource();
        await consumer.Start(stop.Token);
        try
        {
            await Publish("wait");
            (await Task.WhenAny(entered.Task, Task.Delay(Wait))).Should().Be(entered.Task);

            // Our stop from above: the token the consumer was started with.
            await stop.CancelAsync();
            await Until(() => probe.Activities.Count >= 1);
        }
        finally
        {
            await consumer.Stop();
        }

        probe.Activities.Should().ContainSingle().Which.Status.Should().NotBe(ActivityStatusCode.Error,
            "our own stop cancelling the route is not a failure");
    }

    [Fact]
    public async Task Under_3_1_1_the_receive_span_is_a_root()
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await Start(r => r.From(SubscribeUri).Process(_ => { }), version: MqttProtocolVersion.V311))
        {
            await Publish("old", $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01");
            await Until(() => probe.Activities.Count >= 1);
        }

        probe.Activities.Should().ContainSingle().Which.ParentSpanId.Should().Be(default(ActivitySpanId));
    }

    [Fact]
    public async Task Under_3_1_1_the_send_writes_no_context_and_does_not_fail()
    {
        // MQTTnet throws NotSupportedException for user properties on a 3.1.1 connection.
        var (subscriber, messages) = await RawSubscriber();
        using var _ = subscriber;

        await using (var ctx = await Start(r => r.From("direct:send").To(PublishUri), version: MqttProtocolVersion.V311))
        {
            using var template = new ProducerTemplate(ctx);
            template.Start();
            using (new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start())
                await template.SendAsync("direct:send", "old");
        }

        using var cts = new CancellationTokenSource(Wait);
        var received = await messages.ReadAsync(cts.Token);
        UserProperty(received, "traceparent").Should().BeEmpty();
    }

    [Fact]
    public async Task Tracing_off_opens_no_span_on_either_side()
    {
        using var probe = Spans();
        var routed = new TaskCompletionSource();

        await using (var ctx = await Start(r =>
                     {
                         r.From("direct:send").To(PublishUri);
                         r.From(SubscribeUri).Process(_ => routed.TrySetResult());
                     }, telemetry: false))
        {
            await Task.Delay(300);
            using var template = new ProducerTemplate(ctx);
            template.Start();
            await template.SendAsync("direct:send", "off");
            (await Task.WhenAny(routed.Task, Task.Delay(Wait))).Should().Be(routed.Task);
        }

        probe.Activities.Should().BeEmpty();
    }

    /// <summary>The default client factory, on the protocol version the test asks for.</summary>
    private sealed class VersionedClientFactory(MqttProtocolVersion version) : IMqttClientFactory
    {
        public async Task<IMqttClient> CreateConnectedClientAsync(
            MqttBrokerOptions options, string? clientIdOverride = null, CancellationToken ct = default)
        {
            var client = new MqttClientFactory().CreateMqttClient();
            await client.ConnectAsync(new MqttClientOptionsBuilder()
                .WithTcpServer(options.Server, options.Port)
                .WithClientId(clientIdOverride ?? options.ClientId ?? $"redb-route-{Guid.NewGuid():N}")
                .WithCleanSession(options.CleanSession)
                .WithProtocolVersion(version)
                .Build(), ct).ConfigureAwait(false);
            return client;
        }
    }
}

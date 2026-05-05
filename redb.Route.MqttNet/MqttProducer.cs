using System.Text;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.MqttNet.Connection;

namespace redb.Route.MqttNet;

/// <summary>
/// MQTT producer that publishes messages to a topic.
/// The exchange body is serialized to UTF-8 bytes as the MQTT payload.
/// </summary>
internal sealed class MqttProducer : ConnectableProducer
{
    private readonly MqttEndpoint _endpoint;
    private readonly MqttEndpointOptions _options;

    private IMqttClient? _client;

    internal MqttProducer(MqttEndpoint endpoint, MqttEndpointOptions options)
    {
        _endpoint = endpoint;
        _options = options;
    }

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => $"mqtt:{_options.Server}:{_options.Port}";

    /// <inheritdoc/>
    protected override async Task ConnectAsync(CancellationToken ct)
    {
        var brokerOptions = ResolveBrokerOptions();
        var factory = _endpoint.MqttComponent.ClientFactory ?? new DefaultMqttClientFactory();

        _client = await factory.CreateConnectedClientAsync(brokerOptions, _options.ClientId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override async Task DisconnectAsync(CancellationToken ct)
    {
        if (_client != null)
        {
            if (_client.IsConnected)
            {
                try
                {
                    await _client.DisconnectAsync(
                        new MqttClientDisconnectOptionsBuilder()
                            .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                            .Build(), ct).ConfigureAwait(false);
                }
                catch (Exception ex) { Logger?.LogDebug(ex, "MQTT: error disconnecting during stop"); }
            }

            _client.Dispose();
            _client = null;
        }
    }

    /// <inheritdoc/>
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (_client == null || !_client.IsConnected)
            throw new InvalidOperationException("MQTT producer is not connected. Call Start() first.");

        var topic = ResolvePublishTopic(exchange);
        var payload = SerializeBody(exchange.In.Body);
        var qos = ResolveQos(exchange);

        var builder = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(qos)
            .WithRetainFlag(ResolveRetain(exchange));

        // MQTT 5.0 properties
        var contentType = ResolveHeaderString(exchange, MqttHeaders.ContentType)
                          ?? _options.ResolveOption(_options.ContentType, exchange);
        if (contentType != null)
            builder.WithContentType(contentType);

        var responseTopic = ResolveHeaderString(exchange, MqttHeaders.ResponseTopic)
                            ?? _options.ResolveOption(_options.ResponseTopic, exchange);
        if (responseTopic != null)
            builder.WithResponseTopic(responseTopic);

        var correlationData = ResolveHeaderString(exchange, MqttHeaders.CorrelationData);
        if (correlationData != null)
            builder.WithCorrelationData(Encoding.UTF8.GetBytes(correlationData));

        var expiryInterval = ResolveExpiryInterval(exchange);
        if (expiryInterval > 0)
            builder.WithMessageExpiryInterval((uint)expiryInterval);

        // User properties from exchange headers
        if (exchange.In.Headers.TryGetValue(MqttHeaders.UserProperties, out var userPropsObj)
            && userPropsObj is Dictionary<string, string> userProps)
        {
            foreach (var (key, value) in userProps)
                builder.WithUserProperty(key, new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(value)));
        }

        var message = builder.Build();

        try
        {
            await _client.PublishAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger?.LogError(ex, "MQTT publish failed: topic={Topic}, QoS={Qos}, server={Server}:{Port}, connected={IsConnected}",
                topic, qos, _options.Server, _options.Port, _client?.IsConnected);
            throw;
        }

        // Set result headers
        exchange.In.Headers[MqttHeaders.Topic] = topic;
        exchange.In.Headers[MqttHeaders.Qos] = (int)qos;
        exchange.In.Headers[MqttHeaders.Retain] = ResolveRetain(exchange);

        if (_options.Broker != null)
            exchange.In.Headers[MqttHeaders.Broker] = _options.Broker;
    }

    private string ResolvePublishTopic(IExchange exchange)
    {
        // Allow overriding topic via header
        if (exchange.In.Headers.TryGetValue(MqttHeaders.Topic, out var topicObj)
            && topicObj is string topicStr && !string.IsNullOrEmpty(topicStr))
            return topicStr;

        return _options.ResolveOption(_endpoint.Topic, exchange) ?? _endpoint.Topic;
    }

    private MqttQualityOfServiceLevel ResolveQos(IExchange exchange)
    {
        // 1. Header override
        if (exchange.In.Headers.TryGetValue(MqttHeaders.Qos, out var qosObj) && qosObj is int qosInt)
        {
            return qosInt switch
            {
                0 => MqttQualityOfServiceLevel.AtMostOnce,
                1 => MqttQualityOfServiceLevel.AtLeastOnce,
                2 => MqttQualityOfServiceLevel.ExactlyOnce,
                _ => MqttQualityOfServiceLevel.AtMostOnce
            };
        }

        // 2. Expression (e.g. "${body['qos']}")
        if (!string.IsNullOrEmpty(_options.QosExpression))
        {
            var resolved = _options.ResolveOption(_options.QosExpression, exchange);
            if (resolved != null && int.TryParse(resolved, out var exprQos))
            {
                return exprQos switch
                {
                    0 => MqttQualityOfServiceLevel.AtMostOnce,
                    1 => MqttQualityOfServiceLevel.AtLeastOnce,
                    2 => MqttQualityOfServiceLevel.ExactlyOnce,
                    _ => MqttQualityOfServiceLevel.AtMostOnce
                };
            }
        }

        // 3. Static default
        return _options.Qos switch
        {
            0 => MqttQualityOfServiceLevel.AtMostOnce,
            1 => MqttQualityOfServiceLevel.AtLeastOnce,
            2 => MqttQualityOfServiceLevel.ExactlyOnce,
            _ => MqttQualityOfServiceLevel.AtMostOnce
        };
    }

    private bool ResolveRetain(IExchange exchange)
    {
        if (exchange.In.Headers.TryGetValue(MqttHeaders.Retain, out var retainObj) && retainObj is bool retainBool)
            return retainBool;

        return _options.Retain;
    }

    private int ResolveExpiryInterval(IExchange exchange)
    {
        // 1. Header override
        if (exchange.In.Headers.TryGetValue(MqttHeaders.MessageExpiryInterval, out var expiryObj) && expiryObj is int expiryInt)
            return expiryInt;

        // 2. Expression (e.g. "${body['ttl']}")
        if (!string.IsNullOrEmpty(_options.MessageExpiryIntervalExpression))
        {
            var resolved = _options.ResolveOption(_options.MessageExpiryIntervalExpression, exchange);
            if (resolved != null && int.TryParse(resolved, out var exprExpiry))
                return exprExpiry;
        }

        // 3. Static default
        return _options.MessageExpiryInterval;
    }

    private static string? ResolveHeaderString(IExchange exchange, string header)
    {
        if (exchange.In.Headers.TryGetValue(header, out var val) && val is string s && !string.IsNullOrEmpty(s))
            return s;
        return null;
    }

    private static byte[] SerializeBody(object? body)
    {
        return body switch
        {
            null => [],
            byte[] bytes => bytes,
            ReadOnlyMemory<byte> rom => rom.ToArray(),
            ArraySegment<byte> seg => seg.ToArray(),
            string str => Encoding.UTF8.GetBytes(str),
            _ => Encoding.UTF8.GetBytes(body.ToString() ?? string.Empty)
        };
    }

    private MqttBrokerOptions ResolveBrokerOptions()
    {
        if (_options.Broker != null)
        {
            var registry = _endpoint.MqttComponent.BrokerRegistry
                ?? throw new InvalidOperationException(
                    $"Broker '{_options.Broker}' is specified but no IMqttBrokerRegistry is registered.");

            return registry.GetOptions(_options.Broker);
        }

        if (_options.Server != null)
        {
            return new MqttBrokerOptions
            {
                Server = _options.Server,
                Port = _options.Port > 0 ? _options.Port : 1883,
                Username = _options.Username,
                Password = _options.Password,
                ClientId = _options.ClientId,
                UseTls = _options.UseTls,
                KeepAlive = _options.KeepAlive,
                CleanSession = _options.CleanSession
            };
        }

        throw new InvalidOperationException("Either Broker or Server must be configured.");
    }
}

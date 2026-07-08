using System.Buffers;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.MqttNet.Connection;

namespace redb.Route.MqttNet;

/// <summary>
/// MQTT consumer that subscribes to a topic and forwards messages to the route processor.
/// Uses MQTTnet's <see cref="IMqttClient"/> for the connection.
/// </summary>
internal sealed class MqttConsumer : IConsumer
{
    private readonly MqttEndpoint _endpoint;
    public IEndpoint Endpoint => _endpoint;
    private readonly IProcessor _processor;
    private readonly MqttEndpointOptions _options;

    private IMqttClient? _client;
    private readonly InflightDrainGuard _drain = new();
    private ILogger? _logger;

    // Concurrent-dispatch pool (only when ConcurrentConsumers > 1). Received messages are handed to a
    // bounded channel + N workers; each message is acknowledged only AFTER its worker finishes.
    private Channel<MqttDispatch>? _queue;
    private Task[]? _workers;

    private readonly record struct MqttDispatch(IExchange Exchange, MqttApplicationMessageReceivedEventArgs Args);

    /// <summary>Drain timeout for graceful stop (default 30s).</summary>
    internal TimeSpan DrainTimeout { get => _drain.DrainTimeout; set => _drain.DrainTimeout = value; }

    internal MqttConsumer(MqttEndpoint endpoint, IProcessor processor, MqttEndpointOptions options)
    {
        _endpoint = endpoint;
        _processor = processor;
        _options = options;
        _logger = (endpoint.Component as ComponentBase)?.Logger;
    }

    /// <inheritdoc/>
    public async Task Start(CancellationToken ct = default)
    {
        _drain.Start(ct);

        try
        {
            var brokerOptions = ResolveBrokerOptions();
            var factory = _endpoint.MqttComponent.ClientFactory ?? new DefaultMqttClientFactory();

            _client = await factory.CreateConnectedClientAsync(brokerOptions, _options.ClientId, ct)
                .ConfigureAwait(false);

            var topicFilter = BuildTopicFilter();
            var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(topicFilter)
                .Build();

            await _client.SubscribeAsync(subscribeOptions, ct).ConfigureAwait(false);

            // Concurrent dispatch: start N workers that process messages in parallel with manual
            // ack-after-process. Serial (default) processes inline in the handler with MQTTnet auto-ack.
            if (_options.ConcurrentConsumers > 1)
            {
                _queue = Channel.CreateBounded<MqttDispatch>(new BoundedChannelOptions(_options.ConcurrentConsumers)
                {
                    FullMode = BoundedChannelFullMode.Wait, // backpressure: throttle the receive loop to pool capacity
                    SingleReader = false,
                    SingleWriter = true // MQTTnet invokes the received-handler serially on one client
                });
                _workers = new Task[_options.ConcurrentConsumers];
                for (var i = 0; i < _options.ConcurrentConsumers; i++)
                    _workers[i] = Task.Run(() => WorkerLoop(_drain.ProcessingToken));
            }

            // Attach handler AFTER subscribe to avoid receiving unrelated events on shared client
            _client.ApplicationMessageReceivedAsync += OnMessageReceived;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "MQTT consumer Start failed for topic {Topic}", GetEffectiveTopic());
            if (_client != null) { _client.Dispose(); _client = null; }
            _drain.Dispose();
            throw;
        }

        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("MQTT consumer started: {Server}:{Port} topic={Topic}", _options.Server, _options.Port, GetEffectiveTopic());
    }

    /// <inheritdoc/>
    public async Task Stop(CancellationToken ct = default)
    {
        if (_client != null)
        {
            _client.ApplicationMessageReceivedAsync -= OnMessageReceived;

            // Concurrent mode: stop accepting new work; workers finish the queued backlog.
            _queue?.Writer.TryComplete();

            // Drain in-flight (waits for the inflight count to hit zero; force-cancels on timeout)
            await _drain.DrainAsync(ct, _logger, "mqtt").ConfigureAwait(false);

            if (_workers != null)
            {
                try { await Task.WhenAll(_workers).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                _workers = null;
            }

            if (_client.IsConnected)
            {
                try
                {
                    await _client.UnsubscribeAsync(
                        new MqttClientUnsubscribeOptionsBuilder()
                            .WithTopicFilter(GetEffectiveTopic())
                            .Build(), ct).ConfigureAwait(false);
                }
                catch (Exception ex) { _logger?.LogWarning(ex, "MQTT: error unsubscribing during stop"); }

                try
                {
                    await _client.DisconnectAsync(
                        new MqttClientDisconnectOptionsBuilder()
                            .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                            .Build(), ct).ConfigureAwait(false);
                }
                catch (Exception ex) { _logger?.LogWarning(ex, "MQTT: error disconnecting during stop"); }
            }

            _client.Dispose();
            _client = null;
        }

        _drain.Dispose();
        _logger ??= (_endpoint.Component as ComponentBase)?.Logger;
        _logger?.LogInformation("MQTT consumer stopped: {Server}:{Port}", _options.Server, _options.Port);
    }

    private async Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs args)
    {
        if (_drain.ProcessingToken.IsCancellationRequested)
            return;

        var exchange = CreateExchange(args.ApplicationMessage);

        // Serial (default): process inline; MQTTnet auto-acknowledges on handler return (after processing).
        if (_queue is null)
        {
            _drain.Increment();
            try
            {
                await _processor.Process(exchange, _drain.ProcessingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "MQTT message processing failed: topic={Topic}",
                    args.ApplicationMessage.Topic);
            }
            finally
            {
                _drain.Decrement();
                await exchange.DisposeAsync().ConfigureAwait(false);
            }
            return;
        }

        // Concurrent: hand off to a worker and acknowledge only after it finishes (manual ack),
        // so a fast producer keeps the receive loop moving while up to N messages process in parallel.
        args.AutoAcknowledge = false;
        _drain.Increment(); // balanced by ProcessAndAck's finally (worker path or inline fallback)
        try
        {
            await _queue.Writer.WriteAsync(new MqttDispatch(exchange, args), _drain.ProcessingToken).ConfigureAwait(false);
        }
        catch (Exception) // ChannelClosedException / cancellation during shutdown
        {
            // Could not enqueue — process inline so the message is not lost.
            await ProcessAndAck(exchange, args, _drain.ProcessingToken).ConfigureAwait(false);
        }
    }

    private async Task WorkerLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var d in _queue!.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await ProcessAndAck(d.Exchange, d.Args, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    private async Task ProcessAndAck(IExchange exchange, MqttApplicationMessageReceivedEventArgs args, CancellationToken ct)
    {
        try
        {
            await _processor.Process(exchange, ct).ConfigureAwait(false);

            // Acknowledge only after successful processing — preserves at-least-once for QoS 1/2.
            try { await args.AcknowledgeAsync(ct).ConfigureAwait(false); }
            catch (Exception ackEx)
            {
                _logger?.LogWarning(ackEx, "MQTT: manual acknowledge failed: topic={Topic}",
                    args.ApplicationMessage.Topic);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Do NOT acknowledge on failure — the broker redelivers on reconnect (QoS 1/2).
            _logger?.LogError(ex, "MQTT message processing failed: topic={Topic}",
                args.ApplicationMessage.Topic);
        }
        finally
        {
            _drain.Decrement();
            await exchange.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Exchange CreateExchange(MqttApplicationMessage message)
    {
        // Body: raw payload bytes (consumers pass byte[], deserialization is deferred)
        object? body = message.Payload.Length > 0
            ? message.Payload.ToArray()
            : null;

        var exchange = Exchange.Create(new Message(body), _endpoint.ScopeFactory);

        exchange.In.Headers[MqttHeaders.Topic] = message.Topic;
        exchange.In.Headers[MqttHeaders.Qos] = (int)message.QualityOfServiceLevel;
        exchange.In.Headers[MqttHeaders.Retain] = message.Retain;

        if (message.ContentType != null)
        {
            exchange.In.Headers[MqttHeaders.ContentType] = message.ContentType;
            exchange.In.ContentType = message.ContentType;
        }

        if (message.ResponseTopic != null)
            exchange.In.Headers[MqttHeaders.ResponseTopic] = message.ResponseTopic;

        if (message.CorrelationData is { Length: > 0 })
            exchange.In.Headers[MqttHeaders.CorrelationData] = Encoding.UTF8.GetString(message.CorrelationData);

        if (message.MessageExpiryInterval > 0)
            exchange.In.Headers[MqttHeaders.MessageExpiryInterval] = (int)message.MessageExpiryInterval;

        if (_options.Broker != null)
            exchange.In.Headers[MqttHeaders.Broker] = _options.Broker;

        if (_options.ClientId != null)
            exchange.In.Headers[MqttHeaders.ClientId] = _options.ClientId;

        // MQTT 5.0 User Properties → Dictionary
        if (message.UserProperties is { Count: > 0 })
        {
            var userProps = new Dictionary<string, string>(message.UserProperties.Count);
#pragma warning disable CS0618 // MQTTnet marks Value as obsolete in favor of ValueBuffer
            foreach (var prop in message.UserProperties)
                userProps[prop.Name] = prop.Value;
#pragma warning restore CS0618
            exchange.In.Headers[MqttHeaders.UserProperties] = userProps;
        }

        return exchange;
    }

    private MqttTopicFilter BuildTopicFilter()
    {
        var topic = GetEffectiveTopic();
        var qos = _options.Qos switch
        {
            0 => MqttQualityOfServiceLevel.AtMostOnce,
            1 => MqttQualityOfServiceLevel.AtLeastOnce,
            2 => MqttQualityOfServiceLevel.ExactlyOnce,
            _ => MqttQualityOfServiceLevel.AtMostOnce
        };

        return new MqttTopicFilter { Topic = topic, QualityOfServiceLevel = qos };
    }

    private string GetEffectiveTopic()
    {
        var topic = _endpoint.Topic;

        // MQTT 5.0 shared subscriptions: $share/{group}/{topic}
        if (_options.SharedSubscription != null)
            return $"$share/{_options.SharedSubscription}/{topic}";

        return topic;
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

using IBM.XMS;
using Microsoft.Extensions.Logging;
using RouteMessage = redb.Route.Core.Message;
using XmsMessage = IBM.XMS.IMessage;

namespace redb.Route.IbmMq;

/// <summary>
/// Event-driven RPC reply receiver for <see cref="IbmMqProducer"/> — the client leg of request-reply.
/// <para>
/// Replaces the producer's poll-MQGET reply loop with an XMS <c>MessageListener</c> on the reply queue,
/// so waiting for the response also drops from the managed client's ~500&#160;ms poll tick to single-digit
/// milliseconds. Opt-in via <c>receiveMode=listener</c>. The request itself is still sent over IBM.WMQ;
/// only reply <b>reception</b> moves to XMS, so send behaviour is unchanged.
/// </para>
/// <para>
/// For a dynamic reply queue the receiver creates an XMS <b>temporary queue</b> — owned by this XMS
/// connection, which also consumes it, so there is no cross-connection ownership issue — and exposes its
/// bare MQ queue name via <see cref="ReplyQueueName"/> for the producer to stamp on the request's
/// <c>ReplyToQueueName</c>. The (non-JMS) MQ responder then puts the reply there by name.
/// </para>
/// </summary>
internal sealed class IbmMqXmsReplyReceiver
{
    private readonly IbmMqEndpointOptions _options;
    private readonly ILogger? _logger;
    private readonly Action<string, RouteMessage> _onReply; // (correlationKey, response)

    private IConnection? _connection;
    private ISession? _session;
    private IMessageConsumer? _consumer;
    private IDestination? _tempReplyQueue;

    /// <summary>Bare MQ queue name to set on outgoing requests' <c>ReplyToQueueName</c>.</summary>
    public string ReplyQueueName { get; private set; } = string.Empty;

    public IbmMqXmsReplyReceiver(IbmMqEndpointOptions options, ILogger? logger, Action<string, RouteMessage> onReply)
    {
        _options = options;
        _logger = logger;
        _onReply = onReply;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        // Connection/session setup is synchronous and blocking — push it off the caller's thread.
        await Task.Run(() =>
        {
            var factory = BuildConnectionFactory(_options);

            _connection = string.IsNullOrEmpty(_options.User)
                ? factory.CreateConnection()
                : factory.CreateConnection(_options.User, _options.Password);

            _session = _connection.CreateSession(transacted: false, AcknowledgeMode.AutoAcknowledge);

            IDestination replyDest;
            if (string.IsNullOrEmpty(_options.ReplyToQueue))
            {
                _tempReplyQueue = _session.CreateTemporaryQueue();
                replyDest = _tempReplyQueue;
                ReplyQueueName = ExtractQueueName(_tempReplyQueue);
            }
            else
            {
                replyDest = _session.CreateQueue(_options.ReplyToQueue);
                ReplyQueueName = _options.ReplyToQueue;
            }

            _consumer = _session.CreateConsumer(replyDest);
            _consumer.MessageListener = OnReply;

            _connection.Start();
        }, ct).ConfigureAwait(false);

        _logger?.LogDebug("IBM MQ XMS RPC reply receiver started: replyQueue={ReplyQueue}", ReplyQueueName);
    }

    public Task StopAsync(CancellationToken ct)
    {
        try { _connection?.Stop(); } catch (Exception ex) { _logger?.LogDebug(ex, "IBM MQ XMS reply: error stopping connection"); }
        try { _consumer?.Close(); } catch (Exception ex) { _logger?.LogDebug(ex, "IBM MQ XMS reply: error closing consumer"); }
        try { _tempReplyQueue?.Dispose(); } catch (Exception ex) { _logger?.LogDebug(ex, "IBM MQ XMS reply: error deleting temp queue"); }
        try { _session?.Close(); } catch (Exception ex) { _logger?.LogDebug(ex, "IBM MQ XMS reply: error closing session"); }
        try { _connection?.Close(); } catch (Exception ex) { _logger?.LogDebug(ex, "IBM MQ XMS reply: error closing connection"); }

        _consumer = null;
        _tempReplyQueue = null;
        _session = null;
        _connection = null;
        return Task.CompletedTask;
    }

    private void OnReply(XmsMessage message)
    {
        try
        {
            // The responder sets the reply's MQMD CorrelId to the request's MsgId; match on it, exactly
            // like the poll loop keys `_pendingResponses` by hex(reply.CorrelationId).
            byte[]? corr = null;
            try { corr = message.JMSCorrelationIDAsBytes; } catch { /* no correlation */ }
            var correlKey = IbmMqMessageHelper.BytesToHex(corr ?? Array.Empty<byte>());

            _onReply(correlKey, BuildResponse(message));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "IBM MQ XMS: failed to handle RPC reply");
        }
    }

    private RouteMessage BuildResponse(XmsMessage message)
    {
        object body;
        string? contentType;

        switch (message)
        {
            case ITextMessage text:
                body = text.Text ?? string.Empty;
                contentType = "text/plain";
                break;
            case IBytesMessage bytes:
                var length = (int)bytes.BodyLength;
                var buffer = new byte[length];
                if (length > 0) bytes.ReadBytes(buffer);
                body = buffer;
                contentType = "application/octet-stream";
                break;
            default:
                body = string.Empty;
                contentType = null;
                break;
        }

        var response = new RouteMessage(body) { ContentType = contentType };

        // Same redbIbmMq.* metadata and user headers the poll reply path stamps on the response.
        IbmMqXmsHeaderMapper.CopyToHeaders(message, response, ReplyQueueName, _options);

        return response;
    }

    /// <summary>Resolves the bare MQ queue name from an XMS destination (property or <c>queue://qmgr/NAME</c> name).</summary>
    private static string ExtractQueueName(IDestination dest)
    {
        try
        {
            var wmqName = dest.GetStringProperty(XMSC.WMQ_QUEUE_NAME);
            if (!string.IsNullOrEmpty(wmqName)) return wmqName.Trim();
        }
        catch { /* fall through to Name */ }

        var name = dest.Name ?? string.Empty;
        var slash = name.LastIndexOf('/');
        return (slash >= 0 ? name[(slash + 1)..] : name).Trim();
    }

    private static IConnectionFactory BuildConnectionFactory(IbmMqEndpointOptions options)
    {
        var factory = XMSFactoryFactory.GetInstance(XMSC.CT_WMQ).CreateConnectionFactory();

        factory.SetStringProperty(XMSC.WMQ_HOST_NAME, options.Host);
        factory.SetIntProperty(XMSC.WMQ_PORT, options.Port);
        factory.SetStringProperty(XMSC.WMQ_CHANNEL, options.Channel);
        factory.SetStringProperty(XMSC.WMQ_QUEUE_MANAGER, options.QueueManager);
        factory.SetIntProperty(XMSC.WMQ_CONNECTION_MODE, XMSC.WMQ_CM_CLIENT);

        if (!string.IsNullOrEmpty(options.ClientId))
            factory.SetStringProperty(XMSC.CLIENT_ID, options.ClientId);

        if (!string.IsNullOrEmpty(options.SslCipherSpec))
            factory.SetStringProperty(XMSC.WMQ_SSL_CIPHER_SPEC, options.SslCipherSpec);

        return factory;
    }
}

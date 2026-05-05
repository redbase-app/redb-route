using System.Text.Json;
using FcmMessage = FirebaseAdmin.Messaging.Message;
using FcmNotification = FirebaseAdmin.Messaging.Notification;
using FirebaseAdmin.Messaging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Firebase;

/// <summary>
/// FCM producer — sends push notifications via Firebase Cloud Messaging HTTP v1 API.
/// Supports Token, Topic, and Condition targeting with platform-specific configuration.
/// </summary>
internal sealed class FcmProducer : ConnectableProducer
{
    private readonly FcmEndpoint _endpoint;
    private readonly FcmEndpointOptions _options;
    private FirebaseMessaging? _messaging;

    /// <inheritdoc />
    protected override IEndpoint ProducerEndpoint => _endpoint;

    /// <inheritdoc />
    protected override string ProducerName => _endpoint.Uri.NormalizedKey;

    internal FcmProducer(FcmEndpoint endpoint, FcmEndpointOptions options)
    {
        _endpoint = endpoint;
        _options = options;
    }

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken ct)
    {
        var provider = ResolveCredentialProvider();
        var app = provider.GetOrCreateApp(_options.CredentialPath, _options.ProjectId);
        _messaging = FirebaseMessaging.GetMessaging(app);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task DisconnectAsync(CancellationToken ct)
    {
        _messaging = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        EnsureStarted();

        var message = BuildMessage(exchange);
        var messageId = await _messaging!.SendAsync(message, _options.DryRun, ct).ConfigureAwait(false);

        exchange.In.Headers[FcmHeaders.MessageId] = messageId;
        _endpoint.RecordMessageOut();
    }

    private FcmMessage BuildMessage(IExchange exchange)
    {
        var message = new FcmMessage();

        // 1. Target resolution (header override > option)
        switch (_options.MessageType)
        {
            case FcmMessageType.Token:
                message.Token = exchange.In.GetHeader<string>(FcmHeaders.Token)
                                ?? _options.Token?.Resolve(exchange)
                                ?? throw new InvalidOperationException("FCM Token is required but not set");
                break;
            case FcmMessageType.Topic:
                message.Topic = exchange.In.GetHeader<string>(FcmHeaders.Topic)
                                ?? _options.Topic?.Resolve(exchange)
                                ?? throw new InvalidOperationException("FCM Topic is required but not set");
                break;
            case FcmMessageType.Condition:
                message.Condition = exchange.In.GetHeader<string>(FcmHeaders.Condition)
                                    ?? _options.Condition?.Resolve(exchange)
                                    ?? throw new InvalidOperationException("FCM Condition is required but not set");
                break;
        }

        // 2. Notification (unless DataOnly)
        if (!_options.DataOnly)
        {
            var title = exchange.In.GetHeader<string>(FcmHeaders.Title)
                        ?? _options.Title?.Resolve(exchange);
            var body = exchange.In.GetHeader<string>(FcmHeaders.Body)
                       ?? _options.Body?.Resolve(exchange)
                       ?? exchange.In.Body?.ToString();

            if (title is not null || body is not null)
            {
                message.Notification = new FcmNotification
                {
                    Title = title,
                    Body = body,
                    ImageUrl = _options.ImageUrl
                };
            }
        }

        // 3. Data payload from headers with prefix
        var data = new Dictionary<string, string>();
        foreach (var (key, value) in exchange.In.Headers)
        {
            if (key.StartsWith(FcmHeaders.DataPrefix, StringComparison.Ordinal) && value is not null)
                data[key[FcmHeaders.DataPrefix.Length..]] = value.ToString()!;
        }

        // For DataOnly: body as Dictionary or JSON
        if (_options.DataOnly && exchange.In.Body is not null)
        {
            if (exchange.In.Body is IDictionary<string, string> bodyDict)
            {
                foreach (var kv in bodyDict)
                    data[kv.Key] = kv.Value;
            }
            else if (exchange.In.Body is IDictionary<string, object?> bodyObjDict)
            {
                foreach (var kv in bodyObjDict)
                    data[kv.Key] = kv.Value?.ToString() ?? "";
            }
            else
            {
                data["payload"] = exchange.In.Body.ToString() ?? "";
            }
        }

        if (data.Count > 0)
            message.Data = data;

        // 4. Platform-specific configuration
        message.Android = BuildAndroidConfig();
        message.Apns = BuildApnsConfig();
        message.Webpush = BuildWebpushConfig();

        return message;
    }

    private AndroidConfig? BuildAndroidConfig()
    {
        if (_options.AndroidPriority is null && _options.AndroidTtlSeconds is null && _options.AndroidChannelId is null)
            return null;

        var config = new AndroidConfig();
        if (_options.AndroidPriority is not null)
            config.Priority = _options.AndroidPriority.Equals("high", StringComparison.OrdinalIgnoreCase)
                ? Priority.High : Priority.Normal;

        if (_options.AndroidTtlSeconds is not null)
            config.TimeToLive = TimeSpan.FromSeconds(_options.AndroidTtlSeconds.Value);

        if (_options.AndroidChannelId is not null)
            config.Notification = new AndroidNotification { ChannelId = _options.AndroidChannelId };

        return config;
    }

    private ApnsConfig? BuildApnsConfig()
    {
        if (_options.ApnsPriority is null && _options.ApnsCollapseId is null
            && _options.ApnsContentAvailable is null && _options.ApnsMutableContent is null)
            return null;

        var config = new ApnsConfig();
        var headers = new Dictionary<string, string>();

        if (_options.ApnsPriority is not null)
            headers["apns-priority"] = _options.ApnsPriority;
        if (_options.ApnsCollapseId is not null)
            headers["apns-collapse-id"] = _options.ApnsCollapseId;

        if (headers.Count > 0)
            config.Headers = headers;

        if (_options.ApnsContentAvailable is not null || _options.ApnsMutableContent is not null)
        {
            config.Aps = new Aps();
            if (_options.ApnsContentAvailable == true)
                config.Aps.ContentAvailable = true;
            if (_options.ApnsMutableContent == true)
                config.Aps.MutableContent = true;
        }

        return config;
    }

    private WebpushConfig? BuildWebpushConfig()
    {
        if (_options.WebPushLink is null)
            return null;

        return new WebpushConfig
        {
            FcmOptions = new WebpushFcmOptions { Link = _options.WebPushLink }
        };
    }

    private IFirebaseCredentialProvider ResolveCredentialProvider()
    {
        // 1. Try ConnectionFactory from registry
        if (!string.IsNullOrEmpty(_options.ConnectionFactory))
        {
            var fromRegistry = _endpoint.FcmComponent.Context?
                .GetFromRegistry<IFirebaseCredentialProvider>(_options.ConnectionFactory);
            if (fromRegistry is not null) return fromRegistry;
        }

        // 2. Fall back to component-level provider
        return _endpoint.FcmComponent.CredentialProvider
               ?? throw new InvalidOperationException(
                   "No IFirebaseCredentialProvider available. Register via AddRedbRouteFirebase() or set ConnectionFactory.");
    }
}

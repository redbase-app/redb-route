using System.Diagnostics;
using System.Text.Json;
using FcmMessage = FirebaseAdmin.Messaging.Message;
using FcmNotification = FirebaseAdmin.Messaging.Notification;
using FirebaseAdmin.Messaging;
using redb.Route.Abstractions;
using redb.Route.Extensions;
using redb.Route.Core;
using redb.Route.Telemetry;

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

        using var activity = RouteTelemetryExtensions.StartTransportSpan(
            $"fcm {_options.Operation}", ActivityKind.Producer,
            "messaging.system", "fcm",
            _endpoint.Uri.NormalizedKey,
            operation: _options.Operation.ToString().ToLowerInvariant());

        switch (_options.Operation)
        {
            case FcmOperationType.Send:
                await ProcessSend(exchange, activity, ct).ConfigureAwait(false);
                break;
            case FcmOperationType.Multicast:
                await ProcessMulticast(exchange, activity, ct).ConfigureAwait(false);
                break;
            case FcmOperationType.SubscribeToTopic:
            case FcmOperationType.UnsubscribeFromTopic:
                await ProcessTopicManagement(exchange, activity).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unknown FCM operation: {_options.Operation}");
        }
    }

    private async Task ProcessSend(IExchange exchange, Activity? activity, CancellationToken ct)
    {
        var message = BuildMessage(exchange);

        // Destination for the span: topic/condition are addresses, a device token is a secret —
        // it goes into telemetry only as the literal "token".
        activity?.SetTag("messaging.destination.name", message.Topic ?? message.Condition ?? "token");

        var messageId = await _messaging!.SendAsync(message, _options.DryRun, ct).ConfigureAwait(false);

        exchange.In.Headers[FcmHeaders.MessageId] = messageId;
        // MessagesOut is recorded by the core (ToProcessor / the template) - ownership audit.
    }

    private async Task ProcessMulticast(IExchange exchange, Activity? activity, CancellationToken ct)
    {
        var tokens = ResolveTokens(exchange);
        activity?.SetTag("messaging.destination.name", "multicast");

        var message = new MulticastMessage { Tokens = tokens };
        FillPayload(message, exchange);

        var response = await _messaging!.SendEachForMulticastAsync(message, _options.DryRun, ct)
            .ConfigureAwait(false);

        exchange.In.Headers[FcmHeaders.SuccessCount] = response.SuccessCount;
        exchange.In.Headers[FcmHeaders.FailureCount] = response.FailureCount;
        // MessagesOut is recorded by the core (ToProcessor / the template) - ownership audit.
    }

    private async Task ProcessTopicManagement(IExchange exchange, Activity? activity)
    {
        var topic = exchange.In.GetHeader<string>(FcmHeaders.Topic)
                    ?? _options.Topic?.Resolve(exchange)
                    ?? throw new InvalidOperationException(
                        $"FCM Topic is required for the {_options.Operation} operation");
        var tokens = ResolveTokens(exchange);
        activity?.SetTag("messaging.destination.name", topic);

        var response = _options.Operation == FcmOperationType.SubscribeToTopic
            ? await _messaging!.SubscribeToTopicAsync(tokens, topic).ConfigureAwait(false)
            : await _messaging!.UnsubscribeFromTopicAsync(tokens, topic).ConfigureAwait(false);

        exchange.In.Headers[FcmHeaders.SuccessCount] = response.SuccessCount;
        exchange.In.Headers[FcmHeaders.FailureCount] = response.FailureCount;
        // MessagesOut is recorded by the core (ToProcessor / the template) - ownership audit.
    }

    /// <summary>Tokens for Multicast/Subscribe/Unsubscribe: Tokens header wins over the body.</summary>
    private static IReadOnlyList<string> ResolveTokens(IExchange exchange)
    {
        var source = exchange.In.Headers.TryGetValue(FcmHeaders.Tokens, out var header) && header is not null
            ? header
            : exchange.In.Body;

        return source switch
        {
            IReadOnlyList<string> list => list,
            IEnumerable<string> seq => seq.ToList(),
            string csv => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => throw new InvalidOperationException(
                "Device tokens are required: pass IEnumerable<string> (or a comma-separated string) " +
                $"as the body or in the '{FcmHeaders.Tokens}' header."),
        };
    }

    /// <summary>
    /// Payload for a multicast message. Unlike <see cref="BuildMessage"/> there are no
    /// body-fallbacks: the exchange body carries the TOKENS, not the notification text.
    /// </summary>
    private void FillPayload(MulticastMessage message, IExchange exchange)
    {
        if (!_options.DataOnly)
        {
            var title = exchange.In.GetHeader<string>(FcmHeaders.Title)
                        ?? _options.Title?.Resolve(exchange);
            var body = exchange.In.GetHeader<string>(FcmHeaders.Body)
                       ?? _options.Body?.Resolve(exchange);

            if (title is not null || body is not null)
            {
                message.Notification = new FcmNotification
                {
                    Title = title,
                    Body = body,
                    ImageUrl = exchange.In.GetHeader<string>(FcmHeaders.ImageUrl) ?? _options.ImageUrl
                };
            }
        }

        var data = new Dictionary<string, string>();
        foreach (var (key, value) in exchange.In.Headers)
        {
            if (key.StartsWith(FcmHeaders.DataPrefix, StringComparison.Ordinal) && value is not null)
                data[key[FcmHeaders.DataPrefix.Length..]] = value.ToString()!;
        }
        if (data.Count > 0)
            message.Data = data;

        message.Android = BuildAndroidConfig();
        message.Apns = BuildApnsConfig();
        message.Webpush = BuildWebpushConfig();
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
                    ImageUrl = exchange.In.GetHeader<string>(FcmHeaders.ImageUrl) ?? _options.ImageUrl
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
        // 1. ConnectionFactory from registry — a set-but-unknown name fails loud (Ф11 Ж-1).
        if (!string.IsNullOrEmpty(_options.ConnectionFactory))
            return _endpoint.FcmComponent.Context
                .GetRequiredFromRegistry<IFirebaseCredentialProvider>(_options.ConnectionFactory);

        // 2. Fall back to component-level provider
        return _endpoint.FcmComponent.CredentialProvider
               ?? throw new InvalidOperationException(
                   "No IFirebaseCredentialProvider available. Register via AddRedbRouteFirebase() or set ConnectionFactory.");
    }
}

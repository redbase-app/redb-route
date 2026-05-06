using redb.Route.Core;

namespace redb.Route.Firebase;

/// <summary>
/// Endpoint options for the FCM (Firebase Cloud Messaging) component.
/// FCM is producer-only — push notifications are sent to devices, topics, or conditions.
/// </summary>
public sealed class FcmEndpointOptions : EndpointOptions
{
    // ── Auth ──

    /// <summary>Path to the Firebase service-account JSON file.</summary>
    public string? CredentialPath { get; set; }

    /// <summary>Firebase project ID.</summary>
    public string? ProjectId { get; set; }

    /// <summary>Named <see cref="IFirebaseCredentialProvider"/> reference from the registry.</summary>
    public string? ConnectionFactory { get; set; }

    // ── Target ──

    /// <summary>Message target type: Token, Topic, or Condition.</summary>
    public FcmMessageType MessageType { get; set; } = FcmMessageType.Token;

    /// <summary>Device registration token. Supports <c>${...}</c> expressions.</summary>
    public DynamicValue<string>? Token { get; set; }

    /// <summary>Topic name for topic-based targeting. Supports <c>${...}</c> expressions.</summary>
    public DynamicValue<string>? Topic { get; set; }

    /// <summary>Topic condition expression. Supports <c>${...}</c> expressions.</summary>
    public DynamicValue<string>? Condition { get; set; }

    // ── Notification ──

    /// <summary>Notification title. Supports <c>${...}</c> expressions.</summary>
    public DynamicValue<string>? Title { get; set; }

    /// <summary>Notification body text. Overrides exchange.In.Body. Supports <c>${...}</c> expressions.</summary>
    public DynamicValue<string>? Body { get; set; }

    /// <summary>Notification image URL.</summary>
    public string? ImageUrl { get; set; }

    // ── Android ──

    /// <summary>Android notification priority: "normal" or "high".</summary>
    public string? AndroidPriority { get; set; }

    /// <summary>Time-to-live for Android push notifications (seconds).</summary>
    public int? AndroidTtlSeconds { get; set; }

    /// <summary>Android notification channel ID.</summary>
    public string? AndroidChannelId { get; set; }

    // ── APNS (iOS) ──

    /// <summary>APNS priority: "5" (normal) or "10" (immediate).</summary>
    public string? ApnsPriority { get; set; }

    /// <summary>APNS collapse identifier for notification grouping.</summary>
    public string? ApnsCollapseId { get; set; }

    /// <summary>Enable APNS content-available for background updates.</summary>
    public bool? ApnsContentAvailable { get; set; }

    /// <summary>Enable APNS mutable-content for Notification Service Extension.</summary>
    public bool? ApnsMutableContent { get; set; }

    // ── Web (VAPID) ──

    /// <summary>Click action URL for web push notifications.</summary>
    public string? WebPushLink { get; set; }

    // ── Data ──

    /// <summary>Send as data-only message (no visible notification).</summary>
    public bool DataOnly { get; set; }

    /// <summary>Validate message without actually sending.</summary>
    public bool DryRun { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(CredentialPath)
            && string.IsNullOrWhiteSpace(ConnectionFactory)
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")))
            throw new ArgumentOutOfRangeException(nameof(CredentialPath),
                "CredentialPath, ConnectionFactory, or GOOGLE_APPLICATION_CREDENTIALS is required");

        if (MessageType == FcmMessageType.Token && Token is null)
            throw new ArgumentOutOfRangeException(nameof(Token),
                "Token is required for Token message type");
        if (MessageType == FcmMessageType.Topic && Topic is null)
            throw new ArgumentOutOfRangeException(nameof(Topic),
                "Topic is required for Topic message type");
        if (MessageType == FcmMessageType.Condition && Condition is null)
            throw new ArgumentOutOfRangeException(nameof(Condition),
                "Condition is required for Condition message type");

        if (AndroidTtlSeconds is not null && AndroidTtlSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(AndroidTtlSeconds),
                "AndroidTtlSeconds must be >= 0");
    }
}

using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.Firebase;

/// <summary>
/// Fluent API entry point for FCM push notification endpoints.
/// <example>
/// <code>
/// // Send to a specific device:
/// .To(Fcm.Token("${header.deviceToken}")
///         .CredentialPath("/secrets/firebase-sa.json")
///         .Title("Order update").Build())
///
/// // Topic broadcast:
/// .To(Fcm.Topic("news").CredentialPath("/secrets/firebase-sa.json").Build())
///
/// // Condition targeting:
/// .To(Fcm.Condition("'sports' in topics &amp;&amp; 'news' in topics").Build())
/// </code>
/// </example>
/// </summary>
public static class Fcm
{
    /// <summary>Creates a Token-targeted FCM producer endpoint.</summary>
    public static FcmBuilder Token(string token) => new(FcmMessageType.Token, "token", token);

    /// <summary>Creates a Token-targeted FCM producer endpoint with expression.</summary>
    public static FcmBuilder Token(IExpression token) => new(FcmMessageType.Token, "token", token.ToTemplateString());

    /// <summary>Creates a Topic-targeted FCM producer endpoint.</summary>
    public static FcmBuilder Topic(string topic) => new(FcmMessageType.Topic, "topic", topic);

    /// <summary>Creates a Topic-targeted FCM producer endpoint with expression.</summary>
    public static FcmBuilder Topic(IExpression topic) => new(FcmMessageType.Topic, "topic", topic.ToTemplateString());

    /// <summary>Creates a Condition-targeted FCM producer endpoint.</summary>
    public static FcmBuilder Condition(string condition) => new(FcmMessageType.Condition, "condition", condition);

    /// <summary>Creates a Condition-targeted FCM producer endpoint with expression.</summary>
    public static FcmBuilder Condition(IExpression condition) => new(FcmMessageType.Condition, "condition", condition.ToTemplateString());
}

/// <summary>
/// Fluent builder for FCM endpoint URIs.
/// </summary>
public sealed class FcmBuilder
{
    private readonly FcmMessageType _type;
    private readonly Dictionary<string, string> _params = new();

    internal FcmBuilder(FcmMessageType type, string targetKey, string targetValue)
    {
        _type = type;
        _params["messageType"] = type.ToString();
        _params[targetKey] = targetValue;
    }

    /// <summary>Notification title.</summary>
    public FcmBuilder Title(string v) => Set("title", v);
    /// <summary>Notification title from expression.</summary>
    public FcmBuilder Title(IExpression v) => Set("title", v.ToTemplateString());

    /// <summary>Notification body text.</summary>
    public FcmBuilder Body(string v) => Set("body", v);
    /// <summary>Notification body from expression.</summary>
    public FcmBuilder Body(IExpression v) => Set("body", v.ToTemplateString());

    /// <summary>Notification image URL.</summary>
    public FcmBuilder ImageUrl(string v) => Set("imageUrl", v);

    /// <summary>Path to service-account JSON file.</summary>
    public FcmBuilder CredentialPath(string v) => Set("credentialPath", v);

    /// <summary>Firebase project ID.</summary>
    public FcmBuilder ProjectId(string v) => Set("projectId", v);

    /// <summary>Named connection factory reference.</summary>
    public FcmBuilder ConnectionFactory(string v) => Set("connectionFactory", v);

    /// <summary>Send as data-only message (no visible notification).</summary>
    public FcmBuilder DataOnly(bool v = true) => Set("dataOnly", v);

    /// <summary>Validate message without sending.</summary>
    public FcmBuilder DryRun(bool v = true) => Set("dryRun", v);

    /// <summary>Android priority: "normal" or "high".</summary>
    public FcmBuilder AndroidPriority(string p) => Set("androidPriority", p);

    /// <summary>Android TTL in seconds.</summary>
    public FcmBuilder AndroidTtlSeconds(int s) => Set("androidTtlSeconds", s);

    /// <summary>Android notification channel ID.</summary>
    public FcmBuilder AndroidChannelId(string id) => Set("androidChannelId", id);

    /// <summary>APNS priority: "5" or "10".</summary>
    public FcmBuilder ApnsPriority(string p) => Set("apnsPriority", p);

    /// <summary>APNS collapse identifier.</summary>
    public FcmBuilder ApnsCollapseId(string id) => Set("apnsCollapseId", id);

    /// <summary>Web push click action URL.</summary>
    public FcmBuilder WebPushLink(string url) => Set("webPushLink", url);

    /// <summary>Builds the FCM URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder("fcm://send?");
        var first = true;
        foreach (var (key, value) in _params)
        {
            if (!first) sb.Append('&');
            sb.Append(key).Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(FcmBuilder b) => b.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();

    private FcmBuilder Set(string k, object v) { _params[k] = v.ToString()!; return this; }
}

namespace redb.Route.Firebase;

/// <summary>
/// Header constants for the FCM connector. Prefix: <c>redbFcm.</c>
/// </summary>
public static class FcmHeaders
{
    /// <summary>Common prefix for all FCM headers.</summary>
    public const string Prefix = "redbFcm.";

    // ── Set by producer after send ──

    /// <summary>FCM message ID returned by the server (projects/*/messages/*).</summary>
    public const string MessageId = "redbFcm.MessageId";

    /// <summary>Success count for multicast sends (future).</summary>
    public const string SuccessCount = "redbFcm.SuccessCount";

    /// <summary>Failure count for multicast sends (future).</summary>
    public const string FailureCount = "redbFcm.FailureCount";

    // ── Read from exchange for targeting ──

    /// <summary>Device registration token override (from exchange header).</summary>
    public const string Token = "redbFcm.Token";

    /// <summary>Topic name override (from exchange header).</summary>
    public const string Topic = "redbFcm.Topic";

    /// <summary>Topic condition expression override (from exchange header).</summary>
    public const string Condition = "redbFcm.Condition";

    // ── Notification payload from headers ──

    /// <summary>Notification title override.</summary>
    public const string Title = "redbFcm.Title";

    /// <summary>Notification body override.</summary>
    public const string Body = "redbFcm.Body";

    /// <summary>Notification image URL.</summary>
    public const string ImageUrl = "redbFcm.ImageUrl";

    // ── Data payload ──

    /// <summary>
    /// Prefix for data entries. Headers starting with <c>redbFcm.Data.</c> are sent
    /// as key-value pairs in the FCM data payload.
    /// Example: <c>redbFcm.Data.orderId</c> → data key <c>orderId</c>.
    /// </summary>
    public const string DataPrefix = "redbFcm.Data.";
}

namespace redb.Route.Firebase;

/// <summary>
/// FCM message target type. Determines how the push notification is addressed.
/// </summary>
public enum FcmMessageType
{
    /// <summary>Send to a specific device registration token.</summary>
    Token,

    /// <summary>Send to all subscribers of a topic.</summary>
    Topic,

    /// <summary>Send to devices matching a topic condition expression.</summary>
    Condition
}

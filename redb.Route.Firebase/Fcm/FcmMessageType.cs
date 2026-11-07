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

/// <summary>
/// FCM producer operation. <see cref="Send"/> is the classic single-message send;
/// the rest are Д5/Д6 additions (multicast and topic management).
/// </summary>
public enum FcmOperationType
{
    /// <summary>Send one message to a token/topic/condition (default).</summary>
    Send,

    /// <summary>Send the same message to many device tokens (body or Tokens header).</summary>
    Multicast,

    /// <summary>Subscribe device tokens (body or Tokens header) to the topic.</summary>
    SubscribeToTopic,

    /// <summary>Unsubscribe device tokens (body or Tokens header) from the topic.</summary>
    UnsubscribeFromTopic
}

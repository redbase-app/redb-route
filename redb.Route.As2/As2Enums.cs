namespace redb.Route.As2;

/// <summary>
/// How the receiver returns the MDN (Message Disposition Notification) receipt.
/// See <c>docs/as2/01-AS2-PROTOCOL.md §4</c>.
/// </summary>
public enum As2MdnMode
{
    /// <summary>No MDN requested.</summary>
    None,

    /// <summary>MDN returned synchronously in the HTTP response to the same POST (InOut).</summary>
    Sync,

    /// <summary>Receiver answers 200 immediately and POSTs the MDN later to <c>Receipt-Delivery-Option</c>.</summary>
    Async,
}

/// <summary>What an AS2 receive endpoint accepts.</summary>
public enum As2ReceiveMode
{
    /// <summary>Inbound business AS2 messages (default).</summary>
    Message,

    /// <summary>Asynchronous MDN receipts posted back by partners.</summary>
    Mdn,
}

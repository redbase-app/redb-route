namespace redb.Route.Sqs;

/// <summary>
/// Exchange header keys for the SQS transport. All carry the <see cref="Prefix"/> so the producer can
/// tell framework/broker headers apart from user headers it must forward as message attributes.
/// </summary>
public static class SqsHeaders
{
    /// <summary>Common prefix for all SQS headers.</summary>
    public const string Prefix = "redbSqs.";

    /// <summary>Queue name (or URL) the message was received from / is sent to.</summary>
    public const string Queue = Prefix + "queue";

    /// <summary>SQS message id.</summary>
    public const string MessageId = Prefix + "messageId";

    /// <summary>Receipt handle — required to delete or change visibility of a received message.</summary>
    public const string ReceiptHandle = Prefix + "receiptHandle";

    /// <summary>MD5 of the message body (integrity check).</summary>
    public const string Md5OfBody = Prefix + "md5OfBody";

    /// <summary>FIFO message group id.</summary>
    public const string MessageGroupId = Prefix + "messageGroupId";

    /// <summary>FIFO deduplication id.</summary>
    public const string MessageDeduplicationId = Prefix + "messageDeduplicationId";

    /// <summary>FIFO sequence number assigned by SQS.</summary>
    public const string SequenceNumber = Prefix + "sequenceNumber";

    /// <summary>Approximate number of times the message has been received.</summary>
    public const string ApproximateReceiveCount = Prefix + "approximateReceiveCount";

    /// <summary>Epoch-millis the message was sent.</summary>
    public const string SentTimestamp = Prefix + "sentTimestamp";

    /// <summary>Receive timestamp (epoch millis) set by the consumer.</summary>
    public const string Timestamp = Prefix + "timestamp";

    /// <summary>Prefix under which incoming message attributes are exposed as headers.</summary>
    public const string MessageAttributePrefix = Prefix + "attr.";

    /// <summary>True if <paramref name="header"/> is an SQS framework/broker header (should not be re-sent as a user attribute).</summary>
    public static bool IsSqsHeader(string header) =>
        header.StartsWith(Prefix, StringComparison.Ordinal);
}

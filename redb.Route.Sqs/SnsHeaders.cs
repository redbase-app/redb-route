namespace redb.Route.Sqs;

/// <summary>
/// Exchange header keys for the SNS transport. All carry the <see cref="Prefix"/> so the producer can
/// forward only genuine user headers as message attributes.
/// </summary>
public static class SnsHeaders
{
    /// <summary>Common prefix for all SNS headers.</summary>
    public const string Prefix = "redbSns.";

    /// <summary>Topic ARN the message was published to.</summary>
    public const string TopicArn = Prefix + "topicArn";

    /// <summary>SNS message id assigned on publish.</summary>
    public const string MessageId = Prefix + "messageId";

    /// <summary>Message subject.</summary>
    public const string Subject = Prefix + "subject";

    /// <summary>FIFO message group id.</summary>
    public const string MessageGroupId = Prefix + "messageGroupId";

    /// <summary>FIFO deduplication id.</summary>
    public const string MessageDeduplicationId = Prefix + "messageDeduplicationId";

    /// <summary>FIFO sequence number returned by SNS.</summary>
    public const string SequenceNumber = Prefix + "sequenceNumber";

    /// <summary>True if <paramref name="header"/> is an SNS framework header (not a user attribute).</summary>
    public static bool IsSnsHeader(string header) =>
        header.StartsWith(Prefix, StringComparison.Ordinal);
}

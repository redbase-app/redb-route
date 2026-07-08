using redb.Route.Core;

namespace redb.Route.Sqs;

/// <summary>
/// Options for an SNS endpoint (<c>sns://topic-name?...</c>). SNS is publish-only (there is no pull
/// consumer — delivery is push to SQS/HTTP/email/SMS), so these options drive the publisher plus the
/// optional SNS→SQS auto-subscription convenience.
/// </summary>
public sealed class SnsEndpointOptions : AwsEndpointOptions
{
    /// <summary>Explicit topic ARN. When empty it is resolved from the topic name (path) at start.</summary>
    public string TopicArn { get; set; } = "";

    /// <summary>Create the topic if it does not exist (FIFO when the name ends in <c>.fifo</c>). Default false.</summary>
    public bool AutoCreateTopic { get; set; }

    /// <summary>Optional message subject (used by email delivery). Supports <c>${...}</c> expressions.</summary>
    public string Subject { get; set; } = "";

    /// <summary>Set to <c>json</c> to send a protocol-specific message structure (per-transport payloads).</summary>
    public string MessageStructure { get; set; } = "";

    /// <summary>FIFO topic message group id. Supports <c>${...}</c> expressions.</summary>
    public DynamicValue<string>? MessageGroupId { get; set; }

    /// <summary>FIFO deduplication id. Supports <c>${...}</c> expressions. Omit for content-based dedup.</summary>
    public DynamicValue<string>? MessageDeduplicationId { get; set; }

    /// <summary>At start, subscribe the SQS queue named by <see cref="SubscribeQueueArn"/> to this topic. Default false.</summary>
    public bool SubscribeSnsToSqs { get; set; }

    /// <summary>ARN of the SQS queue to subscribe when <see cref="SubscribeSnsToSqs"/> is true.</summary>
    public string SubscribeQueueArn { get; set; } = "";

    /// <inheritdoc />
    public override void Validate()
    {
        ValidateAws();

        if (SubscribeSnsToSqs && string.IsNullOrEmpty(SubscribeQueueArn))
            throw new ArgumentException("subscribeSnsToSqs=true requires subscribeQueueArn (the SQS queue ARN).");
    }
}

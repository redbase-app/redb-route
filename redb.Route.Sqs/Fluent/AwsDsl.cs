namespace redb.Route.Sqs.Fluent;

/// <summary>
/// Fluent URI builder entry point for Amazon SQS queues. Produces a <c>sqs://queue-name?...</c> URI
/// that drops straight into <c>.From(...)</c> / <c>.To(...)</c>.
/// <code>
/// From(Sqs.Queue("orders").WaitTimeSeconds(20).ConcurrentConsumers(4))
///     .Process(...);
/// To(Sqs.Queue("orders").Region("eu-west-1"));
/// </code>
/// </summary>
public static class Sqs
{
    /// <summary>Starts an SQS endpoint builder for the given queue name (use the <c>.fifo</c> suffix for FIFO queues).</summary>
    public static SqsBuilder Queue(string name) => new(name);
}

/// <summary>Fluent builder for an <c>sqs://</c> endpoint URI.</summary>
public sealed class SqsBuilder
{
    private readonly string _name;
    private readonly Dictionary<string, string> _params = new(StringComparer.Ordinal);

    internal SqsBuilder(string name) => _name = name;

    private SqsBuilder Set(string key, string value) { _params[key] = value; return this; }

    // ── Connection / credentials ──
    /// <summary>AWS region (e.g. <c>us-east-1</c>).</summary>
    public SqsBuilder Region(string region) => Set("region", region);
    /// <summary>Explicit service URL (LocalStack / ElasticMQ).</summary>
    public SqsBuilder ServiceUrl(string url) => Set("serviceUrl", url);
    /// <summary>Static access + secret keys.</summary>
    public SqsBuilder Credentials(string accessKey, string secretKey) => Set("accessKey", accessKey).Set("secretKey", secretKey);
    /// <summary>Session token for temporary (STS) credentials.</summary>
    public SqsBuilder SessionToken(string token) => Set("sessionToken", token);
    /// <summary>Named AWS profile.</summary>
    public SqsBuilder Profile(string profileName) => Set("profileName", profileName);
    /// <summary>Use the default AWS credential provider chain.</summary>
    public SqsBuilder UseDefaultCredentials(bool value = true) => Set("useDefaultCredentialsProvider", value ? "true" : "false");
    /// <summary>Reference a pre-registered connection factory by name.</summary>
    public SqsBuilder ConnectionFactory(string name) => Set("connectionFactory", name);
    /// <summary>Create the queue if it does not exist.</summary>
    public SqsBuilder AutoCreateQueue(bool value = true) => Set("autoCreateQueue", value ? "true" : "false");

    // ── Consumer ──
    /// <summary>Long-poll wait time in seconds (0–20).</summary>
    public SqsBuilder WaitTimeSeconds(int seconds) => Set("waitTimeSeconds", seconds.ToString());
    /// <summary>Messages fetched per receive (1–10).</summary>
    public SqsBuilder MaxNumberOfMessages(int count) => Set("maxNumberOfMessages", count.ToString());
    /// <summary>Visibility timeout in seconds.</summary>
    public SqsBuilder VisibilityTimeout(int seconds) => Set("visibilityTimeout", seconds.ToString());
    /// <summary>Number of concurrent receive loops.</summary>
    public SqsBuilder ConcurrentConsumers(int count) => Set("concurrentConsumers", count.ToString());
    /// <summary>Keep extending visibility while a message is processing (requires visibilityTimeout &gt; 0).</summary>
    public SqsBuilder ExtendMessageVisibility(bool value = true) => Set("extendMessageVisibility", value ? "true" : "false");
    /// <summary>Delete a message after successful processing.</summary>
    public SqsBuilder DeleteAfterRead(bool value = true) => Set("deleteAfterRead", value ? "true" : "false");
    /// <summary>Reset visibility to 0 on failure for immediate redelivery.</summary>
    public SqsBuilder ResetVisibilityOnFailure(bool value = true) => Set("resetVisibilityOnFailure", value ? "true" : "false");
    /// <summary>Enrol the ack (delete) into the route transaction.</summary>
    public SqsBuilder Transacted(bool value = true) => Set("transacted", value ? "true" : "false");

    // ── Producer ──
    /// <summary>Delivery delay in seconds (0–900).</summary>
    public SqsBuilder DelaySeconds(int seconds) => Set("delaySeconds", seconds.ToString());
    /// <summary>FIFO message group id (constant or <c>${...}</c> expression).</summary>
    public SqsBuilder MessageGroupId(string value) => Set("messageGroupId", value);
    /// <summary>FIFO deduplication id (constant or <c>${...}</c> expression).</summary>
    public SqsBuilder MessageDeduplicationId(string value) => Set("messageDeduplicationId", value);
    /// <summary>Send an <c>IEnumerable</c> body as one <c>SendMessageBatch</c>.</summary>
    public SqsBuilder EnableBatch(bool value = true) => Set("enableBatch", value ? "true" : "false");

    /// <summary>Sets an arbitrary URI parameter not covered by a typed method.</summary>
    public SqsBuilder Param(string key, string value) => Set(key, value);

    /// <summary>Builds the <c>sqs://</c> URI.</summary>
    public string Build()
    {
        var qs = string.Join("&", _params.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return qs.Length > 0 ? $"sqs://{_name}?{qs}" : $"sqs://{_name}";
    }

    /// <inheritdoc />
    public override string ToString() => Build();

    /// <summary>Implicit conversion to the URI string.</summary>
    public static implicit operator string(SqsBuilder builder) => builder.Build();
}

/// <summary>
/// Fluent URI builder entry point for Amazon SNS topics. Produces a <c>sns://topic-name?...</c> URI.
/// SNS is publish-only.
/// <code>
/// To(Sns.Topic("events").Region("us-east-1"));
/// </code>
/// </summary>
public static class Sns
{
    /// <summary>Starts an SNS endpoint builder for the given topic name (use the <c>.fifo</c> suffix for FIFO topics).</summary>
    public static SnsBuilder Topic(string name) => new(name);
}

/// <summary>Fluent builder for an <c>sns://</c> endpoint URI.</summary>
public sealed class SnsBuilder
{
    private readonly string _name;
    private readonly Dictionary<string, string> _params = new(StringComparer.Ordinal);

    internal SnsBuilder(string name) => _name = name;

    private SnsBuilder Set(string key, string value) { _params[key] = value; return this; }

    /// <summary>AWS region (e.g. <c>us-east-1</c>).</summary>
    public SnsBuilder Region(string region) => Set("region", region);
    /// <summary>Explicit service URL (LocalStack).</summary>
    public SnsBuilder ServiceUrl(string url) => Set("serviceUrl", url);
    /// <summary>Static access + secret keys.</summary>
    public SnsBuilder Credentials(string accessKey, string secretKey) => Set("accessKey", accessKey).Set("secretKey", secretKey);
    /// <summary>Named AWS profile.</summary>
    public SnsBuilder Profile(string profileName) => Set("profileName", profileName);
    /// <summary>Use the default AWS credential provider chain.</summary>
    public SnsBuilder UseDefaultCredentials(bool value = true) => Set("useDefaultCredentialsProvider", value ? "true" : "false");
    /// <summary>Reference a pre-registered connection factory by name.</summary>
    public SnsBuilder ConnectionFactory(string name) => Set("connectionFactory", name);
    /// <summary>Explicit topic ARN (skips name resolution).</summary>
    public SnsBuilder TopicArn(string arn) => Set("topicArn", arn);
    /// <summary>Create the topic if it does not exist.</summary>
    public SnsBuilder AutoCreateTopic(bool value = true) => Set("autoCreateTopic", value ? "true" : "false");
    /// <summary>Message subject (email delivery; constant or <c>${...}</c>).</summary>
    public SnsBuilder Subject(string subject) => Set("subject", subject);
    /// <summary>Message structure (<c>json</c> for per-protocol payloads).</summary>
    public SnsBuilder MessageStructure(string structure) => Set("messageStructure", structure);
    /// <summary>FIFO message group id.</summary>
    public SnsBuilder MessageGroupId(string value) => Set("messageGroupId", value);
    /// <summary>FIFO deduplication id.</summary>
    public SnsBuilder MessageDeduplicationId(string value) => Set("messageDeduplicationId", value);
    /// <summary>Subscribe the given SQS queue ARN to this topic at start.</summary>
    public SnsBuilder SubscribeSnsToSqs(string queueArn) => Set("subscribeSnsToSqs", "true").Set("subscribeQueueArn", queueArn);

    /// <summary>Sets an arbitrary URI parameter not covered by a typed method.</summary>
    public SnsBuilder Param(string key, string value) => Set(key, value);

    /// <summary>Builds the <c>sns://</c> URI.</summary>
    public string Build()
    {
        var qs = string.Join("&", _params.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return qs.Length > 0 ? $"sns://{_name}?{qs}" : $"sns://{_name}";
    }

    /// <inheritdoc />
    public override string ToString() => Build();

    /// <summary>Implicit conversion to the URI string.</summary>
    public static implicit operator string(SnsBuilder builder) => builder.Build();
}

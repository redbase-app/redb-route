namespace redb.Route.AzureServiceBus.Fluent;

/// <summary>
/// Fluent DSL entry point for Azure Service Bus URIs.
/// <example>
/// <code>
/// .From(Asb.Queue("orders").ConnectionString("Endpoint=sb://...").Build())
/// .To(Asb.Topic("events", "my-sub").ConnectionString("...").Build())
/// </code>
/// </example>
/// </summary>
public static class Asb
{
    /// <summary>Creates a builder targeting a queue.</summary>
    public static AzureServiceBusBuilder Queue(string queueName)
        => new(queueName, isTopic: false);

    /// <summary>Creates a builder targeting a topic with a subscription.</summary>
    public static AzureServiceBusBuilder Topic(string topicName, string subscriptionName)
        => new AzureServiceBusBuilder(topicName, isTopic: true)
            .SubscriptionName(subscriptionName);
}

/// <summary>
/// Fluent builder for constructing <c>asb://</c> endpoint URIs.
/// </summary>
public sealed class AzureServiceBusBuilder
{
    private readonly string _entity;
    private readonly bool _isTopic;
    private readonly Dictionary<string, string> _params = new(StringComparer.OrdinalIgnoreCase);

    internal AzureServiceBusBuilder(string entity, bool isTopic)
    {
        _entity = entity;
        _isTopic = isTopic;
    }

    // ── Connection ──

    /// <summary>Sets the connection string.</summary>
    public AzureServiceBusBuilder ConnectionString(string cs) => Set("connectionString", cs);

    /// <summary>Sets the named connection factory from registry.</summary>
    public AzureServiceBusBuilder ConnectionFactory(string name) => Set("connectionFactory", name);

    // ── Entity ──

    /// <summary>Sets the topic subscription name.</summary>
    public AzureServiceBusBuilder SubscriptionName(string name) => Set("subscriptionName", name);

    /// <summary>Sets the sub-queue: "deadletter" or "transferdeadletter".</summary>
    public AzureServiceBusBuilder SubQueue(string sq) => Set("subQueue", sq);

    // ── Consumer ──

    /// <summary>Sets receive mode: "PeekLock" or "ReceiveAndDelete".</summary>
    public AzureServiceBusBuilder ReceiveMode(string mode) => Set("receiveMode", mode);

    /// <summary>Sets maximum concurrent message handler calls.</summary>
    public AzureServiceBusBuilder MaxConcurrentCalls(int n) => Set("maxConcurrentCalls", n);

    /// <summary>Sets message pre-fetch count.</summary>
    public AzureServiceBusBuilder PrefetchCount(int n) => Set("prefetchCount", n);

    /// <summary>Sets maximum auto lock renewal duration in seconds.</summary>
    public AzureServiceBusBuilder MaxAutoLockRenewalDuration(int sec) => Set("maxAutoLockRenewalDuration", sec);

    /// <summary>Enables transacted consumer (deferred complete/abandon).</summary>
    public AzureServiceBusBuilder Transacted(bool v = true) => Set("transacted", v);

    /// <summary>Enables automatic dead-lettering on processing error.</summary>
    public AzureServiceBusBuilder AutoDeadLetter(bool v = true) => Set("autoDeadLetter", v);

    /// <summary>Sets dead-letter reason string.</summary>
    public AzureServiceBusBuilder DeadLetterReason(string reason) => Set("deadLetterReason", reason);

    // ── Sessions ──

    /// <summary>Enables session-aware consumer.</summary>
    public AzureServiceBusBuilder EnableSessions(bool v = true) => Set("enableSessions", v);

    /// <summary>Sets fixed session ID filter.</summary>
    public AzureServiceBusBuilder SessionId(string id) => Set("sessionId", id);

    /// <summary>Sets maximum concurrent session handlers.</summary>
    public AzureServiceBusBuilder MaxConcurrentSessions(int n) => Set("maxConcurrentSessions", n);

    /// <summary>Sets session idle timeout in seconds.</summary>
    public AzureServiceBusBuilder SessionIdleTimeout(int sec) => Set("sessionIdleTimeout", sec);

    // ── Producer ──

    /// <summary>Sets dynamic message ID expression.</summary>
    public AzureServiceBusBuilder MessageId(string expr) => Set("messageId", expr);

    /// <summary>Sets dynamic session ID for producer.</summary>
    public AzureServiceBusBuilder ProducerSessionId(string expr) => Set("producerSessionId", expr);

    /// <summary>Sets partition key.</summary>
    public AzureServiceBusBuilder PartitionKey(string key) => Set("partitionKey", key);

    /// <summary>Sets delayed delivery in seconds.</summary>
    public AzureServiceBusBuilder ScheduleDelaySeconds(int sec) => Set("scheduleDelaySeconds", sec);

    /// <summary>Sets message TTL as TimeSpan string (e.g. "00:05:00").</summary>
    public AzureServiceBusBuilder TimeToLive(string ttl) => Set("timeToLive", ttl);

    /// <summary>Enables batch send mode.</summary>
    public AzureServiceBusBuilder EnableBatch(bool v = true) => Set("enableBatch", v);

    /// <summary>Sets maximum messages per batch.</summary>
    public AzureServiceBusBuilder BatchMaxMessages(int n) => Set("batchMaxMessages", n);

    /// <summary>Sets maximum batch size in bytes.</summary>
    public AzureServiceBusBuilder BatchMaxSizeBytes(long bytes) => Set("batchMaxSizeBytes", bytes);

    // ── Retry ──

    /// <summary>Sets maximum retry count.</summary>
    public AzureServiceBusBuilder RetryMaxRetries(int n) => Set("retryMaxRetries", n);

    /// <summary>Sets retry mode: "Exponential" or "Fixed".</summary>
    public AzureServiceBusBuilder RetryMode(string mode) => Set("retryMode", mode);

    // ── Build ──

    /// <summary>Builds the <c>asb://</c> endpoint URI string.</summary>
    public string Build()
    {
        var qs = string.Join("&", _params.Select(kv =>
            $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return qs.Length > 0
            ? $"asb://{_entity}?{qs}"
            : $"asb://{_entity}";
    }

    /// <inheritdoc />
    public override string ToString() => Build();

    /// <summary>Implicit conversion to string (calls <see cref="Build"/>).</summary>
    public static implicit operator string(AzureServiceBusBuilder builder) => builder.Build();

    private AzureServiceBusBuilder Set(string key, object value)
    {
        _params[key] = value.ToString()!;
        return this;
    }
}

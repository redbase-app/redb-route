using Azure.Messaging.ServiceBus;
using redb.Route.Core;

namespace redb.Route.AzureServiceBus;

/// <summary>
/// Endpoint options for Azure Service Bus. Bound from URI query parameters.
/// </summary>
public sealed class AzureServiceBusEndpointOptions : EndpointOptions
{
    // ── Connection ──

    /// <summary>Azure Service Bus connection string.</summary>
    [Sensitive]
    public string ConnectionString { get; set; } = "";

    /// <summary>Named connection factory from route context registry.</summary>
    public string? ConnectionFactory { get; set; }

    // ── Entity ──

    /// <summary>Explicit queue name (overrides URI path).</summary>
    public string? QueueName { get; set; }

    /// <summary>Explicit topic name (overrides URI path).</summary>
    public string? TopicName { get; set; }

    /// <summary>Topic subscription name. When set, the entity is treated as a topic.</summary>
    public string? SubscriptionName { get; set; }

    // ── Consumer: Receive mode ──

    /// <summary>Receive mode: PeekLock (default) or ReceiveAndDelete.</summary>
    public string ReceiveMode { get; set; } = "PeekLock";

    /// <summary>Maximum concurrent message handler invocations (default 1).</summary>
    public int MaxConcurrentCalls { get; set; } = 1;

    /// <summary>Number of messages to pre-fetch into local buffer (default 0).</summary>
    public int PrefetchCount { get; set; }

    /// <summary>Maximum auto lock renewal duration in seconds (default 300 = 5 min).</summary>
    public int MaxAutoLockRenewalDuration { get; set; } = 300;

    /// <summary>Sub-queue to consume from: "deadletter" or "transferdeadletter".</summary>
    public string? SubQueue { get; set; }

    // ── Consumer: Sessions ──

    /// <summary>Enable session-aware consumer (default false).</summary>
    public bool EnableSessions { get; set; }

    /// <summary>Fixed session ID filter. Null = accept any session.</summary>
    public string? SessionId { get; set; }

    /// <summary>Maximum concurrent session handlers (default 1).</summary>
    public int MaxConcurrentSessions { get; set; } = 1;

    /// <summary>Session idle timeout in seconds. 0 = SDK default.</summary>
    public int SessionIdleTimeout { get; set; }

    // ── Consumer: Error handling ──

    /// <summary>Automatically dead-letter messages on processing error (default false).</summary>
    public bool AutoDeadLetter { get; set; }

    /// <summary>Dead-letter reason string when AutoDeadLetter is enabled.</summary>
    public string? DeadLetterReason { get; set; }

    // ── Consumer: Transacted ──

    /// <summary>Deferred complete/abandon via ITransactedAction (default false).</summary>
    public bool Transacted { get; set; }

    // ── Producer: Send ──

    /// <summary>Dynamic message ID expression (supports ${...}).</summary>
    public DynamicValue<string>? MessageId { get; set; }

    /// <summary>Dynamic session ID for producer (supports ${...}).</summary>
    public DynamicValue<string>? ProducerSessionId { get; set; }

    /// <summary>Partition key for batching.</summary>
    public string? PartitionKey { get; set; }

    /// <summary>Delayed delivery in seconds (default 0 = immediate).</summary>
    public int ScheduleDelaySeconds { get; set; }

    /// <summary>Message TTL as TimeSpan string (e.g. "00:05:00").</summary>
    public string? TimeToLive { get; set; }

    // ── Producer: Batch ──

    /// <summary>Send body as a batch of messages (default false).</summary>
    public bool EnableBatch { get; set; }

    /// <summary>Maximum messages per batch (default 100).</summary>
    public int BatchMaxMessages { get; set; } = 100;

    /// <summary>Maximum batch size in bytes (default 256 KB).</summary>
    public long BatchMaxSizeBytes { get; set; } = 256 * 1024;

    // ── Client retry settings ──

    /// <summary>Maximum retry count (default 3).</summary>
    public int RetryMaxRetries { get; set; } = 3;

    /// <summary>Initial retry delay in milliseconds (default 800).</summary>
    public int RetryDelayMs { get; set; } = 800;

    /// <summary>Maximum retry delay in milliseconds (default 60000).</summary>
    public int RetryMaxDelayMs { get; set; } = 60_000;

    /// <summary>Retry mode: Exponential (default) or Fixed.</summary>
    public string RetryMode { get; set; } = "Exponential";

    // ── Validation ──

    /// <inheritdoc />
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString) && string.IsNullOrWhiteSpace(ConnectionFactory))
            throw new ArgumentOutOfRangeException(nameof(ConnectionString),
                "ConnectionString or ConnectionFactory is required");

        if (MaxConcurrentCalls < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentCalls), "Must be >= 1");

        if (PrefetchCount < 0)
            throw new ArgumentOutOfRangeException(nameof(PrefetchCount), "Must be >= 0");

        if (MaxAutoLockRenewalDuration < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxAutoLockRenewalDuration), "Must be >= 0");

        if (MaxConcurrentSessions < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentSessions), "Must be >= 1");

        if (!ReceiveMode.Equals("PeekLock", StringComparison.OrdinalIgnoreCase)
            && !ReceiveMode.Equals("ReceiveAndDelete", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentOutOfRangeException(nameof(ReceiveMode), "Must be PeekLock or ReceiveAndDelete");

        if (!string.IsNullOrWhiteSpace(SubQueue)
            && !SubQueue.Equals("deadletter", StringComparison.OrdinalIgnoreCase)
            && !SubQueue.Equals("transferdeadletter", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentOutOfRangeException(nameof(SubQueue), "Must be deadletter or transferdeadletter");

        if (BatchMaxMessages < 1)
            throw new ArgumentOutOfRangeException(nameof(BatchMaxMessages), "Must be >= 1");

        if (BatchMaxSizeBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(BatchMaxSizeBytes), "Must be >= 1");

        if (!RetryMode.Equals("Exponential", StringComparison.OrdinalIgnoreCase)
            && !RetryMode.Equals("Fixed", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentOutOfRangeException(nameof(RetryMode), "Must be Exponential or Fixed");
    }

    // ── Internal helpers ──

    /// <summary>Parsed receive mode enum.</summary>
    internal ServiceBusReceiveMode ParsedReceiveMode
        => ReceiveMode.Equals("ReceiveAndDelete", StringComparison.OrdinalIgnoreCase)
            ? ServiceBusReceiveMode.ReceiveAndDelete
            : ServiceBusReceiveMode.PeekLock;

    /// <summary>Parsed sub-queue enum.</summary>
    internal Azure.Messaging.ServiceBus.SubQueue? ParsedSubQueue
        => SubQueue?.ToLowerInvariant() switch
        {
            "deadletter" => Azure.Messaging.ServiceBus.SubQueue.DeadLetter,
            "transferdeadletter" => Azure.Messaging.ServiceBus.SubQueue.TransferDeadLetter,
            _ => null
        };

    /// <summary>Parsed retry mode enum.</summary>
    internal ServiceBusRetryMode ParsedRetryMode
        => RetryMode.Equals("Fixed", StringComparison.OrdinalIgnoreCase)
            ? ServiceBusRetryMode.Fixed
            : ServiceBusRetryMode.Exponential;
}

using redb.Route.Core;

namespace redb.Route.Sqs;

/// <summary>
/// Options for an SQS endpoint (<c>sqs://queue-name?...</c>). The queue name is taken from the URI
/// path; every value below is bound from the query string. Consumer and producer options coexist —
/// only the relevant subset applies to each direction.
/// </summary>
public sealed class SqsEndpointOptions : AwsEndpointOptions
{
    // ── Addressing ────────────────────────────────────────────────────
    /// <summary>Explicit queue URL. When empty it is resolved from the queue name (path) at start.</summary>
    public string QueueUrl { get; set; } = "";

    /// <summary>Create the queue if it does not exist (uses the queue name; FIFO if it ends in <c>.fifo</c>). Default false.</summary>
    public bool AutoCreateQueue { get; set; }

    // ── Consumer (receive) ────────────────────────────────────────────
    /// <summary>Long-poll wait time in seconds (0–20). Default 20 — fewer empty receives, lower cost.</summary>
    public int WaitTimeSeconds { get; set; } = 20;

    /// <summary>Messages fetched per <c>ReceiveMessage</c> call (1–10). Default 10.</summary>
    public int MaxNumberOfMessages { get; set; } = 10;

    /// <summary>Visibility timeout in seconds for received messages. 0 = use the queue default.</summary>
    public int VisibilityTimeout { get; set; }

    /// <summary>Concurrent receive/processing loops: a number or "auto" (= max(CPU, 2)). Default 1 (serial).</summary>
    // A string so "auto" binds verbatim instead of silently degrading to the int default (В-7).
    public string? ConcurrentConsumers { get; set; }

    /// <summary>Resolved consumer parallelism (see <see cref="ConcurrencyOption"/>).</summary>
    public int ResolvedConcurrentConsumers => ConcurrencyOption.Resolve(ConcurrentConsumers, "concurrentConsumers");

    /// <summary>
    /// Keep extending a message's visibility while it is still being processed, so long handlers never
    /// trigger redelivery. Requires <see cref="VisibilityTimeout"/> &gt; 0. Default false.
    /// </summary>
    public bool ExtendMessageVisibility { get; set; }

    /// <summary>Delete a message after it is processed successfully (acknowledge). Default true.</summary>
    public bool DeleteAfterRead { get; set; } = true;

    /// <summary>
    /// On processing failure, immediately reset visibility to 0 so the message is redelivered at once
    /// (rather than waiting out the visibility timeout). Default false.
    /// </summary>
    public bool ResetVisibilityOnFailure { get; set; }

    /// <summary>Comma-separated system attribute names to request (e.g. <c>All</c>). Default <c>All</c>.</summary>
    public string AttributeNames { get; set; } = "All";

    /// <summary>Comma-separated message-attribute names to request. Default <c>All</c>.</summary>
    public string MessageAttributeNames { get; set; } = "All";

    /// <summary>
    /// Producer: whether the send joins the enclosing <c>.Transacted()</c> block. Unset, it follows the block: deferred
    /// until the database commits inside one, sent at once outside. <c>true</c> requires a block and fails outside one;
    /// <c>false</c> sends at once even inside one. A consumer always deletes the message itself once the route has
    /// finished successfully, so the option does not apply to it.
    /// </summary>
    public bool? Transacted { get; set; }

    /// <summary>Delay in milliseconds after an empty receive before polling again. Default 0.</summary>
    public int Delay { get; set; }

    /// <summary>Initial delay in milliseconds before the first poll. Default 0.</summary>
    public int InitialDelay { get; set; }

    // ── Producer (send) ───────────────────────────────────────────────
    /// <summary>Delivery delay in seconds for sent messages (0–900). Default 0.</summary>
    public int DelaySeconds { get; set; }

    /// <summary>FIFO message group id (required for FIFO queues). Supports <c>${...}</c> expressions.</summary>
    public DynamicValue<string>? MessageGroupId { get; set; }

    /// <summary>FIFO deduplication id. Supports <c>${...}</c> expressions. Omit when the queue has content-based dedup.</summary>
    public DynamicValue<string>? MessageDeduplicationId { get; set; }

    /// <summary>Send an <c>IEnumerable</c> body as a single <c>SendMessageBatch</c> call. Default false.</summary>
    public bool EnableBatch { get; set; }

    /// <summary>Maximum messages per batch (SQS hard limit 10). Default 10.</summary>
    public int BatchMaxMessages { get; set; } = 10;

    /// <inheritdoc />
    public override void Validate()
    {
        ValidateAws();

        if (WaitTimeSeconds is < 0 or > 20)
            throw new ArgumentException($"waitTimeSeconds must be 0–20. Got: {WaitTimeSeconds}");
        if (MaxNumberOfMessages is < 1 or > 10)
            throw new ArgumentException($"maxNumberOfMessages must be 1–10. Got: {MaxNumberOfMessages}");
        _ = ConcurrencyOption.Resolve(ConcurrentConsumers, "concurrentConsumers"); // loud on garbage
        if (VisibilityTimeout < 0)
            throw new ArgumentException($"visibilityTimeout cannot be negative. Got: {VisibilityTimeout}");
        if (ExtendMessageVisibility && VisibilityTimeout <= 0)
            throw new ArgumentException("extendMessageVisibility requires visibilityTimeout > 0.");
        if (DelaySeconds is < 0 or > 900)
            throw new ArgumentException($"delaySeconds must be 0–900. Got: {DelaySeconds}");
        if (BatchMaxMessages is < 1 or > 10)
            throw new ArgumentException($"batchMaxMessages must be 1–10. Got: {BatchMaxMessages}");
    }
}

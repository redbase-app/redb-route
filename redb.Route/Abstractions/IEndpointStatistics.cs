namespace redb.Route.Abstractions;

/// <summary>
/// Health status of an endpoint based on recent error rate and activity.
/// </summary>
public enum EndpointHealthStatus
{
    /// <summary>Everything is operating normally.</summary>
    Healthy,
    /// <summary>There are issues, but endpoint is operational.</summary>
    Warning,
    /// <summary>High error rate detected.</summary>
    Critical,
    /// <summary>Endpoint is not running.</summary>
    Failed
}

/// <summary>
/// Optional statistics interface for endpoints that track message counts.
/// Not part of <see cref="IEndpoint"/> — consumers/services check via <c>endpoint is IEndpointStatistics stats</c>.
/// Counters use <see cref="System.Threading.Interlocked"/> for thread safety.
/// </summary>
public interface IEndpointStatistics
{
    /// <summary>Number of messages received (consumed) by this endpoint.</summary>
    long MessagesIn { get; }

    /// <summary>Number of messages sent (produced) by this endpoint.</summary>
    long MessagesOut { get; }

    /// <summary>Number of errors during processing.</summary>
    long Errors { get; }

    /// <summary>Number of warnings during processing.</summary>
    long Warnings { get; }

    /// <summary>
    /// Bytes that entered the endpoint (estimated from message bodies): what a consumer received,
    /// and what a producer got back in reply.
    /// </summary>
    long BytesIn { get; }

    /// <summary>
    /// Bytes that left the endpoint: what a producer sent, and what a consumer wrote back as a
    /// reply. Counterpart of <see cref="BytesIn"/>, so the two together describe an endpoint's
    /// volume in both directions rather than lumping it into one number.
    /// </summary>
    long BytesOut { get; }

    /// <summary>
    /// Requests shed by an admission limit (e.g. <c>maxConcurrentRequests</c>) BEFORE a pipeline
    /// ran: the transport answered 429/503 and no exchange was created, so these are counted in
    /// neither <see cref="MessagesIn"/> nor <see cref="Errors"/>.
    /// </summary>
    long Rejected { get; }

    /// <summary>
    /// Exchanges abandoned by a cooperative cancellation: the caller's token was cancelled while
    /// the pipeline ran (a closed dashboard mid-poll, an aborted HTTP request). Unlike
    /// <see cref="Rejected"/> these ARE counted in <see cref="MessagesIn"/> — the exchange had
    /// entered the pipeline — but never in <see cref="Errors"/>: the route did nothing wrong, and
    /// counting them there turned healthy routes red under nothing but polling churn. An
    /// <see cref="OperationCanceledException"/> thrown while the caller's token is still live is
    /// an internal failure and stays in <see cref="Errors"/>. A consumer endpoint's completed
    /// count is therefore <c>MessagesIn - Errors - Cancelled</c>.
    /// </summary>
    long Cancelled { get; }

    /// <summary>Timestamp of the last message activity (in or out), or null if no activity yet.</summary>
    DateTime? LastActivity { get; }

    /// <summary>Timestamp when the endpoint started tracking statistics.</summary>
    DateTime StartTime { get; }

    /// <summary>Duration the endpoint has been tracking statistics.</summary>
    TimeSpan Uptime { get; }

    /// <summary>Average processing time over the last 100 messages.</summary>
    TimeSpan AverageProcessingTime { get; }

    /// <summary>Messages per second over a 60-second sliding window.</summary>
    double ThroughputPerSecond { get; }

    /// <summary>Timestamp of the last error, or null if no errors.</summary>
    DateTime? LastErrorTime { get; }

    /// <summary>Message text of the last error, or empty string.</summary>
    string LastErrorMessage { get; }

    /// <summary>Timestamp of the last warning, or null if no warnings.</summary>
    DateTime? LastWarningTime { get; }

    /// <summary>Message text of the last warning, or empty string.</summary>
    string LastWarningMessage { get; }

    /// <summary>Health status computed from error rate and activity.</summary>
    EndpointHealthStatus HealthStatus { get; }

    /// <summary>Human-readable reason for the current health status.</summary>
    string HealthReason { get; }

    /// <summary>Resets all counters to zero.</summary>
    void ResetStatistics();

    /// <summary>Record an incoming message (consumed). Thread-safe.</summary>
    void RecordMessageIn();

    /// <summary>Record an outgoing message (produced). Thread-safe.</summary>
    void RecordMessageOut();

    /// <summary>Record a processing error. Thread-safe.</summary>
    void RecordError();

    /// <summary>Record a processing error with exception details. Thread-safe.</summary>
    void RecordError(Exception exception);

    /// <summary>Record a processing warning. Thread-safe.</summary>
    void RecordWarning();

    /// <summary>Record a processing warning with message. Thread-safe.</summary>
    void RecordWarning(string message);

    /// <summary>Record processing time of a single message. Thread-safe.</summary>
    void RecordProcessingTime(TimeSpan duration);

    /// <summary>Record a request shed by an admission limit before the pipeline ran. Thread-safe.</summary>
    void RecordRejected();

    /// <summary>Record an exchange abandoned by a cooperative cancellation. Thread-safe.</summary>
    void RecordCancelled();

    /// <summary>Record incoming bytes (message body size). Thread-safe.</summary>
    void RecordBytesIn(long bytes);

    /// <summary>Record outgoing bytes (message body size). Thread-safe.</summary>
    void RecordBytesOut(long bytes);
}

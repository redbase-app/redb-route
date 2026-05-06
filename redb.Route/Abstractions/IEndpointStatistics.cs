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

    /// <summary>Total bytes processed (estimated from message bodies).</summary>
    long BytesIn { get; }

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

    /// <summary>Record incoming bytes (message body size). Thread-safe.</summary>
    void RecordBytesIn(long bytes);
}

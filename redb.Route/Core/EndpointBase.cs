using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;

namespace redb.Route.Core;

/// <summary>
/// Base class for endpoints with built-in lightweight statistics tracking.
/// All counters use <see cref="Interlocked"/> for thread safety with zero lock contention.
/// Consumers call <see cref="RecordMessageIn"/>/<see cref="RecordMessageOut"/>/<see cref="RecordError()"/>
/// to update counters; external code reads via <see cref="IEndpointStatistics"/>.
/// </summary>
/// <typeparam name="TOptions">Typed options for this endpoint.</typeparam>
public abstract class EndpointBase<TOptions> : IEndpoint, IEndpointStatistics where TOptions : EndpointOptions
{
    /// <inheritdoc />
    public EndpointUri Uri { get; }

    /// <inheritdoc />
    public IComponent Component { get; }

    /// <summary>Typed options bound from URI parameters.</summary>
    protected TOptions Options { get; }

    /// <summary>Scope factory for per-exchange DI scope creation. Set by RouteContext.</summary>
    public IServiceScopeFactory? ScopeFactory { get; set; }

    // ── Statistics fields (Interlocked-based, zero lock contention) ──

    private long _messagesIn;
    private long _messagesOut;
    private long _errors;
    private long _bytesIn;
    private long _lastActivityTicks;
    private readonly DateTime _startTime = DateTime.UtcNow;

    // ── Error tracking ──
    private long _lastErrorTicks;
    private volatile string _lastErrorMessage = "";

    // ── Warning tracking ──
    private long _warnings;
    private long _lastWarningTicks;
    private volatile string _lastWarningMessage = "";

    // ── Processing time (sliding window of last 100 durations in ticks) ──
    private readonly ConcurrentQueue<long> _processingTimes = new();
    private const int MaxProcessingTimesHistory = 100;

    // ── Throughput (sliding window of message timestamps in ticks, 60s) ──
    private readonly ConcurrentQueue<long> _messageTimestamps = new();
    private const int ThroughputWindowSeconds = 60;

    /// <summary>Creates an endpoint with parsed URI, owning component, and bound options.</summary>
    /// <param name="uri">Parsed endpoint URI.</param>
    /// <param name="component">The component that created this endpoint.</param>
    /// <param name="options">Options already bound via BindFromUri and validated.</param>
    protected EndpointBase(EndpointUri uri, IComponent component, TOptions options)
    {
        Uri = uri ?? throw new ArgumentNullException(nameof(uri));
        Component = component ?? throw new ArgumentNullException(nameof(component));
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public abstract IProducer CreateProducer();

    /// <inheritdoc />
    public abstract IConsumer CreateConsumer(IProcessor processor);

    /// <inheritdoc />
    public virtual Task Start(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task Stop(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public override string ToString() => Uri.NormalizedKey;

    // ── IEndpointStatistics — read properties ──

    /// <inheritdoc />
    public long MessagesIn => Interlocked.Read(ref _messagesIn);

    /// <inheritdoc />
    public long MessagesOut => Interlocked.Read(ref _messagesOut);

    /// <inheritdoc />
    public long Errors => Interlocked.Read(ref _errors);

    /// <inheritdoc />
    public long Warnings => Interlocked.Read(ref _warnings);

    /// <inheritdoc />
    public long BytesIn => Interlocked.Read(ref _bytesIn);

    /// <inheritdoc />
    public DateTime? LastActivity
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastActivityTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <inheritdoc />
    public DateTime StartTime => _startTime;

    /// <inheritdoc />
    public TimeSpan Uptime => DateTime.UtcNow - _startTime;

    /// <inheritdoc />
    public TimeSpan AverageProcessingTime
    {
        get
        {
            // Snapshot the queue — ConcurrentQueue enumerator is safe
            var items = _processingTimes.ToArray();
            if (items.Length == 0) return TimeSpan.Zero;
            var avgTicks = items.Sum() / items.Length;
            return new TimeSpan(avgTicks);
        }
    }

    /// <inheritdoc />
    public double ThroughputPerSecond
    {
        get
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var cutoff = nowTicks - TimeSpan.TicksPerSecond * ThroughputWindowSeconds;

            // Drain stale entries
            while (_messageTimestamps.TryPeek(out var oldest) && oldest < cutoff)
                _messageTimestamps.TryDequeue(out _);

            var count = _messageTimestamps.Count;
            if (count == 0) return 0.0;

            // Time span from oldest remaining to now
            if (!_messageTimestamps.TryPeek(out var oldestRemaining))
                return 0.0;

            var windowSeconds = (double)(nowTicks - oldestRemaining) / TimeSpan.TicksPerSecond;
            return windowSeconds > 0 ? count / windowSeconds : 0.0;
        }
    }

    /// <inheritdoc />
    public DateTime? LastErrorTime
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastErrorTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <inheritdoc />
    public string LastErrorMessage => _lastErrorMessage;

    /// <inheritdoc />
    public DateTime? LastWarningTime
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastWarningTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <inheritdoc />
    public string LastWarningMessage => _lastWarningMessage;

    /// <inheritdoc />
    public EndpointHealthStatus HealthStatus => GetHealthStatus().Status;

    /// <inheritdoc />
    public string HealthReason => GetHealthStatus().Reason;

    private (EndpointHealthStatus Status, string Reason) GetHealthStatus()
    {
        var lastErr = LastErrorTime;
        var msgs = MessagesIn;
        var errs = Errors;

        // Recent errors (last 5 min)
        if (lastErr.HasValue && (DateTime.UtcNow - lastErr.Value).TotalMinutes < 5)
        {
            var rate = msgs > 0 ? errs * 100.0 / msgs : 0;
            if (rate > 10)
                return (EndpointHealthStatus.Critical, $"High error rate: {rate:F1}% ({errs}/{msgs})");

            return (EndpointHealthStatus.Warning, $"Recent errors (last 5 min): {errs} error(s)");
        }

        // No activity for > 10 min (only if there was any activity at all)
        var last = LastActivity;
        if (last.HasValue && (DateTime.UtcNow - last.Value).TotalMinutes > 10)
        {
            var idle = DateTime.UtcNow - last.Value;
            return (EndpointHealthStatus.Warning, $"No activity for {idle.TotalMinutes:F0} min");
        }

        return (EndpointHealthStatus.Healthy, "OK");
    }

    /// <inheritdoc />
    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _messagesIn, 0);
        Interlocked.Exchange(ref _messagesOut, 0);
        Interlocked.Exchange(ref _errors, 0);
        Interlocked.Exchange(ref _bytesIn, 0);
        Interlocked.Exchange(ref _lastActivityTicks, 0);
        Interlocked.Exchange(ref _lastErrorTicks, 0);
        _lastErrorMessage = "";
        Interlocked.Exchange(ref _warnings, 0);
        Interlocked.Exchange(ref _lastWarningTicks, 0);
        _lastWarningMessage = "";
        while (_processingTimes.TryDequeue(out _)) { }
        while (_messageTimestamps.TryDequeue(out _)) { }
    }

    // ── Recording helpers (thread-safe) ──

    /// <summary>Record an incoming message (consumed). Thread-safe.</summary>
    public void RecordMessageIn()
    {
        Interlocked.Increment(ref _messagesIn);
        var nowTicks = DateTime.UtcNow.Ticks;
        Interlocked.Exchange(ref _lastActivityTicks, nowTicks);

        // Throughput sliding window
        _messageTimestamps.Enqueue(nowTicks);
        var cutoff = nowTicks - TimeSpan.TicksPerSecond * ThroughputWindowSeconds;
        while (_messageTimestamps.TryPeek(out var oldest) && oldest < cutoff)
            _messageTimestamps.TryDequeue(out _);
    }

    /// <summary>Record an outgoing message (produced). Thread-safe.</summary>
    public void RecordMessageOut()
    {
        Interlocked.Increment(ref _messagesOut);
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>Record a processing error. Thread-safe.</summary>
    public void RecordError()
    {
        Interlocked.Increment(ref _errors);
        var nowTicks = DateTime.UtcNow.Ticks;
        Interlocked.Exchange(ref _lastActivityTicks, nowTicks);
        Interlocked.Exchange(ref _lastErrorTicks, nowTicks);
    }

    /// <summary>Record a processing error with exception details. Thread-safe.</summary>
    public void RecordError(Exception exception)
    {
        _lastErrorMessage = exception.Message;
        RecordError();
    }

    /// <summary>Record a processing warning. Thread-safe.</summary>
    public void RecordWarning()
    {
        Interlocked.Increment(ref _warnings);
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _lastWarningTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>Record a processing warning with message. Thread-safe.</summary>
    public void RecordWarning(string message)
    {
        _lastWarningMessage = message;
        RecordWarning();
    }

    /// <summary>Record processing time of a single message. Thread-safe.</summary>
    public void RecordProcessingTime(TimeSpan duration)
    {
        _processingTimes.Enqueue(duration.Ticks);
        // Trim beyond history limit — approximate, no lock needed
        while (_processingTimes.Count > MaxProcessingTimesHistory)
            _processingTimes.TryDequeue(out _);
    }

    /// <summary>Record incoming bytes (message body size). Thread-safe.</summary>
    public void RecordBytesIn(long bytes)
    {
        Interlocked.Add(ref _bytesIn, bytes);
    }

    // ── Utility ──

    /// <summary>Estimate size of exchange body in bytes (for BytesIn tracking).</summary>
    internal static int EstimateBodySize(object? body)
    {
        if (body is null) return 0;
        if (body is string str) return Encoding.UTF8.GetByteCount(str);
        if (body is byte[] bytes) return bytes.Length;
        if (body is ReadOnlyMemory<byte> rom) return rom.Length;
        if (body is ArraySegment<byte> seg) return seg.Count;

        try
        {
            var json = JsonSerializer.Serialize(body);
            return Encoding.UTF8.GetByteCount(json);
        }
        catch
        {
            return 0;
        }
    }
}

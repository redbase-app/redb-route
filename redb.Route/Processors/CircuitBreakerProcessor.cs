using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Telemetry;

namespace redb.Route.Processors;

/// <summary>
/// Circuit breaker states following the standard pattern.
/// </summary>
public enum CircuitState
{
    /// <summary>Normal operation — requests pass through.</summary>
    Closed,
    /// <summary>Circuit is tripped — requests go to fallback.</summary>
    Open,
    /// <summary>Probing state — limited requests allowed to test recovery.</summary>
    HalfOpen
}

/// <summary>
/// Implements the Circuit Breaker pattern. When consecutive failures exceed a threshold,
/// the circuit "opens" and routes exchanges to a fallback processor. After a reset timeout,
/// the circuit enters a half-open state allowing limited probing calls.
/// All state transitions happen under a single lock to avoid TOCTOU races.
/// Thread-safe for concurrent pipeline usage.
/// </summary>
public sealed class CircuitBreakerProcessor : IProcessor
{
    private readonly IProcessor _next;
    private readonly IProcessor? _fallback;
    private readonly int _failureThreshold;
    private readonly TimeSpan _resetTimeout;
    private readonly int _halfOpenMaxCalls;
    private readonly ILogger? _logger;

    private readonly object _lock = new();
    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures;
    private int _halfOpenCallCount;
    private int _halfOpenSuccessCount;
    private DateTimeOffset _openedAt;

    /// <summary>Gets the current circuit state.</summary>
    public CircuitState State
    {
        get { lock (_lock) return GetEffectiveStateLocked(); }
    }

    /// <summary>Creates a circuit breaker processor.</summary>
    /// <param name="next">The protected processor.</param>
    /// <param name="failureThreshold">Number of consecutive failures before opening.</param>
    /// <param name="resetTimeout">Time to wait before transitioning from Open to HalfOpen.</param>
    /// <param name="halfOpenMaxCalls">Max probe calls allowed in HalfOpen state.</param>
    /// <param name="fallback">Optional fallback processor when circuit is open.</param>
    /// <param name="logger">Optional logger.</param>
    public CircuitBreakerProcessor(
        IProcessor next,
        int failureThreshold = 5,
        TimeSpan? resetTimeout = null,
        int halfOpenMaxCalls = 1,
        IProcessor? fallback = null,
        ILogger? logger = null)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        if (failureThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        _failureThreshold = failureThreshold;
        _resetTimeout = resetTimeout ?? TimeSpan.FromSeconds(30);
        _halfOpenMaxCalls = halfOpenMaxCalls > 0 ? halfOpenMaxCalls : 1;
        _fallback = fallback;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        // Acquire state atomically — decide whether to let this call through
        CircuitState stateForThisCall;
        lock (_lock)
        {
            stateForThisCall = GetEffectiveStateLocked();

            if (stateForThisCall == CircuitState.Open)
            {
                // Don't release lock with pending work — just flag and handle below
            }
            else if (stateForThisCall == CircuitState.HalfOpen)
            {
                if (_halfOpenCallCount >= _halfOpenMaxCalls)
                {
                    // Probe limit reached — treat as open
                    stateForThisCall = CircuitState.Open;
                }
                else
                {
                    _halfOpenCallCount++;
                }
            }
        }

        if (stateForThisCall == CircuitState.Open)
        {
            ProcessorMetrics.CircuitBreakerRejected.Add(1);
            if (_fallback != null)
            {
                await _fallback.Process(exchange, ct).ConfigureAwait(false);
                return;
            }
            exchange.Exception = new CircuitBreakerOpenException(
                $"Circuit breaker is open. Will retry after {_resetTimeout.TotalSeconds}s.");
            return;
        }

        // stateForThisCall is Closed or HalfOpen — execute protected processor
        try
        {
            await _next.Process(exchange, ct).ConfigureAwait(false);

            if (exchange.Exception != null)
            {
                OnFailure();
            }
            else
            {
                OnSuccess(stateForThisCall);
            }
        }
        catch (Exception ex)
        {
            OnFailure();
            exchange.Exception = ex;
        }
    }

    /// <summary>
    /// Evaluates the effective state. Must be called under <see cref="_lock"/>.
    /// Handles the time-based Open → HalfOpen transition.
    /// </summary>
    private CircuitState GetEffectiveStateLocked()
    {
        if (_state == CircuitState.Open && DateTimeOffset.UtcNow - _openedAt >= _resetTimeout)
        {
            _state = CircuitState.HalfOpen;
            _halfOpenCallCount = 0;
            _halfOpenSuccessCount = 0;
        }
        return _state;
    }

    private void OnSuccess(CircuitState stateAtEntry)
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;

            if (stateAtEntry == CircuitState.HalfOpen)
            {
                _halfOpenSuccessCount++;
                // Close only when ALL probe calls succeeded
                if (_halfOpenSuccessCount >= _halfOpenMaxCalls)
                {
                    _state = CircuitState.Closed;
                }
            }
            else
            {
                // Closed state — just reset failures (already done above)
            }
        }
    }

    private void OnFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;

            if (_state == CircuitState.HalfOpen)
            {
                // Any failure in HalfOpen → Open immediately
                _state = CircuitState.Open;
                _openedAt = DateTimeOffset.UtcNow;
                ProcessorMetrics.CircuitBreakerTripped.Add(1);
                _logger?.LogWarning("Circuit breaker tripped to Open (HalfOpen probe failed).");
            }
            else if (_consecutiveFailures >= _failureThreshold)
            {
                _state = CircuitState.Open;
                _openedAt = DateTimeOffset.UtcNow;
                ProcessorMetrics.CircuitBreakerTripped.Add(1);
                _logger?.LogWarning("Circuit breaker tripped to Open after {Failures} consecutive failures.", _consecutiveFailures);
            }
        }
    }
}

/// <summary>
/// Exception thrown when the circuit breaker is in Open state and no fallback is configured.
/// </summary>
public sealed class CircuitBreakerOpenException : Exception
{
    /// <summary>Creates a new circuit breaker open exception.</summary>
    /// <param name="message">Exception message.</param>
    public CircuitBreakerOpenException(string message) : base(message) { }

    /// <summary>Creates a new circuit breaker open exception with inner exception.</summary>
    /// <param name="message">Exception message.</param>
    /// <param name="inner">Inner exception.</param>
    public CircuitBreakerOpenException(string message, Exception inner) : base(message, inner) { }
}

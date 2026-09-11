using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Throttle per key: each distinct key (customer id, tenant, API key…) gets its own sliding-window gate,
/// and the limit is read from the message, so key <b>and</b> limit can come from the exchange
/// ("gold customers 100 per second, others 10, each under its own key").
/// <para>
/// A key's gate <i>is</i> a <see cref="ThrottleProcessor"/> — the same arrival-order-fair,
/// per-message-limit gate as the plain <c>Throttle</c>, one per key — so both throttles behave
/// identically, including the two overflow modes (wait, or reject with 429 + <c>Retry-After</c>).
/// Gates idle for two periods are evicted, at most once per period, so a high-cardinality key space
/// neither grows without bound nor is scanned on every message.
/// </para>
/// </summary>
public sealed class KeyedThrottleProcessor : IProcessor, IDisposable
{
    private readonly IProcessor _next;
    private readonly Func<IExchange, string> _keyExtractor;
    private readonly Func<IExchange, int> _maxPerPeriod;
    private readonly TimeSpan _period;
    private readonly bool _rejectOnOverflow;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, KeyGate> _gates = new(StringComparer.Ordinal);
    private long _nextEvictionTicks;
    private int _disposed;

    /// <summary>Creates a keyed throttle with a fixed limit per key.</summary>
    /// <param name="next">Next processor in the pipeline.</param>
    /// <param name="keyExtractor">Function extracting the throttle key from the exchange.</param>
    /// <param name="maxPerPeriod">Maximum exchanges per period per key.</param>
    /// <param name="period">Time period for the rate limit (default: 1 second).</param>
    /// <param name="rejectOnOverflow">When <c>true</c>, exchanges that exceed the rate limit are
    /// short-circuited with HTTP 429 + <c>Retry-After</c> instead of waiting.</param>
    /// <param name="logger">Optional logger.</param>
    public KeyedThrottleProcessor(
        IProcessor next,
        Func<IExchange, string> keyExtractor,
        int maxPerPeriod,
        TimeSpan? period = null,
        bool rejectOnOverflow = false,
        ILogger? logger = null)
        : this(next, keyExtractor, _ => maxPerPeriod, period, rejectOnOverflow, logger)
    {
        if (maxPerPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(maxPerPeriod), "Must be > 0.");
    }

    /// <summary>Creates a keyed throttle whose limit is evaluated on every message.</summary>
    /// <param name="next">Next processor in the pipeline.</param>
    /// <param name="keyExtractor">Function extracting the throttle key from the exchange.</param>
    /// <param name="maxPerPeriod">Per-message limit for the exchange's key; must return a positive number.</param>
    /// <param name="period">Time period for the rate limit (default: 1 second).</param>
    /// <param name="rejectOnOverflow">When <c>true</c>, overflow is rejected with 429 instead of waiting.</param>
    /// <param name="logger">Optional logger.</param>
    public KeyedThrottleProcessor(
        IProcessor next,
        Func<IExchange, string> keyExtractor,
        Func<IExchange, int> maxPerPeriod,
        TimeSpan? period = null,
        bool rejectOnOverflow = false,
        ILogger? logger = null)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _keyExtractor = keyExtractor ?? throw new ArgumentNullException(nameof(keyExtractor));
        _maxPerPeriod = maxPerPeriod ?? throw new ArgumentNullException(nameof(maxPerPeriod));
        _period = period ?? TimeSpan.FromSeconds(1);
        if (_period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period), "Must be positive.");
        _rejectOnOverflow = rejectOnOverflow;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var key = _keyExtractor(exchange) ?? string.Empty;
        var gate = AcquireGate(key);
        try
        {
            await gate.Throttle.Process(exchange, ct).ConfigureAwait(false);
        }
        finally
        {
            TryEvictStale();
        }
    }

    /// <summary>
    /// The live gate for <paramref name="key"/>, touched under its own lock. Eviction decides under the
    /// same lock, so a gate a message is about to enter is never retired underneath it; a gate retired
    /// between the lookup and the lock is skipped and the lookup repeated.
    /// </summary>
    private KeyGate AcquireGate(string key)
    {
        while (true)
        {
            var gate = _gates.GetOrAdd(key, _ => new KeyGate(new ThrottleProcessor(_next, _maxPerPeriod, _period, _rejectOnOverflow, _logger)));
            lock (gate.Sync)
            {
                if (gate.Retired) continue;
                gate.Touch();
                return gate;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var gate in _gates.Values)
            gate.Throttle.Dispose();
        _gates.Clear();
    }

    /// <summary>Number of keys currently holding a gate (diagnostics / tests).</summary>
    public int ActiveKeyCount => _gates.Count;

    /// <summary>Evicts gates idle for two periods; runs at most once per period, whoever's message triggers it.</summary>
    private void TryEvictStale()
    {
        var now = Environment.TickCount64;
        var next = Volatile.Read(ref _nextEvictionTicks);
        if (now < next || Interlocked.CompareExchange(ref _nextEvictionTicks, now + (long)_period.TotalMilliseconds, next) != next)
            return;

        var threshold = now - (long)(_period.TotalMilliseconds * 2);
        foreach (var (key, gate) in _gates)
        {
            lock (gate.Sync)
            {
                if (gate.Retired || gate.LastUsedTicks >= threshold || !gate.Throttle.IsIdle)
                    continue;
                gate.Retired = true;
                _gates.TryRemove(new KeyValuePair<string, KeyGate>(key, gate));
                gate.Throttle.Dispose();
            }
        }
    }

    private sealed class KeyGate(ThrottleProcessor throttle)
    {
        private long _lastUsedTicks = Environment.TickCount64;

        public readonly object Sync = new();

        public ThrottleProcessor Throttle { get; } = throttle;

        /// <summary>Set under <see cref="Sync"/> by eviction; a retired gate is disposed and must not be entered.</summary>
        public bool Retired { get; set; }

        public long LastUsedTicks => Volatile.Read(ref _lastUsedTicks);

        public void Touch() => Volatile.Write(ref _lastUsedTicks, Environment.TickCount64);
    }
}

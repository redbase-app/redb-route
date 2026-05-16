using redb.Route.Abstractions;

namespace redb.Route.Processors;

/// <summary>
/// Repeats processing either a fixed number of times or until a predicate returns false.
/// Supports both count-based and predicate-based looping with optional exchange copy per iteration.
/// </summary>
public class LoopProcessor : IProcessor
{
    private readonly IProcessor _body;
    private readonly int? _maxIterations;
    private readonly Func<IExchange, bool>? _whileCondition;
    private readonly Func<IExchange, int>? _countFactory;
    private readonly bool _copy;
    private readonly bool _shareScope;

    /// <summary>Creates a count-based loop processor.</summary>
    /// <param name="body">Processor to execute on each iteration.</param>
    /// <param name="maxIterations">Maximum number of iterations.</param>
    /// <param name="copy">If true, each iteration receives a clone of the original exchange.</param>
    /// <param name="shareScope">If true (default), copy-mode iterations share the parent's DI scope (same DB connection, same TX). If false, each iteration gets its own scope.</param>
    public LoopProcessor(IProcessor body, int maxIterations, bool copy = false, bool shareScope = true)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        if (maxIterations < 0)
            throw new ArgumentOutOfRangeException(nameof(maxIterations), "Must be non-negative.");
        _maxIterations = maxIterations;
        _copy = copy;
        _shareScope = shareScope;
    }

    /// <summary>Creates a predicate-based loop processor.</summary>
    /// <param name="body">Processor to execute on each iteration.</param>
    /// <param name="whileCondition">Predicate evaluated before each iteration; loop continues while true.</param>
    /// <param name="copy">If true, each iteration receives a clone of the original exchange.</param>
    /// <param name="shareScope">If true (default), copy-mode iterations share the parent's DI scope (same DB connection, same TX). If false, each iteration gets its own scope.</param>
    public LoopProcessor(IProcessor body, Func<IExchange, bool> whileCondition, bool copy = false, bool shareScope = true)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _whileCondition = whileCondition ?? throw new ArgumentNullException(nameof(whileCondition));
        _copy = copy;
        _shareScope = shareScope;
    }

    /// <summary>Creates a dynamic count-based loop processor where count is resolved from the exchange at runtime.</summary>
    /// <param name="body">Processor to execute on each iteration.</param>
    /// <param name="countFactory">Function that resolves the loop count from the exchange.</param>
    /// <param name="copy">If true, each iteration receives a clone of the original exchange.</param>
    /// <param name="shareScope">If true (default), copy-mode iterations share the parent's DI scope (same DB connection, same TX). If false, each iteration gets its own scope.</param>
    public LoopProcessor(IProcessor body, Func<IExchange, int> countFactory, bool copy = false, bool shareScope = true)
    {
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _countFactory = countFactory ?? throw new ArgumentNullException(nameof(countFactory));
        _copy = copy;
        _shareScope = shareScope;
    }

    /// <inheritdoc />
    public async Task Process(IExchange exchange, CancellationToken ct = default)
    {
        // Snapshot: shares parent scope by default so iterations participate in parent TX.
        var snapshot = _copy
            ? (_shareScope ? exchange.CloneLinked() : exchange.Clone())
            : null;

        try
        {
            if (_maxIterations.HasValue)
            {
                for (var i = 0; i < _maxIterations.Value; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (exchange.IsStopped) break;
                    await ProcessIteration(exchange, snapshot, ct).ConfigureAwait(false);
                }
            }
            else if (_countFactory is not null)
            {
                var count = _countFactory(exchange);
                for (var i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (exchange.IsStopped) break;
                    await ProcessIteration(exchange, snapshot, ct).ConfigureAwait(false);
                }
            }
            else
            {
                while (_whileCondition!(exchange))
                {
                    ct.ThrowIfCancellationRequested();
                    if (exchange.IsStopped) break;
                    await ProcessIteration(exchange, snapshot, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (snapshot is IAsyncDisposable d)
                await d.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ProcessIteration(IExchange exchange, IExchange? snapshot, CancellationToken ct)
    {
        if (!_copy)
        {
            await _body.Process(exchange, ct).ConfigureAwait(false);
            return;
        }

        var target = _shareScope ? snapshot!.CloneLinked() : snapshot!.Clone();
        try
        {
            await _body.Process(target, ct).ConfigureAwait(false);
            MergeBack(exchange, target);
        }
        finally
        {
            if (target is IAsyncDisposable d)
                await d.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Merges iteration results back into the original exchange (headers, properties, exception state).</summary>
    private static void MergeBack(IExchange original, IExchange iteration)
    {
        original.In.Body = iteration.In.Body;
        foreach (var h in iteration.In.Headers)
            original.In.Headers[h.Key] = h.Value;
        foreach (var p in iteration.Properties)
            original.Properties[p.Key] = p.Value;
        if (iteration.Exception != null)
            original.Exception = iteration.Exception;
    }
}

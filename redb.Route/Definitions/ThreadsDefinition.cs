using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for the <b>Threads EIP</b> (Camel-style concurrency stage).
/// Wraps the child pipeline (<see cref="IProcessorDefinition.Outputs"/>) and runs it on a bounded pool
/// of <c>poolSize</c> persistent workers via <see cref="ThreadsProcessor"/>, so a serial polling
/// consumer can process up to <c>poolSize</c> exchanges concurrently.
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>; close with <see cref="EndThreads"/>.
/// <para>
/// Adaptive by exchange pattern: <b>InOnly</b> is a fire-and-forget hand-off (the exchange is cloned and
/// run on the worker pool — a transaction boundary, like <c>.To("seda://")</c>); <b>InOut</b> runs the
/// body inline on the same exchange under a <c>SemaphoreSlim</c> gate (≤ <c>poolSize</c> concurrent), so
/// the reply — on <c>Out</c> or <c>In</c> — is preserved losslessly and request/reply works across it
/// (InOut is NOT a transaction boundary — the ambient transaction flows into the inline body).
/// Ordering is not preserved when <c>poolSize &gt; 1</c>.
/// </para>
/// </summary>
public class ThreadsDefinition : RouteDefinitionBase<ThreadsDefinition>, IRouteScope
{
    private readonly int _poolSize;
    private int _maxQueueSize; // 0 = default (== poolSize)
    private TimeSpan? _enqueueTimeout; // null = wait indefinitely for a free slot

    internal ThreadsDefinition(int poolSize)
    {
        if (poolSize < 1)
            throw new ArgumentOutOfRangeException(nameof(poolSize), poolSize, "poolSize must be at least 1.");
        _poolSize = poolSize;
    }

    // ── Options ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the bounded hand-off queue capacity (backpressure). 0 = default (== pool size).
    /// A larger queue lets the poll loop run further ahead of the pool before it throttles.
    /// </summary>
    public ThreadsDefinition MaxQueueSize(int value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, "maxQueueSize cannot be negative.");
        _maxQueueSize = value;
        return this;
    }

    /// <summary>
    /// Bounds how long a caller waits for a free worker slot when the pool + queue are saturated.
    /// Default (unset) = wait indefinitely (backpressure). When set and the window elapses, the enqueue
    /// fails with <see cref="TimeoutException"/>; for an InOut/RPC exchange that becomes the reply fault
    /// (surfaced to the caller) instead of an unbounded wait.
    /// </summary>
    public ThreadsDefinition EnqueueTimeout(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(value), value, "enqueueTimeout must be positive.");
        _enqueueTimeout = value;
        return this;
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes this threads scope and returns the parent route definition.</summary>
    public IRouteDefinition EndThreads()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndThreads() called without a parent route."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndThreads();

    // ── IProcessorDefinition ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        IProcessor body = Outputs.Count switch
        {
            0 => new DelegateProcessor(_ => { }),
            1 => Outputs[0].CreateProcessor(context),
            _ => BuildPipeline(context)
        };

        return new ThreadsProcessor(body, _poolSize, _maxQueueSize, context)
        {
            EnqueueTimeout = _enqueueTimeout
        };
    }

    private PipelineProcessor BuildPipeline(IRouteContext context)
    {
        var pipeline = new PipelineProcessor();
        foreach (var output in Outputs)
            pipeline.Add(output.CreateProcessor(context));
        return pipeline;
    }
}

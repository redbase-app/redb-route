using redb.Route.Abstractions;
using redb.Route.Expressions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for the Splitter EIP.
/// Splits an exchange into sub-exchanges via a splitter function,
/// then passes each sub-exchange through the child pipeline (Outputs).
/// Close with <see cref="EndSplit"/>.
/// </summary>
public class SplitDefinition : RouteDefinition, IRouteScope
{
    private readonly Func<IExchange, IEnumerable<object?>>? _splitter;
    private readonly Func<IExchange, IAsyncEnumerable<object?>>? _asyncSplitter;
    private bool _parallel;
    private bool _stopOnException = true;
    private TimeSpan _timeout;
    private int _maxDegreeOfParallelism;
    private Func<IExchange?, IExchange, IExchange>? _aggregationStrategy;

    /// <summary>Captured source <see cref="IExpression"/> when the splitter was built from an expression instance; null otherwise.</summary>
    public IExpression? SourceExpression { get; }

    internal SplitDefinition(Func<IExchange, IEnumerable<object?>> splitter)
    {
        ArgumentNullException.ThrowIfNull(splitter);
        _splitter = splitter;
    }

    internal SplitDefinition(IExpression expression)
        : this(ex =>
        {
            var val = expression.Evaluate<object?>(ex);
            return val switch
            {
                IEnumerable<object?> e => e,
                System.Collections.IEnumerable e => e.Cast<object?>(),
                _ => throw new InvalidOperationException(
                    $"Expression did not evaluate to an enumerable; got {val?.GetType().Name ?? "null"}."),
            };
        })
    {
        SourceExpression = expression;
    }

    internal SplitDefinition(Func<IExchange, IAsyncEnumerable<object?>> asyncSplitter)
    {
        ArgumentNullException.ThrowIfNull(asyncSplitter);
        _asyncSplitter = asyncSplitter;
    }

    // ── Options ────────────────────────────────────────────────────────────────

    /// <summary>Enables parallel processing of split parts.</summary>
    public SplitDefinition Parallel(bool value = true) { _parallel = value; return this; }

    /// <summary>Apache Camel alias for <see cref="Parallel(bool)"/>.</summary>
    public SplitDefinition ParallelProcessing(bool value = true) => Parallel(value);

    /// <summary>Sets stop-on-exception behaviour (default: true).</summary>
    public SplitDefinition StopOnException(bool value = true) { _stopOnException = value; return this; }

    /// <summary>Sets maximum degree of parallelism (0 = cpu count).</summary>
    public SplitDefinition MaxParallelism(int value) { _maxDegreeOfParallelism = value; return this; }

    /// <summary>Sets a timeout for the entire split operation.</summary>
    public SplitDefinition Timeout(TimeSpan timeout) { _timeout = timeout; return this; }

    /// <summary>Sets the pair-wise aggregation strategy.</summary>
    public SplitDefinition AggregationStrategy(Func<IExchange?, IExchange, IExchange> strategy)
    {
        _aggregationStrategy = strategy;
        return this;
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes this split scope and returns the parent route definition.</summary>
    public IRouteDefinition EndSplit()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndSplit() called without a parent route."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndSplit();

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

        if (_asyncSplitter is not null)
            return new StreamingSplitterProcessor(_asyncSplitter, body, _stopOnException);

        return new SplitterProcessor(
            _splitter!,
            body,
            parallelProcessing: _parallel,
            maxDegreeOfParallelism: _maxDegreeOfParallelism,
            aggregationStrategy: _aggregationStrategy,
            stopOnException: _stopOnException,
            timeout: _timeout);
    }

    private PipelineProcessor BuildPipeline(IRouteContext context)
    {
        var pipeline = new PipelineProcessor();
        foreach (var output in Outputs)
            pipeline.Add(output.CreateProcessor(context));
        return pipeline;
    }

    // ── Leaf DSL ───────────────────────────────────────────────────────────────

    /// <summary>Sends the split exchange to an endpoint.</summary>
    public SplitDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Processes each split exchange with a synchronous action.</summary>
    public SplitDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Processes each split exchange with an asynchronous action.</summary>
    public SplitDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Processes each split exchange with a pre-built processor.</summary>
    public SplitDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }

    /// <summary>Sets the body to a static value.</summary>
    public SplitDefinition SetBody(object? value) { AddOutput(new SetBodyStaticDefinition(value)); return this; }

    /// <summary>Sets the body using a factory.</summary>
    public SplitDefinition SetBody(Func<IExchange, object?> factory) { AddOutput(new SetBodyFactoryDefinition(factory)); return this; }

    /// <summary>Transforms the body.</summary>
    public SplitDefinition Transform(Func<IExchange, object?> transform) { AddOutput(new TransformDefinition(transform)); return this; }

    /// <summary>Sets a header to a static value.</summary>
    public SplitDefinition SetHeader(string key, object? value) { AddOutput(new SetHeaderStaticDefinition(key, value)); return this; }

    /// <summary>Stops exchange processing.</summary>
    public SplitDefinition Stop() { AddOutput(new StopDefinition()); return this; }
}

/// <summary>
/// Scope-opener definition for the Multicast EIP.
/// Sends the exchange to all child endpoints (fan-out).
/// Each output is compiled as one target processor for <see cref="MulticastProcessor"/>.
/// Close with <see cref="EndMulticast"/>.
/// </summary>
public class MulticastDefinition : RouteDefinition, IRouteScope
{
    private bool _parallel = true;
    private bool _stopOnException;
    private TimeSpan _timeout;
    private int _maxDegreeOfParallelism;
    private Func<IExchange, IExchange, IExchange>? _aggregationStrategy;

    internal MulticastDefinition() { }

    // ── Options ────────────────────────────────────────────────────────────────

    /// <summary>Sets sequential processing (parallel = false).</summary>
    public MulticastDefinition Sequential() { _parallel = false; return this; }

    /// <summary>Enables parallel processing (default).</summary>
    public MulticastDefinition Parallel(bool value = true) { _parallel = value; return this; }

    /// <summary>Apache Camel alias for <see cref="Parallel(bool)"/>.</summary>
    public MulticastDefinition ParallelProcessing(bool value = true) => Parallel(value);

    /// <summary>Sets stop-on-exception behaviour (default: false).</summary>
    public MulticastDefinition StopOnException(bool value = true) { _stopOnException = value; return this; }

    /// <summary>Sets maximum degree of parallelism.</summary>
    public MulticastDefinition MaxParallelism(int value) { _maxDegreeOfParallelism = value; return this; }

    /// <summary>Sets a timeout for the entire multicast operation.</summary>
    public MulticastDefinition Timeout(TimeSpan timeout) { _timeout = timeout; return this; }

    /// <summary>Sets the pair-wise aggregation strategy.</summary>
    public MulticastDefinition AggregationStrategy(Func<IExchange, IExchange, IExchange> strategy)
    {
        _aggregationStrategy = strategy;
        return this;
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes this multicast scope and returns the parent route definition.</summary>
    public IRouteDefinition EndMulticast()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndMulticast() called without a parent route."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndMulticast();

    // ── IProcessorDefinition ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var multicast = new MulticastProcessor(
            parallelProcessing: _parallel,
            aggregationStrategy: _aggregationStrategy,
            stopOnException: _stopOnException,
            timeout: _timeout,
            maxDegreeOfParallelism: _maxDegreeOfParallelism);

        foreach (var output in Outputs)
            multicast.AddTarget(output.CreateProcessor(context));

        return multicast;
    }

    // ── Leaf/target DSL ────────────────────────────────────────────────────────

    /// <summary>Adds an endpoint target to the multicast fan-out.</summary>
    public MulticastDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Adds a processor as a multicast target.</summary>
    public MulticastDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Adds an async processor as a multicast target.</summary>
    public MulticastDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Adds a pre-built processor as a multicast target.</summary>
    public MulticastDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }
}

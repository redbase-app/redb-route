using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Definitions;



/// <summary>
/// Scope-opener definition for the Loop EIP.
/// Repeats the body pipeline either a fixed number of times, a dynamic number of times
/// resolved from the exchange, or while a predicate holds.
/// Close with <see cref="EndLoop"/>.
/// </summary>
public class LoopDefinition : RouteDefinition, IRouteScope
{
    private enum LoopMode { Count, CountFactory, While }

    private readonly LoopMode _mode;
    private readonly int _count;
    private readonly Func<IExchange, int>? _countFactory;
    private readonly Func<IExchange, bool>? _condition;
    private readonly bool _copy;
    private readonly bool _shareScope;

    /// <summary>Creates a count-based loop definition.</summary>
    internal LoopDefinition(int count, bool copy, bool shareScope)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "Must be non-negative.");
        _mode = LoopMode.Count;
        _count = count;
        _copy = copy;
        _shareScope = shareScope;
    }

    /// <summary>Creates a dynamic-count loop definition.</summary>
    internal LoopDefinition(Func<IExchange, int> countFactory, bool copy, bool shareScope)
    {
        ArgumentNullException.ThrowIfNull(countFactory);
        _mode = LoopMode.CountFactory;
        _countFactory = countFactory;
        _copy = copy;
        _shareScope = shareScope;
    }

    /// <summary>Creates a while-condition loop definition.</summary>
    internal LoopDefinition(Func<IExchange, bool> condition, bool copy, bool shareScope)
    {
        ArgumentNullException.ThrowIfNull(condition);
        _mode = LoopMode.While;
        _condition = condition;
        _copy = copy;
        _shareScope = shareScope;
    }

    /// <summary>Whether each iteration receives an independent clone of the exchange.</summary>
    public bool Copy => _copy;

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes this loop scope and returns the parent route definition.</summary>
    public IRouteDefinition EndLoop()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndLoop() called without a parent route."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndLoop();

    // ── Leaf DSL (loop body) ───────────────────────────────────────────────────

    /// <summary>Sends the exchange to an endpoint (loop body).</summary>
    public LoopDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Processes with a synchronous action (loop body).</summary>
    public LoopDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Processes with an asynchronous action (loop body).</summary>
    public LoopDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Processes with a pre-built processor (loop body).</summary>
    public LoopDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }

    /// <summary>Sets the exchange body to a static value (loop body).</summary>
    public LoopDefinition SetBody(object? value) { AddOutput(new SetBodyStaticDefinition(value)); return this; }

    /// <summary>Transforms the exchange body (loop body).</summary>
    public LoopDefinition Transform(Func<IExchange, object?> transform) { AddOutput(new TransformDefinition(transform)); return this; }

    /// <summary>Sets a header (loop body).</summary>
    public LoopDefinition SetHeader(string key, object? value) { AddOutput(new SetHeaderStaticDefinition(key, value)); return this; }

    /// <summary>Sets a property (loop body).</summary>
    public LoopDefinition SetProperty(string key, object? value) { AddOutput(new SetPropertyStaticDefinition(key, value)); return this; }

    /// <summary>Logs a static message (loop body).</summary>
    public LoopDefinition Log(string message, LogLevel level = LogLevel.Information) { AddOutput(new LogStaticDefinition(message, level)); return this; }

    /// <summary>Stops exchange processing (loop body).</summary>
    public LoopDefinition Stop() { AddOutput(new StopDefinition()); return this; }

    // ── IProcessorDefinition ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var body = BuildPipeline(Outputs, context);
        if (_mode == LoopMode.Count)
            return new LoopProcessor(body, _count, _copy, _shareScope);
        if (_mode == LoopMode.CountFactory)
            return new LoopProcessor(body, _countFactory!, _copy, _shareScope);
        return new LoopProcessor(body, _condition!, _copy, _shareScope);
    }

    private static IProcessor BuildPipeline(IList<IProcessorDefinition> outputs, IRouteContext context)
    {
        return outputs.Count switch
        {
            0 => new DelegateProcessor(_ => { }),
            1 => outputs[0].CreateProcessor(context),
            _ => BuildMulti(outputs, context)
        };
    }

    private static PipelineProcessor BuildMulti(IList<IProcessorDefinition> outputs, IRouteContext context)
    {
        var pipeline = new PipelineProcessor();
        foreach (var o in outputs)
            pipeline.Add(o.CreateProcessor(context));
        return pipeline;
    }
}

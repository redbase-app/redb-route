using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Expressions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for the TryCatch EIP.
/// Leaf methods on this class build the try body.
/// Open catch branches with <see cref="Catch{TException}"/>, optionally add a finally block
/// with <see cref="Finally"/>, close the scope with <see cref="EndTryCatch"/>.
/// </summary>
public class TryCatchDefinition : RouteDefinition, IRouteScope
{
    internal readonly List<CatchDefinition> Catches = [];
    internal FinallyDefinition? FinallyBlock;

    internal TryCatchDefinition() { }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Opens a typed catch branch.</summary>
    public CatchDefinition Catch<TException>() where TException : Exception
        => Catch(typeof(TException));

    /// <summary>Opens a catch branch for the given exception type.</summary>
    public CatchDefinition Catch(Type exceptionType)
    {
        ArgumentNullException.ThrowIfNull(exceptionType);
        if (!typeof(Exception).IsAssignableFrom(exceptionType))
            throw new ArgumentException($"Type must be an Exception, got {exceptionType.Name}", nameof(exceptionType));

        var def = new CatchDefinition(exceptionType, this);
        Catches.Add(def);
        return def;
    }

    /// <summary>Opens the Finally block.</summary>
    /// <exception cref="InvalidOperationException">Thrown if Finally was already set.</exception>
    public FinallyDefinition Finally()
    {
        if (FinallyBlock != null)
            throw new InvalidOperationException("Finally() can only be called once per TryCatch scope.");
        FinallyBlock = new FinallyDefinition(this);
        return FinallyBlock;
    }

    /// <summary>Closes this TryCatch scope and returns the parent route definition.</summary>
    public IRouteDefinition EndTryCatch()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndTryCatch() called without a parent route."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndTryCatch();

    // ── IProcessorDefinition ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        IProcessor body = BuildPipeline(Outputs, context);
        var loggerFactory = context.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<TryCatchProcessor>();
        var proc = new TryCatchProcessor(body, logger);

        foreach (var catchDef in Catches)
        {
            IProcessor handler = BuildPipeline(catchDef.Outputs, context);
            proc.Catch(new CatchClause(catchDef.ExceptionType, handler, catchDef.WhenPredicate));
        }

        if (FinallyBlock != null)
            proc.SetFinally(BuildPipeline(FinallyBlock.Outputs, context));

        return proc;
    }

    internal static IProcessor BuildPipeline(IList<IProcessorDefinition> outputs, IRouteContext context)
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

    // ── Leaf DSL (try body) ────────────────────────────────────────────────────

    /// <summary>Sends the exchange to an endpoint (try body).</summary>
    public TryCatchDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Processes with a synchronous action (try body).</summary>
    public TryCatchDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Processes with an asynchronous action (try body).</summary>
    public TryCatchDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Processes with a pre-built processor (try body).</summary>
    public TryCatchDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }

    /// <summary>Sets the exchange body to a static value (try body).</summary>
    public TryCatchDefinition SetBody(object? value) { AddOutput(new SetBodyStaticDefinition(value)); return this; }

    /// <summary>Transforms the exchange body (try body).</summary>
    public TryCatchDefinition Transform(Func<IExchange, object?> transform) { AddOutput(new TransformDefinition(transform)); return this; }

    /// <summary>Sets a header (try body).</summary>
    public TryCatchDefinition SetHeader(string key, object? value) { AddOutput(new SetHeaderStaticDefinition(key, value)); return this; }

    /// <summary>Stops exchange processing (try body).</summary>
    public TryCatchDefinition Stop() { AddOutput(new StopDefinition()); return this; }
}

/// <summary>
/// A catch branch inside a <see cref="TryCatchDefinition"/>.
/// Leaf methods build the catch handler pipeline.
/// Navigate back with <see cref="EndCatch"/>, open another catch with <see cref="Catch{TException}"/>,
/// open finally with <see cref="Finally"/>, or close scope with <see cref="EndTryCatch"/>.
/// </summary>
public class CatchDefinition : RouteDefinition, IRouteScope
{
    internal readonly Type ExceptionType;
    internal Func<Exception, bool>? WhenPredicate;
    private readonly TryCatchDefinition _tryCatch;

    internal CatchDefinition(Type exceptionType, TryCatchDefinition tryCatch)
    {
        ExceptionType = exceptionType;
        _tryCatch = tryCatch;
        Parent = tryCatch;
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Adds an additional guard predicate to this catch clause.</summary>
    public CatchDefinition When(Func<Exception, bool> predicate) { WhenPredicate = predicate; return this; }

    /// <summary>Closes this catch branch and returns to the parent TryCatch.</summary>
    public TryCatchDefinition EndCatch() => _tryCatch;

    /// <summary>Opens another catch branch.</summary>
    public CatchDefinition Catch<TException>() where TException : Exception => _tryCatch.Catch<TException>();

    /// <summary>Opens the Finally block.</summary>
    public FinallyDefinition Finally() => _tryCatch.Finally();

    /// <summary>Closes the entire TryCatch scope.</summary>
    public IRouteDefinition EndTryCatch() => _tryCatch.EndTryCatch();
    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndTryCatch();
    // ── IProcessorDefinition ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => throw new InvalidOperationException(
            "CatchDefinition is compiled via its parent TryCatchDefinition.CreateProcessor.");

    // ── Leaf DSL (catch handler) ───────────────────────────────────────────────

    /// <summary>Sends the exchange to an endpoint (catch handler).</summary>
    public CatchDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Processes with a synchronous action (catch handler).</summary>
    public CatchDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Processes with an asynchronous action (catch handler).</summary>
    public CatchDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Processes with a pre-built processor (catch handler).</summary>
    public CatchDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }

    /// <summary>Sets the exchange body (catch handler).</summary>
    public CatchDefinition SetBody(object? value) { AddOutput(new SetBodyStaticDefinition(value)); return this; }

    /// <summary>Transforms the exchange body (catch handler).</summary>
    public CatchDefinition Transform(Func<IExchange, object?> transform) { AddOutput(new TransformDefinition(transform)); return this; }

    /// <summary>Sets a header (catch handler).</summary>
    public CatchDefinition SetHeader(string key, object? value) { AddOutput(new SetHeaderStaticDefinition(key, value)); return this; }

    /// <summary>Stops exchange processing (catch handler).</summary>
    public CatchDefinition Stop() { AddOutput(new StopDefinition()); return this; }
}

/// <summary>
/// The Finally block inside a <see cref="TryCatchDefinition"/>.
/// Leaf methods build the finally pipeline.
/// Close with <see cref="EndFinally"/> or <see cref="EndTryCatch"/>.
/// </summary>
public class FinallyDefinition : RouteDefinition, IRouteScope
{
    private readonly TryCatchDefinition _tryCatch;

    internal FinallyDefinition(TryCatchDefinition tryCatch)
    {
        _tryCatch = tryCatch;
        Parent = tryCatch;
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    /// <summary>Closes the Finally block and returns to the parent TryCatch.</summary>
    public TryCatchDefinition EndFinally() => _tryCatch;

    /// <summary>Closes the entire TryCatch scope.</summary>
    public IRouteDefinition EndTryCatch() => _tryCatch.EndTryCatch();
    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndTryCatch();
    // ── IProcessorDefinition ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => throw new InvalidOperationException(
            "FinallyDefinition is compiled via its parent TryCatchDefinition.CreateProcessor.");

    // ── Leaf DSL (finally body) ────────────────────────────────────────────────

    /// <summary>Sends the exchange to an endpoint (finally body).</summary>
    public FinallyDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Processes with a synchronous action (finally body).</summary>
    public FinallyDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Processes with an asynchronous action (finally body).</summary>
    public FinallyDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Processes with a pre-built processor (finally body).</summary>
    public FinallyDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }

    /// <summary>Sets the exchange body (finally body).</summary>
    public FinallyDefinition SetBody(object? value) { AddOutput(new SetBodyStaticDefinition(value)); return this; }

    /// <summary>Sets a header (finally body).</summary>
    public FinallyDefinition SetHeader(string key, object? value) { AddOutput(new SetHeaderStaticDefinition(key, value)); return this; }

    /// <summary>Stops exchange processing (finally body).</summary>
    public FinallyDefinition Stop() { AddOutput(new StopDefinition()); return this; }
}

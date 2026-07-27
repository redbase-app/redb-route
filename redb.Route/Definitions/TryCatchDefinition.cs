using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Scope-opener definition for the TryCatch EIP.
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>; child steps
/// build the try body. Open catch branches with <see cref="Catch{TException}"/>,
/// optionally add a finally block with <see cref="Finally"/>, close the scope with
/// <see cref="EndTryCatch"/>.
/// </summary>
public class TryCatchDefinition : RouteDefinitionBase<TryCatchDefinition>, IRouteScope, IBranchingDefinition
{
    internal readonly List<CatchDefinition> Catches = [];
    internal FinallyDefinition? FinallyBlock;

    /// <summary>
    /// The catch/finally handler bodies — logical children outside <see cref="Outputs"/> (which
    /// holds the try body), so a generic tree-walk (validation) reaches steps nested in a handler.
    /// </summary>
    public IEnumerable<IProcessorDefinition> Branches
    {
        get
        {
            foreach (var c in Catches) yield return c;
            if (FinallyBlock is not null) yield return FinallyBlock;
        }
    }

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
}

/// <summary>
/// A catch branch inside a <see cref="TryCatchDefinition"/>.
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>; child steps
/// build the catch handler pipeline. Navigate back with <see cref="EndCatch"/>,
/// open another catch with <see cref="Catch{TException}"/>, open finally with
/// <see cref="Finally"/>, or close the entire scope with <see cref="EndTryCatch"/>.
/// </summary>
public class CatchDefinition : RouteDefinitionBase<CatchDefinition>, IRouteScope
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
}

/// <summary>
/// The Finally block inside a <see cref="TryCatchDefinition"/>.
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>; child steps
/// build the finally pipeline. Close with <see cref="EndFinally"/> or
/// <see cref="EndTryCatch"/>.
/// </summary>
public class FinallyDefinition : RouteDefinitionBase<FinallyDefinition>, IRouteScope
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
}

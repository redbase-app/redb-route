using redb.Route.Abstractions;
using redb.Route.Expressions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Content-based router scope opener (Choice EIP).
/// Add branches with <see cref="When(Func{IExchange,bool})"/> / <see cref="Otherwise"/>.
/// Close with <see cref="EndChoice"/>.
/// <para>
/// Inherits the entire leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>;
/// however, leaf calls placed directly on a Choice scope are not part of the
/// standard EIP shape — use <c>When</c> / <c>Otherwise</c> branches instead.
/// </para>
/// </summary>
public class ChoiceDefinition : RouteDefinitionBase<ChoiceDefinition>, IRouteScope
{
    private readonly List<WhenDefinition> _whens = [];
    private OtherwiseDefinition? _otherwise;

    /// <summary>All registered When branches, in declaration order.</summary>
    public IReadOnlyList<WhenDefinition> Whens => _whens;

    /// <summary>The Otherwise branch (null if not declared).</summary>
    public OtherwiseDefinition? OtherwiseBranch => _otherwise;

    // ── Branch builders ───────────────────────────────────────────────────────

    /// <summary>Opens a When branch with a bool predicate.</summary>
    public WhenDefinition When(Func<IExchange, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var when = new WhenDefinition(predicate, this);
        _whens.Add(when);
        return when;
    }

    /// <summary>Opens a When branch with an <see cref="IExpression"/> predicate.</summary>
    public WhenDefinition When(IExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return When(e => ConvertToBoolean(expression.Evaluate<object?>(e)));
    }

    /// <summary>Apache Camel nested-lambda overload: opens a When branch with the given predicate,
    /// populates it via <paramref name="branch"/>, and returns this <see cref="ChoiceDefinition"/>
    /// so further <c>When</c>/<c>Otherwise</c> branches can be chained.</summary>
    public ChoiceDefinition When(Func<IExchange, bool> predicate, Action<WhenDefinition> branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        var w = When(predicate);
        branch(w);
        return this;
    }

    /// <summary>Nested-lambda overload accepting an <see cref="IExpression"/> predicate.</summary>
    public ChoiceDefinition When(IExpression expression, Action<WhenDefinition> branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        var w = When(expression);
        branch(w);
        return this;
    }

    /// <summary>Nested-lambda Otherwise overload.</summary>
    public ChoiceDefinition Otherwise(Action<OtherwiseDefinition> branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        branch(Otherwise());
        return this;
    }

    /// <summary>Opens the Otherwise (fallback) branch.</summary>
    /// <exception cref="InvalidOperationException">Thrown if Otherwise was already set.</exception>
    public OtherwiseDefinition Otherwise()
    {
        if (_otherwise != null)
            throw new InvalidOperationException("Otherwise() can only be called once per Choice scope.");
        _otherwise = new OtherwiseDefinition(this);
        return _otherwise;
    }

    /// <summary>Closes this choice scope and returns the parent route definition.</summary>
    public IRouteDefinition EndChoice()
        => (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndChoice() called without a parent route."));

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndChoice();

    // ── IProcessorDefinition ──────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var choice = new ChoiceProcessor();
        foreach (var when in _whens)
        {
            IProcessor body = BuildPipeline(when.Outputs, context);
            choice.When(when.Predicate, body);
        }
        if (_otherwise != null)
        {
            choice.SetOtherwise(BuildPipeline(_otherwise.Outputs, context));
        }
        return choice;
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

/// <summary>
/// A single When branch inside a <see cref="ChoiceDefinition"/>.
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>; close with
/// <see cref="EndWhen"/>, <see cref="Otherwise"/>, or <see cref="EndChoice"/>.
/// </summary>
public class WhenDefinition : RouteDefinitionBase<WhenDefinition>, IRouteScope
{
    internal readonly Func<IExchange, bool> Predicate;
    private readonly ChoiceDefinition _choice;

    /// <summary>Captured source <see cref="IPredicate"/> when this When branch was built from a predicate instance; null otherwise.</summary>
    public IPredicate? SourcePredicate { get; internal set; }

    /// <summary>Captured source string template (e.g. <c>"${header.flag}"</c>) when this When branch was built from a Simple expression; null otherwise.</summary>
    public string? SourceExpression { get; internal set; }

    internal WhenDefinition(Func<IExchange, bool> predicate, ChoiceDefinition choice)
    {
        Predicate = predicate;
        _choice = choice;
        Parent = choice;
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    /// <summary>Closes this When branch and returns the parent choice.</summary>
    public ChoiceDefinition EndWhen() => _choice;

    /// <summary>Opens another When branch on the parent choice.</summary>
    public WhenDefinition When(Func<IExchange, bool> predicate) => _choice.When(predicate);

    /// <summary>Opens another When branch with an expression predicate.</summary>
    public WhenDefinition When(IExpression expression) => _choice.When(expression);

    /// <summary>Opens the Otherwise fallback branch on the parent choice.</summary>
    public OtherwiseDefinition Otherwise() => _choice.Otherwise();

    /// <summary>Closes the entire Choice scope and returns the parent route definition.</summary>
    public IRouteDefinition EndChoice() => _choice.EndChoice();

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndChoice();

    // ── IProcessorDefinition ──────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => throw new InvalidOperationException(
            "WhenDefinition is compiled via its parent ChoiceDefinition.CreateProcessor.");
}

/// <summary>
/// The Otherwise (fallback) branch inside a <see cref="ChoiceDefinition"/>.
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>; close with
/// <see cref="EndOtherwise"/> or <see cref="EndChoice"/>.
/// </summary>
public class OtherwiseDefinition : RouteDefinitionBase<OtherwiseDefinition>, IRouteScope
{
    private readonly ChoiceDefinition _choice;

    internal OtherwiseDefinition(ChoiceDefinition choice)
    {
        _choice = choice;
        Parent = choice;
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    /// <summary>Closes this Otherwise branch and returns the parent choice.</summary>
    public ChoiceDefinition EndOtherwise() => _choice;

    /// <summary>Closes the entire Choice scope and returns the parent route definition.</summary>
    public IRouteDefinition EndChoice() => _choice.EndChoice();

    /// <inheritdoc cref="IRouteScope.End"/>
    public IRouteDefinition End() => EndChoice();

    // ── IProcessorDefinition ──────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => throw new InvalidOperationException(
            "OtherwiseDefinition is compiled via its parent ChoiceDefinition.CreateProcessor.");
}

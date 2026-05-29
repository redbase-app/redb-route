using redb.Route.Abstractions;
using redb.Route.Expressions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Content-based router scope opener (Choice EIP).
/// Add branches with <see cref="When(Func{IExchange,bool})"/> / <see cref="Otherwise"/>.
/// Close with <see cref="EndChoice"/>.
/// </summary>
public class ChoiceDefinition : RouteDefinition, IRouteScope
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

    private static bool ConvertToBoolean(object? value) => value switch
    {
        bool b => b,
        string s => bool.TryParse(s, out var result) ? result : !string.IsNullOrEmpty(s),
        null => false,
        _ => true
    };
}

/// <summary>
/// A single When branch inside a <see cref="ChoiceDefinition"/>.
/// Add steps with the leaf DSL, then close with <see cref="EndWhen"/>, <see cref="Otherwise"/>,
/// or <see cref="EndChoice"/>.
/// </summary>
public class WhenDefinition : RouteDefinition, IRouteScope
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

    // ── Leaf DSL ──────────────────────────────────────────────────────────────

    /// <summary>Sends the exchange to an endpoint.</summary>
    public WhenDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Processes the exchange with a synchronous action.</summary>
    public WhenDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Processes the exchange with an asynchronous action.</summary>
    public WhenDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Processes the exchange with a pre-built processor instance.</summary>
    public WhenDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }

    /// <summary>Sets the exchange body to a static value.</summary>
    public WhenDefinition SetBody(object? value) { AddOutput(new SetBodyStaticDefinition(value)); return this; }

    /// <summary>Sets the exchange body using a factory.</summary>
    public WhenDefinition SetBody(Func<IExchange, object?> factory) { AddOutput(new SetBodyFactoryDefinition(factory)); return this; }

    /// <summary>Transforms the exchange body.</summary>
    public WhenDefinition Transform(Func<IExchange, object?> transform) { AddOutput(new TransformDefinition(transform)); return this; }

    /// <summary>Removes the exchange body.</summary>
    public WhenDefinition RemoveBody() { AddOutput(new RemoveBodyDefinition()); return this; }

    /// <summary>Sets a header to a static value.</summary>
    public WhenDefinition SetHeader(string key, object? value) { AddOutput(new SetHeaderStaticDefinition(key, value)); return this; }

    /// <summary>Removes a header.</summary>
    public WhenDefinition RemoveHeader(string key) { AddOutput(new RemoveHeaderDefinition(key)); return this; }

    /// <summary>Sets a property to a static value.</summary>
    public WhenDefinition SetProperty(string key, object? value) { AddOutput(new SetPropertyStaticDefinition(key, value)); return this; }

    /// <summary>Stops exchange processing.</summary>
    public WhenDefinition Stop() { AddOutput(new StopDefinition()); return this; }
}

/// <summary>
/// The Otherwise (fallback) branch inside a <see cref="ChoiceDefinition"/>.
/// Add steps with the leaf DSL, then close with <see cref="EndOtherwise"/> or <see cref="EndChoice"/>.
/// </summary>
public class OtherwiseDefinition : RouteDefinition, IRouteScope
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

    // ── Leaf DSL ──────────────────────────────────────────────────────────────

    /// <summary>Sends the exchange to an endpoint.</summary>
    public OtherwiseDefinition To(string uri) { AddOutput(new ToDefinition(uri)); return this; }

    /// <summary>Processes the exchange with a synchronous action.</summary>
    public OtherwiseDefinition Process(Action<IExchange> action) { AddOutput(new ProcessActionDefinition(action)); return this; }

    /// <summary>Processes the exchange with an asynchronous action.</summary>
    public OtherwiseDefinition Process(Func<IExchange, CancellationToken, Task> action) { AddOutput(new ProcessAsyncDefinition(action)); return this; }

    /// <summary>Processes the exchange with a pre-built processor instance.</summary>
    public OtherwiseDefinition Process(IProcessor processor) { AddOutput(new ProcessInstanceDefinition(processor)); return this; }

    /// <summary>Sets the exchange body to a static value.</summary>
    public OtherwiseDefinition SetBody(object? value) { AddOutput(new SetBodyStaticDefinition(value)); return this; }

    /// <summary>Sets the exchange body using a factory.</summary>
    public OtherwiseDefinition SetBody(Func<IExchange, object?> factory) { AddOutput(new SetBodyFactoryDefinition(factory)); return this; }

    /// <summary>Transforms the exchange body.</summary>
    public OtherwiseDefinition Transform(Func<IExchange, object?> transform) { AddOutput(new TransformDefinition(transform)); return this; }

    /// <summary>Removes the exchange body.</summary>
    public OtherwiseDefinition RemoveBody() { AddOutput(new RemoveBodyDefinition()); return this; }

    /// <summary>Sets a header to a static value.</summary>
    public OtherwiseDefinition SetHeader(string key, object? value) { AddOutput(new SetHeaderStaticDefinition(key, value)); return this; }

    /// <summary>Removes a header.</summary>
    public OtherwiseDefinition RemoveHeader(string key) { AddOutput(new RemoveHeaderDefinition(key)); return this; }

    /// <summary>Sets a property to a static value.</summary>
    public OtherwiseDefinition SetProperty(string key, object? value) { AddOutput(new SetPropertyStaticDefinition(key, value)); return this; }

    /// <summary>Stops exchange processing.</summary>
    public OtherwiseDefinition Stop() { AddOutput(new StopDefinition()); return this; }
}

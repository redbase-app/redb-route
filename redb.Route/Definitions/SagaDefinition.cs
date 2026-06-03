using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Definition for a Saga step. Collects saga step entries and builds a
/// <see cref="SagaProcessor"/> at route-build time.
/// Supports both callback-style (via <see cref="ISagaDefinition"/>) and
/// fluent-scope-style (via <see cref="SagaStep"/> / <see cref="EndSaga"/>).
/// Inherits the leaf DSL from <see cref="RouteDefinitionBase{TSelf}"/>.
/// </summary>
public class SagaDefinition : RouteDefinitionBase<SagaDefinition>, ISagaDefinition, IRouteScope
{
    private readonly List<SagaStepEntry> _entries = [];

    /// <summary>The collected saga step entries (forward action + optional compensation).</summary>
    public IReadOnlyList<SagaStepEntry> Entries => _entries;

    /// <summary>Optional callback invoked after all steps complete successfully.</summary>
    public Func<IExchange, CancellationToken, Task>? CompletionCallback { get; private set; }

    // ── ISagaDefinition (callback-style builder) ──────────────────────────────

    /// <inheritdoc />
    public ISagaDefinition Step(Action<IExchange> action, Action<IExchange> compensate)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(compensate);
        _entries.Add(new SagaStepEntry(
            (e, _) => { action(e); return Task.CompletedTask; },
            (e, _) => { compensate(e); return Task.CompletedTask; }));
        return this;
    }

    /// <inheritdoc />
    public ISagaDefinition Step(
        Func<IExchange, CancellationToken, Task> action,
        Func<IExchange, CancellationToken, Task> compensate)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(compensate);
        _entries.Add(new SagaStepEntry(action, compensate));
        return this;
    }

    /// <inheritdoc />
    public ISagaDefinition Step(Action<IExchange> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _entries.Add(new SagaStepEntry(
            (e, _) => { action(e); return Task.CompletedTask; },
            null));
        return this;
    }

    /// <inheritdoc />
    public ISagaDefinition Step(Func<IExchange, CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _entries.Add(new SagaStepEntry(action, null));
        return this;
    }

    /// <inheritdoc />
    public ISagaDefinition OnCompletion(Action<IExchange> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        CompletionCallback = (e, _) => { callback(e); return Task.CompletedTask; };
        return this;
    }

    /// <inheritdoc />
    public ISagaDefinition OnCompletion(Func<IExchange, CancellationToken, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        CompletionCallback = callback;
        return this;
    }

    // ── Scope / fluent-chain DSL ──────────────────────────────────────────────

    /// <summary>Adds a synchronous saga step with both action and compensation.</summary>
    public SagaDefinition SagaStep(Action<IExchange> action, Action<IExchange> compensate)
    {
        Step(action, compensate);
        return this;
    }

    /// <summary>Adds a synchronous saga step with no compensation.</summary>
    public SagaDefinition SagaStep(Action<IExchange> action)
    {
        Step(action);
        return this;
    }

    /// <summary>Adds an asynchronous saga step with both action and compensation.</summary>
    public SagaDefinition SagaStep(
        Func<IExchange, CancellationToken, Task> action,
        Func<IExchange, CancellationToken, Task> compensate)
    {
        Step(action, compensate);
        return this;
    }

    /// <summary>Adds an asynchronous saga step with no compensation.</summary>
    public SagaDefinition SagaStep(Func<IExchange, CancellationToken, Task> action)
    {
        Step(action);
        return this;
    }

    /// <summary>Sets the saga completion callback (synchronous).</summary>
    public SagaDefinition OnSagaCompletion(Action<IExchange> callback)
    {
        OnCompletion(callback);
        return this;
    }

    /// <summary>Sets the saga completion callback (asynchronous).</summary>
    public SagaDefinition OnSagaCompletion(Func<IExchange, CancellationToken, Task> callback)
    {
        OnCompletion(callback);
        return this;
    }

    /// <summary>Closes this saga scope and returns the parent route definition.</summary>
    public IRouteDefinition EndSaga()
    {
        return (IRouteDefinition)(Parent ?? throw new InvalidOperationException(
            "EndSaga() can only be called on a saga scope opened via RouteDefinition.Saga()."));
    }

    /// <inheritdoc />
    public IRouteDefinition End() => EndSaga();

    // ── ProcessorDefinition ───────────────────────────────────────────────────

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        if (_entries.Count == 0)
            throw new InvalidOperationException("Saga must have at least one step.");
        var logger = context.GetService<ILoggerFactory>()?.CreateLogger<SagaProcessor>();
        return new SagaProcessor(_entries.ToArray(), CompletionCallback, logger);
    }
}

/// <summary>A single saga step entry: forward action + optional compensation.</summary>
/// <param name="Action">Forward action to execute.</param>
/// <param name="Compensate">Compensation to run on rollback (null = no compensation).</param>
public sealed record SagaStepEntry(
    Func<Abstractions.IExchange, CancellationToken, Task> Action,
    Func<Abstractions.IExchange, CancellationToken, Task>? Compensate);

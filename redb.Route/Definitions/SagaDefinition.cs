using redb.Route.Abstractions;

namespace redb.Route.Definitions;

/// <summary>
/// Internal builder for saga definition. Collects steps and builds <see cref="SagaRouteStep"/>.
/// </summary>
internal sealed class SagaDefinition : ISagaDefinition
{
    internal readonly List<SagaStepEntry> Entries = [];
    internal Func<IExchange, CancellationToken, Task>? CompletionCallback;

    /// <inheritdoc />
    public ISagaDefinition Step(Action<IExchange> action, Action<IExchange> compensate)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(compensate);
        Entries.Add(new SagaStepEntry(
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
        Entries.Add(new SagaStepEntry(action, compensate));
        return this;
    }

    /// <inheritdoc />
    public ISagaDefinition Step(Action<IExchange> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Entries.Add(new SagaStepEntry(
            (e, _) => { action(e); return Task.CompletedTask; },
            null));
        return this;
    }

    /// <inheritdoc />
    public ISagaDefinition Step(Func<IExchange, CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Entries.Add(new SagaStepEntry(action, null));
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

    /// <summary>Builds the saga route step. Validates at least one step exists.</summary>
    internal SagaRouteStep Build()
    {
        if (Entries.Count == 0)
            throw new InvalidOperationException("Saga must have at least one step.");
        return new SagaRouteStep(Entries.ToArray(), CompletionCallback);
    }
}

/// <summary>A single saga step entry: forward action + optional compensation.</summary>
/// <param name="Action">Forward action to execute.</param>
/// <param name="Compensate">Compensation to run on rollback (null = no compensation).</param>
public sealed record SagaStepEntry(
    Func<Abstractions.IExchange, CancellationToken, Task> Action,
    Func<Abstractions.IExchange, CancellationToken, Task>? Compensate);

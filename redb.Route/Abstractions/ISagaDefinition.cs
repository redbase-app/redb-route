namespace redb.Route.Abstractions;

/// <summary>
/// Fluent builder for configuring saga steps with forward actions and compensation.
/// </summary>
public interface ISagaDefinition
{
    /// <summary>Adds a synchronous saga step with compensation.</summary>
    /// <param name="action">Forward action to execute.</param>
    /// <param name="compensate">Compensation action to run on rollback.</param>
    /// <returns>This definition for chaining.</returns>
    ISagaDefinition Step(Action<IExchange> action, Action<IExchange> compensate);

    /// <summary>Adds an async saga step with compensation.</summary>
    /// <param name="action">Forward action to execute.</param>
    /// <param name="compensate">Compensation action to run on rollback.</param>
    /// <returns>This definition for chaining.</returns>
    ISagaDefinition Step(
        Func<IExchange, CancellationToken, Task> action,
        Func<IExchange, CancellationToken, Task> compensate);

    /// <summary>Adds a forward-only saga step (no compensation on rollback).</summary>
    /// <param name="action">Forward action to execute.</param>
    /// <returns>This definition for chaining.</returns>
    ISagaDefinition Step(Action<IExchange> action);

    /// <summary>Adds an async forward-only saga step (no compensation on rollback).</summary>
    /// <param name="action">Forward action to execute.</param>
    /// <returns>This definition for chaining.</returns>
    ISagaDefinition Step(Func<IExchange, CancellationToken, Task> action);

    /// <summary>Sets a callback invoked when all saga steps complete successfully.</summary>
    /// <param name="callback">Callback to invoke on saga completion.</param>
    /// <returns>This definition for chaining.</returns>
    ISagaDefinition OnCompletion(Action<IExchange> callback);

    /// <summary>Sets an async callback invoked when all saga steps complete successfully.</summary>
    /// <param name="callback">Callback to invoke on saga completion.</param>
    /// <returns>This definition for chaining.</returns>
    ISagaDefinition OnCompletion(Func<IExchange, CancellationToken, Task> callback);
}

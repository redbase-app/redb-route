namespace redb.Route.Abstractions.Typed;

/// <summary>
/// Typed fluent API for defining a message route.
/// Generic wrapper over IRouteDefinition — casts Body to TIn at DSL level.
/// Zero overhead: internally operates on untyped Exchange.In.Body = object?.
/// </summary>
/// <typeparam name="TIn">Expected type of the message body.</typeparam>
public interface IRouteDefinition<TIn> : IRouteDefinition
{
    /// <summary>Filters exchanges by typed predicate.</summary>
    /// <param name="predicate">Typed filter condition.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition<TIn> Filter(Func<TIn, bool> predicate);

    /// <summary>Transforms the message body to a new type.</summary>
    /// <typeparam name="TOut">Output type.</typeparam>
    /// <param name="transform">Transform function.</param>
    /// <returns>Definition typed to TOut for chaining.</returns>
    IRouteDefinition<TOut> Transform<TOut>(Func<TIn, TOut> transform);

    /// <summary>Processes the typed message body.</summary>
    /// <param name="processor">Typed processing delegate.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition<TIn> Process(Func<TIn, CancellationToken, Task<TIn>> processor);

    /// <summary>Processes the typed message body with a synchronous action.</summary>
    /// <param name="action">Typed processing action.</param>
    /// <returns>This definition for chaining.</returns>
    IRouteDefinition<TIn> Process(Action<TIn> action);
}


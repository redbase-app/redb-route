namespace redb.Route.Definitions;

/// <summary>
/// A route configuration (Apache Camel's Route Configuration): error handling declared once, apart from the routes,
/// and applied to the routes of any builder of the context.
/// <para>
/// A configuration without an id is the default: it applies to every route that has no configurations assigned. A
/// configuration with an id applies to the routes that name it with <c>.RouteConfigurationId("id")</c>.
/// </para>
/// Declared in a <see cref="Core.RouteConfigurationBuilder"/>.
/// </summary>
public sealed class RouteConfigurationDefinition
{
    private readonly List<OnExceptionDefinition> _exceptionDefinitions = [];

    internal RouteConfigurationDefinition(string? id)
    {
        if (id is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    /// <summary>The id routes name the configuration by; <c>null</c> for the default configuration.</summary>
    public string? Id { get; }

    /// <summary>The exception handlers of the configuration, in declaration order.</summary>
    public IReadOnlyList<OnExceptionDefinition> ExceptionDefinitions => _exceptionDefinitions;

    /// <summary>
    /// Defines an exception handler of the configuration: every setting and every step an <c>OnException</c> of a
    /// builder has (redeliveries, <c>OnWhen</c>, <c>Handled</c>, the handler's steps). For a failure of a route the
    /// handler nearest to the exception type wins, wherever it is declared; the route's own handler wins a tie, and
    /// the configuration's handler is used instead of a handler of the same type on the route's builder.
    /// </summary>
    /// <typeparam name="TException">Exception type to handle.</typeparam>
    /// <returns>Definition for fluent chaining of handler steps and configuration.</returns>
    public OnExceptionDefinition OnException<TException>() where TException : Exception
        => OnException(typeof(TException));

    /// <summary>Defines an exception handler of the configuration for several exception types at once.</summary>
    /// <param name="exceptionTypes">Exception types to handle.</param>
    /// <returns>Definition for fluent chaining.</returns>
    public OnExceptionDefinition OnException(params Type[] exceptionTypes)
    {
        if (exceptionTypes is not { Length: > 0 })
            throw new ArgumentException("At least one exception type must be specified.", nameof(exceptionTypes));
        foreach (var t in exceptionTypes)
            if (!typeof(Exception).IsAssignableFrom(t))
                throw new ArgumentException($"Type '{t.Name}' is not an Exception type.", nameof(exceptionTypes));

        var def = new OnExceptionDefinition(exceptionTypes);
        _exceptionDefinitions.Add(def);
        return def;
    }
}

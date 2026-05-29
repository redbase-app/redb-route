using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Leaf definition that rethrows the exception stored on the exchange.
/// </summary>
public sealed class RethrowExceptionDefinition : ProcessorDefinition
{
    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange =>
            throw exchange.Exception ?? new InvalidOperationException("No exception on exchange to rethrow."));
}

/// <summary>
/// Leaf definition that throws a new <see cref="Exception"/> with a static message.
/// </summary>
public sealed class ThrowMessageDefinition : ProcessorDefinition
{
    private readonly string _message;

    /// <summary>Creates a throw definition with a static message.</summary>
    public ThrowMessageDefinition(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _message = message;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(_ => throw new Exception(_message));
}

/// <summary>
/// Leaf definition that throws a specific pre-built exception instance.
/// </summary>
public sealed class ThrowExceptionDefinition : ProcessorDefinition
{
    private readonly Exception _exception;

    /// <summary>Creates a throw definition from an exception instance.</summary>
    public ThrowExceptionDefinition(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _exception = exception;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(_ => throw _exception);
}

/// <summary>
/// Leaf definition that instantiates and throws an exception by type.
/// </summary>
public sealed class ThrowExceptionTypeDefinition : ProcessorDefinition
{
    private readonly Type _exceptionType;
    private readonly string? _message;

    /// <summary>Creates a throw definition for a specific exception type.</summary>
    /// <param name="exceptionType">Exception type to instantiate and throw.</param>
    /// <param name="message">Optional message to pass to the exception constructor.</param>
    public ThrowExceptionTypeDefinition(Type exceptionType, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(exceptionType);
        if (!typeof(Exception).IsAssignableFrom(exceptionType))
            throw new ArgumentException($"Type '{exceptionType.Name}' does not inherit from Exception.", nameof(exceptionType));
        _exceptionType = exceptionType;
        _message = message;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(_ =>
            throw (Exception)(_message is not null
                ? Activator.CreateInstance(_exceptionType, _message)
                : Activator.CreateInstance(_exceptionType))!);
}

/// <summary>
/// Leaf definition that marks the exchange exception as handled.
/// </summary>
public sealed class ExceptionHandledDefinition : ProcessorDefinition
{
    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange =>
        {
            exchange.ExceptionHandled = true;
            exchange.Exception = null;
        });
}

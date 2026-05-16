using redb.Route.Abstractions;

namespace redb.Route.Validation;

/// <summary>
/// Validates the exchange using a user-supplied predicate function.
/// Gives developers full freedom to define custom validation logic.
/// <para>
/// The predicate receives the exchange and returns <c>true</c> for valid, <c>false</c> for invalid.
/// An optional error message factory can produce a descriptive error on failure.
/// </para>
/// </summary>
public sealed class PredicateValidator : IMessageValidator
{
    private readonly Func<IExchange, bool> _predicate;
    private readonly Func<IExchange, string>? _errorFactory;
    private readonly string _defaultError;

    /// <summary>Creates a predicate-based validator.</summary>
    /// <param name="predicate">Validation predicate: returns true if valid.</param>
    /// <param name="errorMessage">Static error message on failure (default: "Validation failed").</param>
    public PredicateValidator(Func<IExchange, bool> predicate, string errorMessage = "Validation failed")
    {
        _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        _defaultError = errorMessage;
    }

    /// <summary>Creates a predicate-based validator with a dynamic error message.</summary>
    /// <param name="predicate">Validation predicate: returns true if valid.</param>
    /// <param name="errorFactory">Factory to produce the error message from the exchange.</param>
    public PredicateValidator(Func<IExchange, bool> predicate, Func<IExchange, string> errorFactory)
    {
        _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        _errorFactory = errorFactory ?? throw new ArgumentNullException(nameof(errorFactory));
        _defaultError = "Validation failed";
    }

    /// <inheritdoc />
    public ValidationResult Validate(IExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange, nameof(exchange));

        if (_predicate(exchange))
            return ValidationResult.Success();

        var error = _errorFactory != null ? _errorFactory(exchange) : _defaultError;
        return ValidationResult.Failure(error);
    }
}

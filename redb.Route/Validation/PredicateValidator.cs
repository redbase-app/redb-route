using redb.Route.Abstractions;
using redb.Route.Predicates;

namespace redb.Route.Validation;

/// <summary>
/// Validator that checks an <see cref="IPredicate"/> against the exchange. The predicate is
/// awaited through <see cref="IPredicate.MatchesAsync"/> when the processor validates
/// asynchronously, so a predicate that consults a store or a service does not block. A plain
/// delegate is wrapped in a <see cref="LambdaPredicate"/> at the boundary.
/// </summary>
public sealed class PredicateValidator : IMessageValidator
{
    private readonly IPredicate _predicate;
    private readonly Func<IExchange, string>? _errorFactory;
    private readonly string _defaultError;

    /// <summary>Creates a validator from a predicate instance and a fixed error message.</summary>
    /// <param name="predicate">The condition that must hold.</param>
    /// <param name="errorMessage">The error reported when it does not.</param>
    public PredicateValidator(IPredicate predicate, string errorMessage = "Validation failed")
    {
        _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        _defaultError = errorMessage;
    }

    /// <summary>Creates a validator from a predicate instance and an error factory.</summary>
    /// <param name="predicate">The condition that must hold.</param>
    /// <param name="errorFactory">Builds the error message from the failing exchange.</param>
    public PredicateValidator(IPredicate predicate, Func<IExchange, string> errorFactory)
    {
        _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        _errorFactory = errorFactory ?? throw new ArgumentNullException(nameof(errorFactory));
        _defaultError = "Validation failed";
    }

    /// <summary>Creates a validator from a delegate and a fixed error message.</summary>
    /// <param name="predicate">The condition that must hold.</param>
    /// <param name="errorMessage">The error reported when it does not.</param>
    public PredicateValidator(Func<IExchange, bool> predicate, string errorMessage = "Validation failed")
        : this(new LambdaPredicate(predicate ?? throw new ArgumentNullException(nameof(predicate))), errorMessage)
    {
    }

    /// <summary>Creates a validator from a delegate and an error factory.</summary>
    /// <param name="predicate">The condition that must hold.</param>
    /// <param name="errorFactory">Builds the error message from the failing exchange.</param>
    public PredicateValidator(Func<IExchange, bool> predicate, Func<IExchange, string> errorFactory)
        : this(new LambdaPredicate(predicate ?? throw new ArgumentNullException(nameof(predicate))), errorFactory)
    {
    }

    /// <inheritdoc />
    public ValidationResult Validate(IExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange, nameof(exchange));
        return ToResult(_predicate.Matches(exchange), exchange);
    }

    /// <inheritdoc />
    public async Task<ValidationResult> ValidateAsync(IExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange, nameof(exchange));
        return ToResult(await _predicate.MatchesAsync(exchange).ConfigureAwait(false), exchange);
    }

    private ValidationResult ToResult(bool holds, IExchange exchange)
    {
        if (holds)
            return ValidationResult.Success();

        var error = _errorFactory != null ? _errorFactory(exchange) : _defaultError;
        return ValidationResult.Failure(error);
    }
}

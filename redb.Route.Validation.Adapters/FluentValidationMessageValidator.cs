using FluentValidation;
using redb.Route.Abstractions;

namespace redb.Route.Validation.Adapters;

/// <summary>
/// Bridges <see href="https://docs.fluentvalidation.net/">FluentValidation</see>'s
/// <see cref="IValidator{T}"/> to redb.Route's <see cref="IMessageValidator"/>.
/// <para>
/// The exchange body is cast to <typeparamref name="T"/> before validation. A null body or
/// a body of incompatible type yields a single error result; the supplied severity governs
/// how <see cref="ValidateProcessor"/> reacts.
/// </para>
/// </summary>
/// <typeparam name="T">The expected body type.</typeparam>
public sealed class FluentValidationMessageValidator<T> : IMessageValidator
{
    private readonly IValidator<T> _validator;
    private readonly ValidationSeverity _severity;

    /// <summary>
    /// Creates a FluentValidation-backed message validator.
    /// </summary>
    /// <param name="validator">FluentValidation validator instance.</param>
    /// <param name="severity">Severity for failures (default <see cref="ValidationSeverity.Error"/>).</param>
    public FluentValidationMessageValidator(IValidator<T> validator,
        ValidationSeverity severity = ValidationSeverity.Error)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _severity = severity;
    }

    /// <inheritdoc />
    public ValidationResult Validate(IExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        var body = exchange.In.Body;

        if (body is null)
            return ValidationResult.Failure(new[] { "Message body is null." }, _severity);

        if (body is not T typed)
            return ValidationResult.Failure(
                new[] { $"Body of type '{body.GetType().FullName}' is not assignable to '{typeof(T).FullName}'." },
                _severity);

        var result = _validator.Validate(typed);
        if (result.IsValid)
            return ValidationResult.Success();

        var errors = result.Errors
            .Select(e => string.IsNullOrEmpty(e.PropertyName) ? e.ErrorMessage : $"{e.PropertyName}: {e.ErrorMessage}")
            .ToArray();
        return ValidationResult.Failure(errors, _severity);
    }
}

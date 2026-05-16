using redb.Route.Abstractions;

namespace redb.Route.Validation;

/// <summary>
/// Pipeline processor that validates the exchange using an <see cref="IMessageValidator"/>.
/// <para>
/// By default, a failed validation sets the <c>ValidationErrors</c> property on the exchange
/// and throws a <see cref="ValidationException"/>. If <see cref="ThrowOnFailure"/> is <c>false</c>,
/// the processor only sets the property without throwing — giving the developer freedom to handle
/// errors downstream via <c>.Choice()</c> or <c>.Filter()</c>.
/// </para>
/// </summary>
public sealed class ValidateProcessor : IProcessor
{
    /// <summary>Well-known property key for validation error messages.</summary>
    public const string ValidationErrorsProperty = "ValidationErrors";

    /// <summary>Well-known property key for the boolean validation result.</summary>
    public const string ValidationResultProperty = "ValidationResult";

    /// <summary>
    /// Well-known message-header key receiving warning-severity validation messages.
    /// Multiple warnings are appended as a semicolon-joined string.
    /// </summary>
    public const string ValidationWarningsHeader = "redb.validation.warnings";

    private readonly IMessageValidator _validator;

    /// <summary>
    /// Whether to throw <see cref="ValidationException"/> on failure.
    /// Default is <c>true</c>. Set to <c>false</c> for soft validation that only sets properties.
    /// Ignored when <see cref="ValidationResult.Severity"/> is <see cref="ValidationSeverity.Warning"/>.
    /// </summary>
    public bool ThrowOnFailure { get; }

    /// <summary>Creates a validate processor that wraps the specified validator.</summary>
    /// <param name="validator">Validator to apply.</param>
    /// <param name="throwOnFailure">Whether to throw on validation failure (default: true).</param>
    public ValidateProcessor(IMessageValidator validator, bool throwOnFailure = true)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        ThrowOnFailure = throwOnFailure;
    }

    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        var result = _validator.Validate(exchange);

        // Always set properties so downstream steps can inspect them
        exchange.Properties[ValidationResultProperty] = result.IsValid;
        exchange.Properties[ValidationErrorsProperty] = result.IsValid
            ? null
            : string.Join("; ", result.Errors);

        if (!result.IsValid)
        {
            if (result.Severity == ValidationSeverity.Warning)
            {
                // Soft path: append to warnings header, never throw.
                var newMessages = string.Join("; ", result.Errors);
                if (exchange.In.Headers.TryGetValue(ValidationWarningsHeader, out var existing) &&
                    existing is string s && !string.IsNullOrEmpty(s))
                {
                    exchange.In.Headers[ValidationWarningsHeader] = s + "; " + newMessages;
                }
                else
                {
                    exchange.In.Headers[ValidationWarningsHeader] = newMessages;
                }
            }
            else if (ThrowOnFailure)
            {
                throw new ValidationException(result.Errors);
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Exception thrown when message validation fails and <see cref="ValidateProcessor.ThrowOnFailure"/> is true.
/// </summary>
public sealed class ValidationException : Exception
{
    /// <summary>Individual validation error messages.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Creates a validation exception with the specified errors.</summary>
    /// <param name="errors">Validation error messages.</param>
    public ValidationException(IReadOnlyList<string> errors)
        : base($"Validation failed: {string.Join("; ", errors)}")
    {
        Errors = errors;
    }
}

using redb.Route.Abstractions;

namespace redb.Route.Validation;

/// <summary>
/// Result of a message validation operation.
/// </summary>
public sealed class ValidationResult
{
    /// <summary>Whether the validation passed.</summary>
    public bool IsValid { get; }

    /// <summary>Human-readable error messages (empty when valid).</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>
    /// Severity for failures. Defaults to <see cref="ValidationSeverity.Error"/> for backward
    /// compatibility. Adapters that produce non-blocking diagnostics may set
    /// <see cref="ValidationSeverity.Warning"/>.
    /// </summary>
    public ValidationSeverity Severity { get; }

    private ValidationResult(bool isValid, IReadOnlyList<string> errors, ValidationSeverity severity)
    {
        IsValid = isValid;
        Errors = errors;
        Severity = severity;
    }

    /// <summary>Creates a successful validation result.</summary>
    public static ValidationResult Success() => new(true, Array.Empty<string>(), ValidationSeverity.Error);

    /// <summary>Creates a failed validation result with one or more errors.</summary>
    /// <param name="errors">Validation error messages.</param>
    public static ValidationResult Failure(IReadOnlyList<string> errors) => new(false, errors, ValidationSeverity.Error);

    /// <summary>Creates a failed validation result with a single error message.</summary>
    /// <param name="error">Validation error message.</param>
    public static ValidationResult Failure(string error) => new(false, new[] { error }, ValidationSeverity.Error);

    /// <summary>Creates a failed validation result with explicit severity.</summary>
    /// <param name="errors">Validation error messages.</param>
    /// <param name="severity">Severity classification.</param>
    public static ValidationResult Failure(IReadOnlyList<string> errors, ValidationSeverity severity)
        => new(false, errors, severity);
}

/// <summary>
/// Contract for message validators. Validates the exchange body (or a specific part of it)
/// and returns a structured <see cref="ValidationResult"/>.
/// <para>
/// Implementations include JSON Schema validation, XSD validation, and custom predicate-based validation.
/// </para>
/// </summary>
public interface IMessageValidator
{
    /// <summary>
    /// Validates the exchange and returns a result indicating success or failure with error details.
    /// </summary>
    /// <param name="exchange">The exchange to validate.</param>
    /// <returns>A <see cref="ValidationResult"/> with validation outcome and any errors.</returns>
    ValidationResult Validate(IExchange exchange);
}

namespace redb.Route.Validation;

/// <summary>
/// Severity classification for a <see cref="ValidationResult"/>.
/// Lets a single <see cref="IMessageValidator"/> contract distinguish soft warnings
/// (route continues, header set) from hard errors (route may throw).
/// </summary>
public enum ValidationSeverity
{
    /// <summary>
    /// Validation failed but processing should continue. <see cref="ValidateProcessor"/>
    /// appends the errors to the <c>redb.validation.warnings</c> in-header (semicolon-joined),
    /// and never throws.
    /// </summary>
    Warning = 0,

    /// <summary>
    /// Validation failed and is treated as an error. Honours the existing
    /// <see cref="ValidateProcessor.ThrowOnFailure"/> flag — throws
    /// <see cref="ValidationException"/> when set, otherwise sets properties only.
    /// </summary>
    Error = 1
}

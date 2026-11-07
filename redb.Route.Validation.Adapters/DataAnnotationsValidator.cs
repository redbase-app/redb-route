using System.ComponentModel.DataAnnotations;
using redb.Route.Abstractions;

namespace redb.Route.Validation.Adapters;

/// <summary>
/// Bridges System.ComponentModel.DataAnnotations to redb.Route's <see cref="IMessageValidator"/>.
/// Runs <see cref="Validator.TryValidateObject(object, ValidationContext, ICollection{System.ComponentModel.DataAnnotations.ValidationResult}, bool)"/>
/// against the exchange body and aggregates errors with their member names.
/// </summary>
public sealed class DataAnnotationsValidator : IMessageValidator
{
    private readonly ValidationSeverity _severity;
    private readonly IServiceProvider? _serviceProvider;

    /// <summary>
    /// Creates a DataAnnotations-backed message validator.
    /// </summary>
    /// <param name="severity">Severity for failures (default <see cref="ValidationSeverity.Error"/>).</param>
    /// <param name="serviceProvider">
    /// Optional <see cref="IServiceProvider"/> propagated to <see cref="ValidationContext"/>
    /// for <c>IValidatableObject</c> implementations that need DI services.
    /// </param>
    public DataAnnotationsValidator(
        ValidationSeverity severity = ValidationSeverity.Error,
        IServiceProvider? serviceProvider = null)
    {
        _severity = severity;
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public ValidationResult Validate(IExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        var body = exchange.In.Body;
        if (body is null)
            return ValidationResult.Failure(new[] { "Message body is null." }, _severity);

        var ctx = new ValidationContext(body, _serviceProvider, items: null);
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var ok = Validator.TryValidateObject(body, ctx, results, validateAllProperties: true);
        if (ok)
            return ValidationResult.Success();

        var errors = results
            .Select(r => r.MemberNames.Any()
                ? $"{string.Join(",", r.MemberNames)}: {r.ErrorMessage}"
                : (r.ErrorMessage ?? "Validation failed"))
            .ToArray();
        return ValidationResult.Failure(errors, _severity);
    }
}

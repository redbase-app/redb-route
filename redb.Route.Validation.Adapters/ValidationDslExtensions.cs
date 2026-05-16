using FluentValidation;
using redb.Route.Abstractions;
using redb.Route.Definitions;
using redb.Route.Validation;

namespace redb.Route.Validation.Adapters;

/// <summary>
/// Fluent DSL helpers for attaching the <see cref="FluentValidationMessageValidator{T}"/>
/// and <see cref="DataAnnotationsValidator"/> adapters to a route definition.
/// </summary>
public static class ValidationDslExtensions
{
    /// <summary>
    /// Attaches a FluentValidation validator as an inline pipeline step.
    /// </summary>
    /// <typeparam name="T">Expected body type.</typeparam>
    /// <param name="route">Route definition.</param>
    /// <param name="validator">FluentValidation validator.</param>
    /// <param name="severity">Severity for failures (default <see cref="ValidationSeverity.Error"/>).</param>
    /// <param name="throwOnFailure">
    /// When severity is <see cref="ValidationSeverity.Error"/>, controls whether a
    /// <see cref="ValidationException"/> is thrown. Ignored for warnings (never throws).
    /// </param>
    public static IRouteDefinition ValidateFluent<T>(
        this IRouteDefinition route,
        IValidator<T> validator,
        ValidationSeverity severity = ValidationSeverity.Error,
        bool throwOnFailure = true)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(validator);
        return route.Validate(new FluentValidationMessageValidator<T>(validator, severity), throwOnFailure);
    }

    /// <summary>
    /// Attaches a DataAnnotations validator as an inline pipeline step.
    /// </summary>
    /// <param name="route">Route definition.</param>
    /// <param name="severity">Severity for failures (default <see cref="ValidationSeverity.Error"/>).</param>
    /// <param name="throwOnFailure">
    /// When severity is <see cref="ValidationSeverity.Error"/>, controls whether a
    /// <see cref="ValidationException"/> is thrown. Ignored for warnings (never throws).
    /// </param>
    /// <param name="serviceProvider">
    /// Optional service provider passed to <see cref="System.ComponentModel.DataAnnotations.ValidationContext"/>.
    /// </param>
    public static IRouteDefinition ValidateAnnotations(
        this IRouteDefinition route,
        ValidationSeverity severity = ValidationSeverity.Error,
        bool throwOnFailure = true,
        IServiceProvider? serviceProvider = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        return route.Validate(new DataAnnotationsValidator(severity, serviceProvider), throwOnFailure);
    }
}

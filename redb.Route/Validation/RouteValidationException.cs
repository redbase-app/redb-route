using System;
using System.Collections.Generic;

namespace redb.Route.Validation;

/// <summary>
/// Thrown when a route definition fails structural validation before compilation.
/// Contains all detected issues in <see cref="Errors"/>.
/// </summary>
public sealed class RouteValidationException : InvalidOperationException
{
    /// <summary>Route identifier, or null if unnamed.</summary>
    public string? RouteId { get; }

    /// <summary>All validation errors detected.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Creates a route validation exception.</summary>
    /// <param name="routeId">Route identifier.</param>
    /// <param name="errors">List of validation errors.</param>
    public RouteValidationException(string? routeId, IReadOnlyList<string> errors)
        : base($"Route '{routeId ?? "(unnamed)"}' validation failed:\n  - "
               + string.Join("\n  - ", errors))
    {
        RouteId = routeId;
        Errors = errors;
    }
}

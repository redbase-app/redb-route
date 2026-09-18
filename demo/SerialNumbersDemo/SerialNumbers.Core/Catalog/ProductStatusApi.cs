namespace SerialNumbers.Core.Catalog;

/// <summary>
/// The body of <c>PUT /api/products/{gtin}/status</c>, for example
/// <c>{"status":"Active","changedBy":"alice"}</c>.
/// </summary>
public sealed record ProductStatusChange(string Status, string? ChangedBy);

/// <summary>The answer to a status change.</summary>
/// <param name="Outcome">One of <see cref="ProductChangeOutcomes"/>.</param>
/// <param name="RequestsReleased">Requests on hold sent through the request route again; 0 unless the product is active.</param>
public sealed record ProductStatusResult(string Gtin, string Status, string Outcome, int RequestsReleased);

/// <summary>The answer to a refused status change: an unknown product or status.</summary>
public sealed record ApiError(string Error);

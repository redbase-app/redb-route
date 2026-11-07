using redb.Route.Abstractions;

namespace redb.Route.Xml.Packaging;

/// <summary>
/// Reads a route's <c>file=</c> references from the project on disk by
/// <see cref="RoutePackage.ResourceCandidatesFor"/>, the rule the packaging checks and the Tsak loader
/// share: the tool's <c>csharp</c> and <c>mermaid</c> find exactly the files <c>check</c> accepts.
/// </summary>
/// <param name="routeFile">The route file whose references are resolved.</param>
public sealed class RouteProjectResourceResolver(string routeFile) : IRouteResourceResolver
{
    /// <inheritdoc />
    public string? Resolve(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return RoutePackage.ResourceCandidatesFor(routeFile, reference).FirstOrDefault(File.Exists);
    }

    /// <inheritdoc />
    public string DescribeSearch(string reference)
        => string.Join(", ", RoutePackage.ResourceCandidatesFor(routeFile, reference));
}

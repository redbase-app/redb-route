namespace redb.Route.JsonTransform;

/// <summary>
/// A JSONata specification could not be read or parsed. Raised while the route is compiled, so it
/// surfaces from <c>RouteContext.Start()</c> with the specification's name and the engine's message
/// (which carries the position of the error).
/// </summary>
public sealed class JsonTransformCompilationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public JsonTransformCompilationException(string specificationName, string message, Exception? inner = null)
        : base($"JSON transform '{specificationName}': {message}", inner)
    {
        SpecificationName = specificationName;
    }

    /// <summary>Specification name as given in the DSL / XML (file path, resource locator, or <c>&lt;inline&gt;</c>).</summary>
    public string SpecificationName { get; }
}

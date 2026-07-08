namespace redb.Route.Abstractions;

/// <summary>
/// Creates endpoints for a specific URI scheme (e.g., kafka, rabbitmq, redis).
/// Each component handles one scheme and creates configured endpoints.
/// </summary>
public interface IComponent
{
    /// <summary>The URI scheme this component handles (e.g., "kafka", "rabbitmq").</summary>
    string Scheme { get; }

    /// <summary>
    /// Optional alternate scheme aliases (e.g., "es" for "elasticsearch").
    /// <see cref="Abstractions.IRouteContext.AddComponent"/> registers the component under all schemes.
    /// </summary>
    IReadOnlyList<string> AlternateSchemes => [];

    /// <summary>Creates an endpoint from a parsed URI.</summary>
    /// <param name="uri">Parsed endpoint URI with scheme, path, and parameters.</param>
    /// <returns>Configured endpoint ready for producing/consuming.</returns>
    IEndpoint CreateEndpoint(EndpointUri uri);
}

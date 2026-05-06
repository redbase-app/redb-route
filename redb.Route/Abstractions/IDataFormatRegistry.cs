namespace redb.Route.Abstractions;

/// <summary>
/// Registry of content-type → serializer mappings.
/// Used by <see cref="IMessageSerializer"/>-based processors to auto-select
/// the appropriate serializer based on the exchange's ContentType.
/// <para>
/// Two lookup dimensions are supported:
/// </para>
/// <list type="bullet">
///   <item><description><b>Media type</b> — resolves by MIME string
///     (<c>application/json</c>, <c>application/scim+json</c>). Used for wire-format
///     interop; honors RFC 6838 structured-suffix fallbacks (<c>+json</c>/<c>+xml</c>).</description></item>
///   <item><description><b>Profile name</b> — resolves by a caller-chosen alias
///     (e.g. <c>"scim"</c>, <c>"problem"</c>). Used by application code that wants a
///     named preset independent of the wire media type.</description></item>
/// </list>
/// </summary>
public interface IDataFormatRegistry
{
    /// <summary>Finds a serializer for the given content type (e.g. "application/json").</summary>
    /// <param name="contentType">MIME content type, optionally with parameters (charset, etc.).</param>
    /// <returns>Matching serializer, or null if none registered.</returns>
    IMessageSerializer? GetSerializer(string contentType);

    /// <summary>Registers a serializer for the given content type.</summary>
    /// <param name="contentType">MIME content type (e.g. "application/json").</param>
    /// <param name="serializer">Serializer instance.</param>
    void Register(string contentType, IMessageSerializer serializer);

    /// <summary>
    /// Resolves a serializer registered under a named profile (e.g. <c>"scim"</c>,
    /// <c>"problem"</c>, <c>"oauth"</c>). Profiles are separate from media-type lookup —
    /// used by application code that selects a preset by name rather than by wire media type.
    /// </summary>
    /// <param name="profileName">Profile alias.</param>
    /// <returns>Matching serializer, or null if no profile by that name is registered.</returns>
    /// <remarks>
    /// Default implementation returns <c>null</c>; registries that don't care about profiles
    /// remain source-compatible with the original contract.
    /// </remarks>
    IMessageSerializer? ResolveProfile(string profileName) => null;

    /// <summary>
    /// Registers a serializer under a named profile alias.
    /// </summary>
    /// <param name="profileName">Profile alias (e.g. <c>"scim"</c>).</param>
    /// <param name="serializer">Serializer instance.</param>
    /// <remarks>
    /// Default implementation is a no-op, preserving source compatibility with
    /// external <see cref="IDataFormatRegistry"/> implementations.
    /// </remarks>
    void RegisterProfile(string profileName, IMessageSerializer serializer) { }
}

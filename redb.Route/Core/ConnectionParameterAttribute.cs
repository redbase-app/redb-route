namespace redb.Route.Core;

/// <summary>
/// Declares that a named connection factory sets this option: it describes the connection (host, credentials, TLS,
/// timeouts), not the endpoint. A factory is the whole connection, as in Camel ("all connection options set on URI are
/// not used"), so this option written in a URI beside <see cref="ConnectionFactoryReferenceAttribute">the factory
/// reference</see> would be ignored and send the route elsewhere than written; the core refuses it when the options are
/// bound (<see cref="EndpointOptions.WrittenBesideConnectionFactory"/>). Tooling reads the declaration by reflection.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class ConnectionParameterAttribute : Attribute;

/// <summary>
/// Marks the option that names a registered connection factory (usually <c>connectionFactory</c>). An options type
/// that declares <see cref="ConnectionParameterAttribute"/> options declares exactly one of these: the core does not
/// guess which option names the factory. It is a <see cref="RegistryReferenceAttribute"/> too: the value names a
/// registry object.
/// </summary>
/// <param name="expected">The type the registry object must have, or <c>null</c> when the option does not say.</param>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class ConnectionFactoryReferenceAttribute(Type? expected = null) : RegistryReferenceAttribute(expected);

/// <summary>
/// Declares that the option's value names an object in the context registry (<c>connectionFactory=main</c>,
/// <c>headerFilterStrategy=#filter</c>; the <c>#</c> is optional, the lookup strips it). The engine resolves the name
/// where the endpoint is built; tooling reads the declaration by reflection: the component catalog writes it as the
/// option's <c>reference</c>, and the package gate holds the name against the beans the package declares.
/// </summary>
/// <param name="expected">The type the registry object must have, or <c>null</c> when the option does not say.</param>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public class RegistryReferenceAttribute(Type? expected = null) : Attribute
{
    /// <summary>The type the registry object must have, or <c>null</c> when the option does not say.</summary>
    public Type? Expected { get; } = expected;
}

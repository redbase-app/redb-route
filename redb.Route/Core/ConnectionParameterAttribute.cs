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
/// guess which option names the factory.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class ConnectionFactoryReferenceAttribute : Attribute;

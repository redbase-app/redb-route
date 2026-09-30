namespace redb.Route.Core;

/// <summary>
/// Declares that an endpoint options class takes URI parameters it has no property for, and reads
/// them itself from <see cref="EndpointOptions.UnmappedParameters"/> (<c>http:</c> and <c>sql:</c>
/// pass <c>param.*</c> on, <c>bean:</c> sets them on the bean). Without it an unknown parameter is
/// refused when the endpoint is created: a misspelt option name would otherwise be dropped without a
/// word and the option would keep its default.
/// <para>
/// The counterpart of Apache Camel's <c>Component.isLenientProperties()</c>, which is also
/// <c>false</c> unless a component says otherwise. It is an attribute on the options type, not a
/// property of an endpoint instance, so tooling (the Route-XML catalog, the XSD, <c>check</c>) reads
/// it by reflection without creating an endpoint — see <see cref="EndpointOptions.IsLenient"/>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class LenientPropertiesAttribute : Attribute;

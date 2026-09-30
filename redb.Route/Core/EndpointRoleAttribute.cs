namespace redb.Route.Core;

/// <summary>The side of an endpoint that reads an option.</summary>
public enum EndpointRole
{
    /// <summary>Read only by the consumer (<c>from(...)</c>).</summary>
    Consumer,

    /// <summary>Read only by the producer (<c>to(...)</c>).</summary>
    Producer,
}

/// <summary>
/// Declares that only one side of an endpoint reads this option — Camel's <c>@UriParam(label = "consumer")</c> /
/// <c>label = "producer"</c>. An options class shared by producer and consumer otherwise gives no sign that an option
/// written on the other side does nothing: <c>username</c> on an http: consumer looked like protection and checked
/// nobody. The connector refuses such an option by this declaration
/// (<see cref="EndpointOptions.WrittenForOtherRole"/>), and tooling — the Route-XML catalog, the editor schema,
/// <c>check</c> — reads the same declaration by reflection. An option both sides read carries no role.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class EndpointRoleAttribute(EndpointRole role) : Attribute
{
    /// <summary>The side that reads the option.</summary>
    public EndpointRole Role { get; } = role;
}

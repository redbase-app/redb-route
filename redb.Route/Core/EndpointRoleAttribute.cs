namespace redb.Route.Core;

/// <summary>The side of an endpoint that reads an option, or the only side a component has.</summary>
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
/// <para>
/// On a component class it declares the only side the component has: Apache Camel's <c>producerOnly</c> /
/// <c>consumerOnly</c>. <c>[EndpointRole(EndpointRole.Producer)]</c> on <c>smtp</c> means a route cannot start from it;
/// the context refuses such a route, and the catalog, the editor schema and <c>check</c> read the same declaration
/// (<see cref="EndpointSides"/>). A component with both sides carries none.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class EndpointRoleAttribute(EndpointRole role) : Attribute
{
    /// <summary>The side that reads the option, or the only side of the component.</summary>
    public EndpointRole Role { get; } = role;

    /// <summary>
    /// On a component: what to use instead, appended to the refusal of the missing side
    /// (<c>"Use imap: or pop3: to receive mail."</c>). Not read on an option.
    /// </summary>
    public string? Hint { get; init; }
}

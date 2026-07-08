namespace redb.Route.Controllers.Attributes;

/// <summary>Binds a parameter from the exchange message body (exchange.In.Body). JSON-deserialized.</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class FromBodyAttribute : Attribute { }

/// <summary>Binds a parameter from a message header (exchange.In.Headers[name]).</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class FromHeaderAttribute : Attribute
{
    /// <summary>Header key name.</summary>
    public string Name { get; }

    /// <param name="name">Header key name.</param>
    public FromHeaderAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }
}

/// <summary>Binds a parameter from an exchange property (exchange.Properties[name]).</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class FromPropertyAttribute : Attribute
{
    /// <summary>Property key name.</summary>
    public string Name { get; }

    /// <param name="name">Property key name.</param>
    public FromPropertyAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }
}

/// <summary>Binds a parameter from a query string value (exchange.In.Headers["query.{name}"]).</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class FromQueryAttribute : Attribute
{
    /// <summary>Query parameter name.</summary>
    public string Name { get; }

    /// <param name="name">Query parameter name.</param>
    public FromQueryAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }
}

/// <summary>Binds a parameter from a route path segment (extracted from template matching, e.g. {id}).</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class FromRouteAttribute : Attribute
{
    /// <summary>Route parameter name (must match a {param} in the route template).</summary>
    public string Name { get; }

    /// <param name="name">Route parameter name.</param>
    public FromRouteAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }
}

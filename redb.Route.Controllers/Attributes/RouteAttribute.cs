namespace redb.Route.Controllers.Attributes;

/// <summary>
/// Specifies the base route path for a controller.
/// Applied to classes inheriting <see cref="RedbController"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RouteAttribute : Attribute
{
    /// <summary>Route path template (e.g. "modules", "contexts").</summary>
    public string Template { get; }

    /// <param name="template">Route path template.</param>
    public RouteAttribute(string template)
    {
        Template = template ?? throw new ArgumentNullException(nameof(template));
    }
}

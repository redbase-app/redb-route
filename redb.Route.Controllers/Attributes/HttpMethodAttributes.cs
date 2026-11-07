namespace redb.Route.Controllers.Attributes;

/// <summary>HTTP method type for route matching.</summary>
public enum HttpMethodType
{
    /// <summary>GET request.</summary>
    Get,
    /// <summary>POST request.</summary>
    Post,
    /// <summary>PUT request.</summary>
    Put,
    /// <summary>DELETE request.</summary>
    Delete,
    /// <summary>PATCH request.</summary>
    Patch
}

/// <summary>
/// Base attribute for HTTP method markers on controller actions.
/// These are route-table markers, not transport directives.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public abstract class HttpMethodAttribute : Attribute
{
    /// <summary>HTTP method this action responds to.</summary>
    public HttpMethodType Method { get; }

    /// <summary>Optional sub-template (e.g. "{id}").</summary>
    public string? Template { get; }

    /// <param name="method">HTTP method type.</param>
    /// <param name="template">Optional sub-template appended to the controller route.</param>
    protected HttpMethodAttribute(HttpMethodType method, string? template = null)
    {
        Method = method;
        Template = template;
    }
}

/// <summary>Marks a controller action as handling GET requests.</summary>
public sealed class HttpGetAttribute : HttpMethodAttribute
{
    /// <param name="template">Optional sub-template (e.g. "{id}").</param>
    public HttpGetAttribute(string? template = null) : base(HttpMethodType.Get, template) { }
}

/// <summary>Marks a controller action as handling POST requests.</summary>
public sealed class HttpPostAttribute : HttpMethodAttribute
{
    /// <param name="template">Optional sub-template (e.g. "search").</param>
    public HttpPostAttribute(string? template = null) : base(HttpMethodType.Post, template) { }
}

/// <summary>Marks a controller action as handling PUT requests.</summary>
public sealed class HttpPutAttribute : HttpMethodAttribute
{
    /// <param name="template">Optional sub-template (e.g. "{id}").</param>
    public HttpPutAttribute(string? template = null) : base(HttpMethodType.Put, template) { }
}

/// <summary>Marks a controller action as handling DELETE requests.</summary>
public sealed class HttpDeleteAttribute : HttpMethodAttribute
{
    /// <param name="template">Optional sub-template (e.g. "{id}").</param>
    public HttpDeleteAttribute(string? template = null) : base(HttpMethodType.Delete, template) { }
}

/// <summary>Marks a controller action as handling PATCH requests.</summary>
public sealed class HttpPatchAttribute : HttpMethodAttribute
{
    /// <param name="template">Optional sub-template (e.g. "{id}").</param>
    public HttpPatchAttribute(string? template = null) : base(HttpMethodType.Patch, template) { }
}

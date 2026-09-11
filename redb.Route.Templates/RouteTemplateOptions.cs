namespace redb.Route.Templates;

/// <summary>
/// Package-wide settings, registered with <c>services.AddRouteTemplates(...)</c> or
/// <c>context.UseTemplates(...)</c>; defaults apply when neither was called.
/// </summary>
public sealed class RouteTemplateOptions
{
    /// <summary>Directory relative template paths are resolved against. Default: <see cref="AppContext.BaseDirectory"/>.</summary>
    public string BaseDirectory { get; set; } = AppContext.BaseDirectory;

    /// <summary>Parse templates with Scriban's Liquid-compatible syntax instead of the native <c>{{ }}</c> syntax. Default: <c>false</c>.</summary>
    public bool Liquid { get; set; }

    /// <summary>Fail a render when the template reads a variable that does not exist (Scriban <c>StrictVariables</c>). Default: <c>false</c> — a missing value renders empty.</summary>
    public bool StrictVariables { get; set; }

    /// <summary>Upper bound on loop iterations per render, a guard against a runaway template. Default: 10 000.</summary>
    public int LoopLimit { get; set; } = 10_000;
}

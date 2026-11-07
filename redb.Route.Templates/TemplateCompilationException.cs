namespace redb.Route.Templates;

/// <summary>
/// A template could not be read or parsed. Raised while the route is compiled, so it surfaces from
/// <c>RouteContext.Start()</c> with the template name and the first error's line and column.
/// </summary>
public sealed class TemplateCompilationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TemplateCompilationException(string templateName, int? line, int? column, string message, Exception? inner = null)
        : base(Format(templateName, line, column, message), inner)
    {
        TemplateName = templateName;
        Line = line;
        Column = column;
    }

    /// <summary>Template name as given in the DSL / XML (file path, resource locator, or <c>&lt;inline&gt;</c>).</summary>
    public string TemplateName { get; }

    /// <summary>1-based line of the first error, when known.</summary>
    public int? Line { get; }

    /// <summary>1-based column of the first error, when known.</summary>
    public int? Column { get; }

    private static string Format(string name, int? line, int? column, string message)
        => line is null
            ? $"Template '{name}': {message}"
            : $"Template '{name}' ({line},{column}): {message}";
}

namespace redb.Route.JsonTransform;

/// <summary>What <c>TransformJson</c> leaves in the body.</summary>
public enum JsonTransformOutput
{
    /// <summary>JSON text (<c>ContentType</c> = <c>application/json</c>). Default.</summary>
    String,

    /// <summary>A <c>System.Text.Json.Nodes.JsonNode</c> tree, for further steps that navigate the result.</summary>
    Node,
}

/// <summary>
/// Package-wide settings, registered with <c>services.AddJsonTransform(...)</c> or
/// <c>context.UseJsonTransform(...)</c>; defaults apply when neither was called.
/// </summary>
public sealed class JsonTransformOptions
{
    /// <summary>Directory relative specification paths are resolved against. Default <see cref="AppContext.BaseDirectory"/>.</summary>
    public string BaseDirectory { get; set; } = AppContext.BaseDirectory;

    /// <summary>Indent the JSON text output. Default <c>false</c>.</summary>
    public bool Indent { get; set; }
}

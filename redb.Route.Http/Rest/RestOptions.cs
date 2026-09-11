namespace redb.Route.Http.Rest;

/// <summary>How request and response bodies are bound to CLR types by the REST DSL.</summary>
public enum RestBindingMode
{
    /// <summary>Bodies stay as the HTTP consumer delivers them (<c>byte[]</c> in, whatever the route leaves out).</summary>
    Off,

    /// <summary>A verb with <c>Type&lt;T&gt;()</c> gets its request body unmarshalled from JSON to <c>T</c>; a non-text response body is marshalled to JSON.</summary>
    Json,
}

/// <summary>
/// Settings of one <c>Rest(...)</c> declaration: where the routes listen, default binding and media
/// types, and the OpenAPI document.
/// </summary>
public sealed class RestOptions
{
    /// <summary>Bind host of the shared HTTP server. Default <c>0.0.0.0</c>.</summary>
    public string Host { get; set; } = "0.0.0.0";

    /// <summary>Bind port. Default <c>8080</c>. Several <c>Rest(...)</c> declarations may share a port.</summary>
    public int Port { get; set; } = 8080;

    /// <summary>Default binding mode of the verbs; a verb can override it. Default <see cref="RestBindingMode.Off"/>.</summary>
    public RestBindingMode BindingMode { get; set; } = RestBindingMode.Off;

    /// <summary>Default request media type expected by the verbs (<c>application/json</c>); a verb can override it.</summary>
    public string? Consumes { get; set; }

    /// <summary>Default response media type of the verbs; a verb can override it.</summary>
    public string? Produces { get; set; }

    /// <summary>Serve the generated OpenAPI 3.0 document. Default <c>true</c>.</summary>
    public bool OpenApi { get; set; } = true;

    /// <summary>Path of the OpenAPI document on the same host and port; <c>null</c> = <c>{basePath}/openapi.json</c>, so several declarations on one port do not collide.</summary>
    public string? OpenApiPath { get; set; }

    /// <summary>OpenAPI <c>info.title</c>.</summary>
    public string Title { get; set; } = "redb.Route API";

    /// <summary>OpenAPI <c>info.version</c>.</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>Extra query parameters appended to every generated consumer URI (<c>"ssl=true&amp;sslCertPath=..."</c>), for options the DSL does not model.</summary>
    public string? ExtraConsumerOptions { get; set; }
}

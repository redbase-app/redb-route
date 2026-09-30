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

    /// <summary>
    /// Validate client requests before the route runs, like Camel's <c>clientRequestValidation</c>:
    /// an <c>Accept</c> that excludes the produced type is answered with 406, a missing required or
    /// non-convertible declared parameter with 400. A verb can override it. Default <c>false</c>:
    /// parameters declared for the OpenAPI document alone never start refusing requests.
    /// The 415 check of <c>Consumes</c> runs either way.
    /// </summary>
    public bool ClientRequestValidation { get; set; }

    /// <summary>
    /// Registry name of an <see cref="Abstractions.IProcessor"/> that writes the body of a refused
    /// request (415, 406, 400), <c>#name</c> or <c>name</c>. It finds the code, reason and parameter in
    /// <see cref="RestErrorProperties"/>. A name that is not registered fails when the routes start.
    /// <c>null</c> = the reason as <c>text/plain</c>.
    /// </summary>
    public string? ErrorHandler { get; set; }

    /// <summary>
    /// How the callers of every route of this declaration prove who they are (the http: consumer's <c>inboundAuth</c>):
    /// <c>Basic</c> with <see cref="InboundUsername"/> / <see cref="InboundPassword"/>, <c>Bearer</c> with the registered
    /// validator named by <see cref="TokenValidator"/>. A refused request gets 401 before the route runs. The OpenAPI
    /// document is behind the same check. Default <c>None</c>.
    /// </summary>
    public HttpAuthScheme InboundAuth { get; set; } = HttpAuthScheme.None;

    /// <summary>User name <c>InboundAuth=Basic</c> accepts.</summary>
    public string? InboundUsername { get; set; }

    /// <summary>Password <c>InboundAuth=Basic</c> accepts; take it from configuration (<c>{{…}}</c>).</summary>
    public string? InboundPassword { get; set; }

    /// <summary>Realm named in <c>WWW-Authenticate</c>; <c>null</c> = the consumer's default.</summary>
    public string? InboundRealm { get; set; }

    /// <summary>Registry name of the <see cref="IHttpTokenValidator"/> for <c>InboundAuth=Bearer</c>.</summary>
    public string? TokenValidator { get; set; }

    /// <summary>Extra query parameters appended to every generated consumer URI (<c>"ssl=true&amp;sslCertPath=..."</c>), for options the DSL does not model.</summary>
    public string? ExtraConsumerOptions { get; set; }
}

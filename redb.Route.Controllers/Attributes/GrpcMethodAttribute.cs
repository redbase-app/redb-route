namespace redb.Route.Controllers.Attributes;

/// <summary>
/// Maps a controller method to the name gRPC callers dispatch on (<c>dispatch-method</c>). Optional:
/// without it the C# method name is the wire name, which means renaming the method breaks every client.
/// A gRPC facade publishes its contract outside the codebase, so pin the name here and refactor freely.
/// <para>Mirrors <see cref="SoapOperationAttribute"/>, which does the same for SOAP operations.</para>
/// <example><code>
/// [GrpcMethod("ListUsers")]
/// public Task&lt;UserPage&gt; List(int page) =&gt; ...;
/// </code></example>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class GrpcMethodAttribute : Attribute
{
    /// <param name="method">The gRPC dispatch name.</param>
    public GrpcMethodAttribute(string method) => Method = method;

    /// <summary>The gRPC dispatch name.</summary>
    public string Method { get; }
}

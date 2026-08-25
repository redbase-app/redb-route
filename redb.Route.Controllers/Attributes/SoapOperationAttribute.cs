namespace redb.Route.Controllers.Attributes;

/// <summary>
/// Maps a controller method to a SOAP operation — the local name of the <c>&lt;soap:Body&gt;</c> root element
/// the SOAP consumer surfaces on <c>redbSoap.operation</c>. Optional: without it the method name is the
/// operation. Use it when the request element name differs from the method name.
/// <example><code>
/// [SoapOperation("GetFares")]
/// public Task&lt;GetFaresResponse&gt; Fares([FromBody] GetFaresRequest req) =&gt; ...;
/// </code></example>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SoapOperationAttribute : Attribute
{
    /// <param name="operation">The SOAP operation name (the request Body element's local name).</param>
    public SoapOperationAttribute(string operation) => Operation = operation;

    /// <summary>The SOAP operation name.</summary>
    public string Operation { get; }
}

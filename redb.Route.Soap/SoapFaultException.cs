namespace redb.Route.Soap;

/// <summary>
/// Thrown when a SOAP call returns a <c>soap:Fault</c>. The fault code/string are also placed on the
/// exchange under <c>redbSoap.faultCode</c> / <c>redbSoap.faultString</c> so <c>OnException</c> handlers
/// (or a route that catches this) can react.
/// </summary>
public sealed class SoapFaultException : Exception
{
    /// <summary>SOAP fault code (1.1 <c>faultcode</c> / 1.2 <c>Code/Value</c>).</summary>
    public string? FaultCode { get; }

    /// <summary>SOAP fault string (1.1 <c>faultstring</c> / 1.2 <c>Reason/Text</c>).</summary>
    public string? FaultString { get; }

    /// <summary>
    /// Namespace the prefix in <see cref="FaultCode"/> stands for, when the code uses one the connector
    /// does not already know. A fault code is a QName, so an unbound prefix is not something a strict
    /// client can resolve — and the clients that branch on fault codes are the strict ones.
    /// </summary>
    public string? FaultCodeNamespace { get; }

    /// <summary>Creates a SOAP fault exception.</summary>
    /// <param name="faultCodeNamespace">
    /// Optional namespace for the prefix in <paramref name="faultCode"/>. The standard prefixes
    /// (<c>wst</c>, <c>wsse</c>, <c>wsu</c>, <c>wsa</c>, …) are known and need not be given.
    /// </param>
    public SoapFaultException(string? faultCode, string? faultString, string? faultCodeNamespace = null)
        : base($"SOAP fault: {faultCode} — {faultString}")
    {
        FaultCode = faultCode;
        FaultString = faultString;
        FaultCodeNamespace = faultCodeNamespace;
    }
}

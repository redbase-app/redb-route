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

    /// <summary>Creates a SOAP fault exception.</summary>
    public SoapFaultException(string? faultCode, string? faultString)
        : base($"SOAP fault: {faultCode} — {faultString}")
    {
        FaultCode = faultCode;
        FaultString = faultString;
    }
}

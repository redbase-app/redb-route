namespace redb.Route.Abstractions;

/// <summary>
/// The incoming request could not be bound to what the receiver expects — an unparsable body, a
/// parameter of the wrong shape. It is the CALLER's error, and transports that catch it answer
/// with their "your request is wrong" form: the SOAP consumer writes a <c>Sender</c>/<c>Client</c>
/// fault carrying this message instead of a <c>Receiver</c> fault with a generic text.
/// </summary>
/// <remarks>
/// The message deliberately describes the caller's own bytes (the missing member, the token that
/// did not parse) — returning it is the BR-4 rule, not a violation of it: unlike an action
/// failure, nothing in it is ours to hide. This type lives in the core so that a dispatcher
/// (redb.Route.Controllers) can signal it without referencing any transport package, and a
/// transport (redb.Route.Soap) can recognise it without referencing the dispatchers.
/// </remarks>
public sealed class MalformedRequestException : Exception
{
    /// <summary>Creates the exception with a caller-facing description of what did not bind.</summary>
    public MalformedRequestException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

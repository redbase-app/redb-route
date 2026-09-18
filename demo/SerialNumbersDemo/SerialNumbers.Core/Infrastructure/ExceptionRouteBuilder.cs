using Microsoft.Extensions.Logging;
using redb.Route.Core;
using redb.Route.RedbCore.Extensions;
using SerialNumbers.Core.Services;

namespace SerialNumbers.Core.Infrastructure;

/// <summary>
/// Exception handlers for the whole context: an <c>OnException</c> declared in any route builder
/// wraps every route of the module.
/// <para>
/// Only for a failure whose right outcome is "take the message and record it": a handled
/// exception is a success for the consumer, so the SFTP file moves to <c>.done</c> and an AS2
/// sender gets a positive MDN. A catch-all <c>OnException&lt;Exception&gt;().Handled(true)</c>
/// is right for an HTTP API, where it turns any error into an error response, and wrong for
/// file and message consumers, where it would acknowledge a message the database never saw.
/// Technical failures are left unhandled on purpose: the transaction rolls back and the file
/// stays with the partner for the next poll.
/// </para>
/// </summary>
public sealed class ExceptionRouteBuilder : RouteBuilder
{
    protected override void Configure()
    {
        OnException<UnsupportedMessageTypeException>()
            .Handled(true)
            .ProcessWithRedb(IntakeRecorder.RecordParkedAsync)
            .Log("${header.serials.partner}: ${header.serials.fileName} parked, " +
                 "no route for message type ${header.serials.messageType}", LogLevel.Warning)
            // The steps the message went through before it was parked, with their timings. Collected by
            // .MessageHistory() on the intake and request routes; without a step that prints it the trace
            // stays inside the exchange.
            .Log(MessageHistory.Format, LogLevel.Warning);
    }
}

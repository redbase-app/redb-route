using RabbitMQ.Client.Exceptions;

namespace redb.Route.RabbitMQ;

/// <summary>
/// A mandatory publish came back from the broker (<c>basic.return</c>): no queue takes it for its exchange and routing
/// key, so it was not delivered. Every publish path throws it — an immediate send, a request-reply call and the sends
/// of a transacted block — so one <c>OnException&lt;RabbitMQUnroutableException&gt;</c> covers all of them.
/// </summary>
public sealed class RabbitMQUnroutableException : Exception
{
    /// <summary>Creates the exception for a returned publish.</summary>
    public RabbitMQUnroutableException(
        string message, string exchange, string routingKey, ushort replyCode, string replyText, Exception? innerException = null)
        : base(message, innerException)
    {
        Exchange = exchange;
        RoutingKey = routingKey;
        ReplyCode = replyCode;
        ReplyText = replyText;
    }

    /// <summary>The exchange the message was published to; empty for the default exchange.</summary>
    public string Exchange { get; }

    /// <summary>The routing key no queue is bound for.</summary>
    public string RoutingKey { get; }

    /// <summary>The broker's reply code, <c>312</c> (NO_ROUTE) for an unroutable message.</summary>
    public ushort ReplyCode { get; }

    /// <summary>The broker's reply text, such as <c>NO_ROUTE</c>.</summary>
    public string ReplyText { get; }

    /// <summary>Wraps the client's exception for a publish the broker returned.</summary>
    internal static RabbitMQUnroutableException From(PublishReturnException returned) => new(
        $"RabbitMQ: the message came back unroutable and was not delivered: no queue takes it for " +
        $"exchange='{returned.Exchange}', routingKey='{returned.RoutingKey}' ({returned.ReplyCode} {returned.ReplyText}).",
        returned.Exchange, returned.RoutingKey, returned.ReplyCode, returned.ReplyText, returned);
}

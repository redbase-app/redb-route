namespace redb.Route.Abstractions;

/// <summary>
/// Decides which headers a transport carries between an exchange and the messages of its protocol: the HTTP request
/// and response headers, the headers of a Kafka record, the application properties of an AMQP or Service Bus message.
/// Apache Camel's <c>HeaderFilterStrategy</c>.
/// <para>
/// Two directions, asked about every header one by one:
/// <list type="bullet">
/// <item><see cref="FilterOutgoing"/> — the exchange's header is about to go into an outgoing message: the request a
/// producer sends, the reply a consumer sends back (Camel: <c>applyFilterToCamelHeaders</c>);</item>
/// <item><see cref="FilterIncoming"/> — a header of an incoming message is about to come into the exchange: the
/// message a consumer receives, the reply a producer gets (Camel: <c>applyFilterToExternalHeaders</c>).</item>
/// </list>
/// Both return <c>true</c> to <b>drop</b> the header and <c>false</c> to let it through, as in Camel.
/// </para>
/// <para>
/// Every transport that carries arbitrary headers has a default strategy that keeps its documented behaviour; a
/// strategy set on an endpoint (<c>headerFilterStrategy=#name</c>) replaces that default entirely. A strategy decides
/// about the headers of the route only: the fields a transport sets itself (trace context, content type, the native
/// properties of the message) do not go through it. <see cref="DefaultHeaderFilterStrategy"/> covers the usual
/// cases — a list of names, a regular expression, name prefixes — as a blacklist or a whitelist.
/// </para>
/// </summary>
public interface IHeaderFilterStrategy
{
    /// <summary>
    /// Whether the exchange header <paramref name="name"/> is <b>dropped</b> instead of going into an outgoing message
    /// (a producer's request, a consumer's reply).
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <param name="value">The header value; may be <c>null</c>.</param>
    /// <param name="exchange">The exchange the header belongs to.</param>
    /// <returns><c>true</c> to drop the header; <c>false</c> to send it.</returns>
    bool FilterOutgoing(string name, object? value, IExchange exchange);

    /// <summary>
    /// Whether the header <paramref name="name"/> of an incoming message (a consumed message, a producer's reply) is
    /// <b>dropped</b> instead of coming into the exchange.
    /// </summary>
    /// <param name="name">The header name as the protocol carries it.</param>
    /// <param name="value">The header value; may be <c>null</c>.</param>
    /// <param name="exchange">The exchange the header would come into.</param>
    /// <returns><c>true</c> to drop the header; <c>false</c> to take it.</returns>
    bool FilterIncoming(string name, object? value, IExchange exchange);
}

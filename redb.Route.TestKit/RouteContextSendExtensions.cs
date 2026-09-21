using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;

namespace redb.Route.TestKit;

/// <summary>
/// One-line sends for tests: replaces the <c>GetEndpoint / CreateProducer / Start / Process</c>
/// plumbing with <see cref="SendBody"/>, <see cref="SendBodyAndHeaders"/> and <see cref="RequestBody{T}"/>,
/// plus <see cref="Mock"/> to reach the mock standing in for an endpoint.
/// Each call uses a short-lived <see cref="ProducerTemplate"/>, so producers are started and stopped
/// around the send and nothing leaks between tests.
/// </summary>
public static class RouteContextSendExtensions
{
    /// <summary>Sends <paramref name="body"/> to <paramref name="endpointUri"/> (in-only).</summary>
    public static async Task SendBody(this IRouteContext context, string endpointUri, object? body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var template = Template(context);
        var exchange = Exchange.Create(new Message(body), null);
        try { await template.SendAsync(endpointUri, exchange, ct).ConfigureAwait(false); }
        finally { await exchange.DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>Sends <paramref name="body"/> with the given headers to <paramref name="endpointUri"/> (in-only).</summary>
    public static async Task SendBodyAndHeaders(this IRouteContext context, string endpointUri, object? body,
        IEnumerable<KeyValuePair<string, object?>> headers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(headers);
        using var template = Template(context);
        var exchange = Exchange.Create(WithHeaders(body, headers), null);
        try { await template.SendAsync(endpointUri, exchange, ct).ConfigureAwait(false); }
        finally { await exchange.DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>Sends <paramref name="body"/> with a single header to <paramref name="endpointUri"/> (in-only).</summary>
    public static Task SendBodyAndHeader(this IRouteContext context, string endpointUri, object? body,
        string headerName, object? headerValue, CancellationToken ct = default)
        => context.SendBodyAndHeaders(endpointUri, body, [new KeyValuePair<string, object?>(headerName, headerValue)], ct);

    /// <summary>Request-reply: sends <paramref name="body"/> and returns the reply body converted to <typeparamref name="T"/>.</summary>
    public static async Task<T?> RequestBody<T>(this IRouteContext context, string endpointUri, object? body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var template = Template(context);
        var exchange = Exchange.Create(new Message(body), null);
        try
        {
            var reply = await template.RequestAsync(endpointUri, exchange, ct).ConfigureAwait(false);
            return ReplyAs<T>(reply, endpointUri);
        }
        finally { await exchange.DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>Request-reply with headers; returns the reply body converted to <typeparamref name="T"/>.</summary>
    public static async Task<T?> RequestBodyAndHeaders<T>(this IRouteContext context, string endpointUri, object? body,
        IEnumerable<KeyValuePair<string, object?>> headers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(headers);
        using var template = Template(context);
        var exchange = Exchange.Create(WithHeaders(body, headers), null);
        try
        {
            var reply = await template.RequestAsync(endpointUri, exchange, ct).ConfigureAwait(false);
            return ReplyAs<T>(reply, endpointUri);
        }
        finally { await exchange.DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>
    /// The <see cref="MockEndpoint"/> standing in for <paramref name="uri"/>: either a <c>mock://name</c>
    /// URI or the original endpoint URI that <see cref="AdviceWithBuilder.MockEndpoints"/> replaced
    /// (<c>kafka://orders-vip</c> resolves to <c>mock://kafka:orders-vip</c>, see <see cref="MockUri.For"/>).
    /// The endpoint is created on first access, so expectations can be set before any message arrives.
    /// </summary>
    public static MockEndpoint Mock(this IRouteContext context, string uri)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.GetEndpoint(MockUri.For(uri));
        return endpoint as MockEndpoint
            ?? throw new InvalidOperationException($"'{uri}' resolved to '{endpoint.GetType().Name}', not a MockEndpoint.");
    }

    private static ProducerTemplate Template(IRouteContext context)
    {
        var template = new ProducerTemplate(context);
        template.Start();
        return template;
    }

    private static Message WithHeaders(object? body, IEnumerable<KeyValuePair<string, object?>> headers)
    {
        var message = new Message(body);
        foreach (var (key, value) in headers)
            message.Headers[key] = value;
        return message;
    }

    /// <summary>
    /// The reply body converted to <typeparamref name="T"/>, taken before the helper ends the exchange. A body that reads
    /// from resources of the exchange (<see cref="IExchangeBoundBody"/>) would be dead once the helper returns, so it is
    /// refused; a nullable <typeparamref name="T"/> converts to its underlying type.
    /// </summary>
    private static T? ReplyAs<T>(IExchange reply, string endpointUri)
    {
        var replyBody = reply.Out?.Body ?? reply.In.Body;
        if (replyBody is IExchangeBoundBody)
            throw new InvalidOperationException(
                $"The reply of '{EndpointUri.Sanitize(endpointUri)}' is a {ReadableName(replyBody.GetType())} that reads from " +
                "resources of its exchange, and the helper ends the exchange before it returns. Use " +
                "ProducerTemplate.RequestAsync, read the body, then dispose the exchange.");

        return replyBody switch
        {
            null => default,
            T typed => typed,
            _ => (T)Convert.ChangeType(replyBody, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T),
                System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary><c>StreamedQueryResult&lt;Order&gt;</c> rather than the runtime name <c>StreamedQueryResult`1</c>.</summary>
    private static string ReadableName(Type type) =>
        type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(ReadableName))}>"
            : type.Name;
}

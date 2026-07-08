using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Telegram;

/// <summary>
/// Telegram Bot transport component for redb.Route.
/// Scheme: <c>telegram</c>.
/// <para>
/// URI format:<br/>
/// Consumer: <c>telegram://receive?token=TOKEN&amp;timeoutSeconds=30</c><br/>
/// Producer: <c>telegram://send?token=TOKEN&amp;chatId=123456&amp;parseMode=HTML</c><br/>
/// Document: <c>telegram://document?token=TOKEN&amp;chatId=123456&amp;fileName=report.pdf</c>
/// </para>
/// <para>
/// Owns a per-token registry of <see cref="TelegramBotClient"/> instances with a
/// dedicated <see cref="HttpClient"/> per token. All consumers and producers
/// targeting the same token share the same client — required by Telegram Bot API
/// which allows only one <c>getUpdates</c> stream per token (HTTP 409 otherwise).
/// The component disposes all owned <see cref="HttpClient"/> instances on
/// <see cref="DisposeAsync"/>.
/// </para>
/// </summary>
public sealed class TelegramComponent : ComponentBase
{
    private readonly ConcurrentDictionary<string, ClientEntry> _clients =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, byte> _consumerTokens =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public override string Scheme => "telegram";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new TelegramEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new TelegramEndpoint(uri, this, options);
    }

    /// <summary>
    /// Returns the shared <see cref="TelegramBotClient"/> for <paramref name="rawToken"/>,
    /// creating it on first use. <c>${env:VAR}</c> expressions in the token are
    /// resolved via <see cref="TelegramEndpointOptions.ResolveTokenExpression"/>.
    /// </summary>
    public TelegramBotClient GetOrCreateClient(string rawToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(rawToken);
        var resolved = TelegramEndpointOptions.ResolveTokenExpression(rawToken);

        var entry = _clients.GetOrAdd(resolved, static token =>
        {
            // Own the HttpClient so we can dispose it on component shutdown.
            // PooledConnectionLifetime mirrors Telegram.Bot's own default when
            // no HttpClient is supplied.
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(3)
            };
            var http = new HttpClient(handler)
            {
                // Must exceed the maximum long-polling duration (50s) plus a margin.
                Timeout = TimeSpan.FromSeconds(75)
            };
            var bot = new TelegramBotClient(token, http);
            return new ClientEntry(bot, http);
        });

        return entry.Bot;
    }

    /// <summary>
    /// Reserves consumer ownership for <paramref name="rawToken"/> within this
    /// component. Throws when a second consumer attempts to register the same
    /// token — Telegram Bot API only permits a single <c>getUpdates</c> stream
    /// per token, so a duplicate consumer would silently fail with HTTP 409.
    /// </summary>
    public void RegisterConsumer(string rawToken)
    {
        var resolved = TelegramEndpointOptions.ResolveTokenExpression(rawToken);
        if (!_consumerTokens.TryAdd(resolved, 0))
            throw new InvalidOperationException(
                "Telegram: only one consumer per bot token is allowed within a RouteContext. " +
                "The Bot API returns HTTP 409 Conflict when two getUpdates streams run for the same token.");
    }

    /// <summary>Releases the consumer slot reserved by <see cref="RegisterConsumer"/>.</summary>
    public void UnregisterConsumer(string rawToken)
    {
        var resolved = TelegramEndpointOptions.ResolveTokenExpression(rawToken);
        _consumerTokens.TryRemove(resolved, out _);
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        foreach (var kv in _clients)
        {
            try { kv.Value.Http.Dispose(); }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Telegram: error disposing HttpClient for token");
            }
        }
        _clients.Clear();
        _consumerTokens.Clear();
        return base.DisposeAsync();
    }

    private readonly record struct ClientEntry(TelegramBotClient Bot, HttpClient Http);
}

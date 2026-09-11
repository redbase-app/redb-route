using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using redb.Route.Abstractions;
using redb.Route.Extensions;
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

        // Resolve the named ConnectionFactory BEFORE Validate(): the factory may be the only
        // source of the bot token, and Validate() requires one. This is what lets a route
        // reference `connectionFactory=my-bot` and keep the token out of the URI entirely.
        if (!string.IsNullOrEmpty(options.ConnectionFactory))
        {
            // A set-but-unknown name fails loud -- never a silent fallback to URI params (Ф11 Ж-1).
            var factory = Context.GetRequiredFromRegistry<TelegramConnectionFactory>(options.ConnectionFactory);
            factory.ApplyTo(options, uri);
        }

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
            var http = BuildHttpClient();
            var bot = new TelegramBotClient(token, http);
            return new ClientEntry(bot, http);
        });

        return entry.Bot;
    }

    /// <summary>
    /// The shared HTTP transport of one bot: long polling and every producer send and download go
    /// through it. A getUpdates long poll is silent for up to ~50 s (Telegram caps the wait there);
    /// middleboxes (VPN tunnels, NAT, proxies) drop TLS connections that stay silent for about that
    /// long, which showed up as "The response ended prematurely" on nearly every poll through a
    /// sing-tun/xray tunnel (2026-09-04) — a retry storm in the log, and a message arriving in the
    /// gap waits for the next poll. HTTP/2 with PING frames while a request is in flight keeps the
    /// connection visibly alive (four consecutive 50-second polls completed with a ping every 15 s;
    /// without pings HTTP/1.1 lost every one). api.telegram.org speaks HTTP/2; on HTTP/1.1 the pings
    /// are simply not sent.
    /// </summary>
    internal static HttpClient BuildHttpClient() => new(new Http2UpgradeHandler(BuildHandler()))
    {
        // Must exceed the maximum long-polling duration (50s) plus a margin.
        Timeout = TimeSpan.FromSeconds(75),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };

    /// <summary>See <see cref="BuildHttpClient"/>. PooledConnectionLifetime mirrors Telegram.Bot's own default.</summary>
    internal static SocketsHttpHandler BuildHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(3),
        KeepAlivePingDelay = TimeSpan.FromSeconds(15),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
    };

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

/// <summary>
/// Asks for HTTP/2 on every request that passes through. <see cref="HttpClient.DefaultRequestVersion"/>
/// applies only to messages the client creates itself; Telegram.Bot builds its own
/// <see cref="HttpRequestMessage"/>s, which therefore start at HTTP/1.1 — and on HTTP/1.1 the
/// keep-alive pings of <see cref="TelegramComponent.BuildHandler"/> are never sent (verified
/// 2026-09-04: getMe through a client with DefaultRequestVersion 2.0 still went out as 1.1).
/// HTTP/1.1 remains the fallback when the server does not offer h2.
/// </summary>
internal sealed class Http2UpgradeHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        return base.SendAsync(request, cancellationToken);
    }
}

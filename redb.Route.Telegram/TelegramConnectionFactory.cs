using redb.Route.Abstractions;

namespace redb.Route.Telegram;

/// <summary>
/// Named connection factory for Telegram. Register it in the route registry and reference it by
/// name from the endpoint URI (<c>connectionFactory=my-bot</c>) so the bot token — a full account
/// credential — never has to appear in the URI, and therefore can never reach logs, telemetry,
/// or the Tsak dashboard.
/// <para>
/// Example:
/// <code>
/// context.AddToRegistry("support-bot", new TelegramConnectionFactory
/// {
///     Token = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN")!
/// });
///
/// // route carries no token at all:
/// r.From("telegram://receive?connectionFactory=support-bot")
/// </code>
/// </para>
/// </summary>
public sealed class TelegramConnectionFactory
{
    /// <summary>Bot token from @BotFather. Supports <c>${env:NAME}</c> expressions.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Per-send timeout in seconds for producer calls (default 120, range 1–600).</summary>
    public int SendTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Copies this factory's settings onto the endpoint options, but only for parameters the
    /// endpoint URI did not set explicitly — an inline URI value always wins, so existing routes
    /// keep their behaviour.
    /// </summary>
    internal void ApplyTo(TelegramEndpointOptions options, EndpointUri uri)
    {
        var supplied = uri.RawParameters;

        if (!supplied.ContainsKey(nameof(options.Token))) options.Token = Token;
        if (!supplied.ContainsKey(nameof(options.SendTimeoutSeconds)))
            options.SendTimeoutSeconds = SendTimeoutSeconds;
    }
}

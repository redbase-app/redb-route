using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Telegram;

/// <summary>
/// Telegram Bot endpoint.
/// Path segment determines the operation mode:
///   "receive"  — consumer (long polling)
///   "send"     — producer: text message
///   "document" — producer: file/document (body must be Stream or byte[])
///   "photo"    — producer: photo (body must be Stream or byte[])
///   "answer"   — producer: answer a callback query (no chatId needed)
///   "edit"     — producer: edit an existing message
///   "delete"   — producer: delete a message
///   "download" — producer: fetch a file the user sent (no chatId needed);
///                the bytes land in Out.Body
/// </summary>
public sealed class TelegramEndpoint : EndpointBase<TelegramEndpointOptions>
{
    /// <summary>Mode parsed from URI path: receive | send | document | photo | answer | edit | delete | download.</summary>
    public string Mode { get; }

    /// <summary>Creates a Telegram endpoint.</summary>
    public TelegramEndpoint(EndpointUri uri, IComponent component, TelegramEndpointOptions options)
        : base(uri, component, options)
    {
        Mode = uri.Path.Trim('/').ToLowerInvariant() switch
        {
            "receive"  => "receive",
            "send"     => "send",
            "document" => "document",
            "photo"    => "photo",
            "answer"   => "answer",
            "edit"     => "edit",
            "delete"   => "delete",
            "download" => "download",
            var other  => throw new ArgumentException(
                $"Unknown Telegram mode '{other}'. Expected: receive | send | document | photo | answer | edit | delete | download.",
                nameof(uri))
        };
    }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new TelegramProducer(this, Options);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        if (Mode != "receive")
            throw new InvalidOperationException(
                $"Telegram endpoint '{Mode}' cannot be used as a consumer. " +
                $"Use 'telegram://receive?token=...' for long polling.");

        return new TelegramConsumer(this, processor, Options);
    }
}

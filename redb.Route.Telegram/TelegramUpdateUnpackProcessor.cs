using Telegram.Bot.Types;
using redb.Route.Abstractions;

namespace redb.Route.Telegram;

/// <summary>
/// Inline processor that unpacks a Telegram <see cref="Update"/> from the exchange body
/// and populates <see cref="TelegramHeaders"/> on the inbound message.
/// <para>
/// Use this after <c>Unmarshal(TelegramUpdateSerializer, typeof(Update))</c> in a webhook route:
/// <code>
/// From(Http.Listen("/tg/webhook"))
///     .Unmarshal(new TelegramUpdateSerializer(), typeof(Update))
///     .Process(new TelegramUpdateUnpackProcessor())
///     .To("direct://tg-dispatch");
/// </code>
/// Or use the convenience extension: <c>.UnpackTelegramUpdate()</c>.
/// </para>
/// <para>
/// After this processor runs the exchange has the same headers as a long-polling
/// <see cref="TelegramConsumer"/> exchange, ensuring uniform downstream processing.
/// </para>
/// </summary>
public sealed class TelegramUpdateUnpackProcessor : IProcessor
{
    /// <inheritdoc />
    public Task Process(IExchange exchange, CancellationToken ct = default)
    {
        if (exchange.In.Body is not Update update)
            throw new InvalidOperationException(
                $"TelegramUpdateUnpackProcessor expects an {nameof(Update)} in the exchange body, " +
                $"but found: {exchange.In.Body?.GetType().Name ?? "null"}. " +
                $"Ensure Unmarshal(new TelegramUpdateSerializer(), typeof(Update)) runs before this processor.");

        if (!TelegramUpdateMapper.MapUpdate(update, exchange.In))
            throw new InvalidOperationException(
                $"TelegramUpdateUnpackProcessor: unsupported update type '{update.Type}'. " +
                "Add explicit handling or filter the route before this processor.");

        return Task.CompletedTask;
    }
}

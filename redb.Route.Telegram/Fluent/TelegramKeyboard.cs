using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace redb.Route.Telegram.Fluent;

/// <summary>
/// Typed builders for Telegram reply markup (keyboards).
/// <para>
/// The result is a <c>Telegram.Bot</c> markup object that is placed on the exchange
/// under <see cref="TelegramHeaders.ReplyMarkup"/> — the same header the producer reads.
/// Use these factories to construct keyboards without referencing
/// <c>Telegram.Bot.Types.ReplyMarkups</c> directly in route code.
/// </para>
/// <example><code>
/// // Static keyboard via the route-extension sugar:
/// From(Tg.Receive(token))
///     .SetBody("Choose:")
///     .WithInlineKeyboard(k => k
///         .Row(r => r.Callback("✅ Yes", "act:yes").Callback("❌ No", "act:no")))
///     .To(Tg.Send(token).ChatId(Header(TelegramHeaders.ChatId)));
///
/// // Dynamic keyboard inside a processor:
/// exchange.In.Headers[TelegramHeaders.ReplyMarkup] = TgKeyboard.Inline(k =>
/// {
///     foreach (var o in orders) k.Row(r => r.Callback(o.Title, $"order:{o.Id}"));
/// });
/// </code></example>
/// </summary>
public static class TgKeyboard
{
    /// <summary>Builds an inline keyboard (callback / URL / web-app buttons attached to the message).</summary>
    /// <param name="build">Callback that adds rows of buttons.</param>
    public static InlineKeyboardMarkup Inline(Action<InlineKeyboardBuilder> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var builder = new InlineKeyboardBuilder();
        build(builder);
        return builder.Build();
    }

    /// <summary>Builds a reply keyboard (buttons shown below the text input).</summary>
    /// <param name="build">Callback that adds rows of buttons and configures options.</param>
    public static ReplyKeyboardMarkup Reply(Action<ReplyKeyboardBuilder> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var builder = new ReplyKeyboardBuilder();
        build(builder);
        return builder.Build();
    }

    /// <summary>Builds a markup that removes the current custom reply keyboard.</summary>
    /// <param name="selective">Remove the keyboard only for specific users (see Bot API docs).</param>
    public static ReplyKeyboardRemove Remove(bool selective = false)
    {
        var markup = new ReplyKeyboardRemove();
        if (selective) markup.Selective = true;
        return markup;
    }
}

/// <summary>Fluent builder for an <see cref="InlineKeyboardMarkup"/>.</summary>
public sealed class InlineKeyboardBuilder
{
    private readonly List<List<InlineKeyboardButton>> _rows = new();

    /// <summary>Adds a row of buttons. Empty rows are ignored.</summary>
    /// <param name="build">Callback that adds buttons to the row.</param>
    public InlineKeyboardBuilder Row(Action<InlineRowBuilder> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var row = new InlineRowBuilder();
        build(row);
        if (row.Buttons.Count > 0)
            _rows.Add(row.Buttons);
        return this;
    }

    internal InlineKeyboardMarkup Build() => new(_rows);
}

/// <summary>Fluent builder for a single row of inline keyboard buttons.</summary>
public sealed class InlineRowBuilder
{
    private readonly List<InlineKeyboardButton> _buttons = new();

    internal List<InlineKeyboardButton> Buttons => _buttons;

    /// <summary>Adds a callback button. The tap arrives as a CallbackQuery update carrying <paramref name="data"/>.</summary>
    public InlineRowBuilder Callback(string text, string data)
    {
        _buttons.Add(InlineKeyboardButton.WithCallbackData(text, data));
        return this;
    }

    /// <summary>Adds a button that opens a URL.</summary>
    public InlineRowBuilder Url(string text, string url)
    {
        _buttons.Add(InlineKeyboardButton.WithUrl(text, url));
        return this;
    }

    /// <summary>Adds a button that launches a Web App at <paramref name="url"/>.</summary>
    public InlineRowBuilder WebApp(string text, string url)
    {
        _buttons.Add(InlineKeyboardButton.WithWebApp(text, new WebAppInfo { Url = url }));
        return this;
    }

    /// <summary>Adds a button that prompts the user to pick a chat and switches to inline mode with <paramref name="query"/>.</summary>
    public InlineRowBuilder SwitchInlineQuery(string text, string query = "")
    {
        _buttons.Add(InlineKeyboardButton.WithSwitchInlineQuery(text, query));
        return this;
    }

    /// <summary>Adds a button that switches to inline mode in the current chat with <paramref name="query"/>.</summary>
    public InlineRowBuilder SwitchInlineQueryCurrentChat(string text, string query = "")
    {
        _buttons.Add(InlineKeyboardButton.WithSwitchInlineQueryCurrentChat(text, query));
        return this;
    }
}

/// <summary>Fluent builder for a <see cref="ReplyKeyboardMarkup"/>.</summary>
public sealed class ReplyKeyboardBuilder
{
    private readonly List<List<KeyboardButton>> _rows = new();
    private bool _resize;
    private bool _oneTime;
    private bool _persistent;
    private bool _selective;
    private string? _placeholder;

    /// <summary>Adds a row of buttons. Empty rows are ignored.</summary>
    /// <param name="build">Callback that adds buttons to the row.</param>
    public ReplyKeyboardBuilder Row(Action<ReplyRowBuilder> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var row = new ReplyRowBuilder();
        build(row);
        if (row.Buttons.Count > 0)
            _rows.Add(row.Buttons);
        return this;
    }

    /// <summary>Resize the keyboard vertically to fit its buttons.</summary>
    public ReplyKeyboardBuilder Resize(bool value = true) { _resize = value; return this; }

    /// <summary>Hide the keyboard after the user taps a button.</summary>
    public ReplyKeyboardBuilder OneTime(bool value = true) { _oneTime = value; return this; }

    /// <summary>Keep the keyboard open until explicitly removed.</summary>
    public ReplyKeyboardBuilder Persistent(bool value = true) { _persistent = value; return this; }

    /// <summary>Show the keyboard only to specific users (see Bot API docs).</summary>
    public ReplyKeyboardBuilder Selective(bool value = true) { _selective = value; return this; }

    /// <summary>Placeholder text shown in the input field while the keyboard is active.</summary>
    public ReplyKeyboardBuilder Placeholder(string text) { _placeholder = text; return this; }

    internal ReplyKeyboardMarkup Build()
    {
        var markup = new ReplyKeyboardMarkup(_rows);
        if (_resize)     markup.ResizeKeyboard      = true;
        if (_oneTime)    markup.OneTimeKeyboard      = true;
        if (_persistent) markup.IsPersistent         = true;
        if (_selective)  markup.Selective            = true;
        if (_placeholder is not null) markup.InputFieldPlaceholder = _placeholder;
        return markup;
    }
}

/// <summary>Fluent builder for a single row of reply keyboard buttons.</summary>
public sealed class ReplyRowBuilder
{
    private readonly List<KeyboardButton> _buttons = new();

    internal List<KeyboardButton> Buttons => _buttons;

    /// <summary>Adds a plain text button; tapping it sends the text as a message.</summary>
    public ReplyRowBuilder Button(string text)
    {
        _buttons.Add(new KeyboardButton(text));
        return this;
    }

    /// <summary>Adds a button that requests the user's phone number.</summary>
    public ReplyRowBuilder ContactButton(string text)
    {
        _buttons.Add(KeyboardButton.WithRequestContact(text));
        return this;
    }

    /// <summary>Adds a button that requests the user's location.</summary>
    public ReplyRowBuilder LocationButton(string text)
    {
        _buttons.Add(KeyboardButton.WithRequestLocation(text));
        return this;
    }

    /// <summary>Adds a button that launches a Web App at <paramref name="url"/>.</summary>
    public ReplyRowBuilder WebAppButton(string text, string url)
    {
        _buttons.Add(KeyboardButton.WithWebApp(text, new WebAppInfo { Url = url }));
        return this;
    }
}

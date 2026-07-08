using Telegram.Bot.Types.ReplyMarkups;
using redb.Route.Telegram.Fluent;

namespace redb.Route.Tests.Telegram;

/// <summary>
/// Unit tests for the fluent keyboard builder (<see cref="TgKeyboard"/>) — no network required.
/// Covers inline / reply keyboard construction, button types, reply-keyboard options,
/// keyboard removal, and the "empty row is ignored" rule.
/// </summary>
public class TelegramKeyboardTests
{
    private static List<List<InlineKeyboardButton>> Rows(InlineKeyboardMarkup m) =>
        m.InlineKeyboard.Select(r => r.ToList()).ToList();

    private static List<List<KeyboardButton>> Rows(ReplyKeyboardMarkup m) =>
        m.Keyboard.Select(r => r.ToList()).ToList();

    // ── Inline keyboard ───────────────────────────────────────────────

    [Fact]
    public void Inline_BuildsRowsAndButtons()
    {
        var markup = TgKeyboard.Inline(k => k
            .Row(r => r.Callback("✅ Yes", "act:yes").Callback("❌ No", "act:no"))
            .Row(r => r.Url("📖 Docs", "https://example.com")));

        var rows = Rows(markup);
        rows.Should().HaveCount(2);

        rows[0].Should().HaveCount(2);
        rows[0][0].Text.Should().Be("✅ Yes");
        rows[0][0].CallbackData.Should().Be("act:yes");
        rows[0][1].CallbackData.Should().Be("act:no");

        rows[1].Should().HaveCount(1);
        rows[1][0].Text.Should().Be("📖 Docs");
        rows[1][0].Url.Should().Be("https://example.com");
    }

    [Fact]
    public void Inline_IgnoresEmptyRows()
    {
        var markup = TgKeyboard.Inline(k => k
            .Row(r => { /* no buttons */ })
            .Row(r => r.Callback("A", "a")));

        Rows(markup).Should().HaveCount(1);
    }

    // ── Reply keyboard ────────────────────────────────────────────────

    [Fact]
    public void Reply_BuildsButtonsAndOptions()
    {
        var markup = TgKeyboard.Reply(k => k
            .Row(r => r.Button("📋 Menu").Button("❓ Help"))
            .Resize()
            .OneTime()
            .Persistent()
            .Selective()
            .Placeholder("Type here"));

        var rows = Rows(markup);
        rows.Should().HaveCount(1);
        rows[0].Select(b => b.Text).Should().Equal("📋 Menu", "❓ Help");

        markup.ResizeKeyboard.Should().BeTrue();
        markup.OneTimeKeyboard.Should().BeTrue();
        markup.IsPersistent.Should().BeTrue();
        markup.Selective.Should().BeTrue();
        markup.InputFieldPlaceholder.Should().Be("Type here");
    }

    [Fact]
    public void Reply_OptionsUnsetByDefault()
    {
        var markup = TgKeyboard.Reply(k => k.Row(r => r.Button("A")));

        markup.ResizeKeyboard.Should().BeFalse();
        markup.OneTimeKeyboard.Should().BeFalse();
        markup.IsPersistent.Should().BeFalse();
        markup.Selective.Should().BeFalse();
        markup.InputFieldPlaceholder.Should().BeNull();
    }

    // ── Remove keyboard ───────────────────────────────────────────────

    [Fact]
    public void Remove_ProducesRemoveMarkup()
    {
        var markup = TgKeyboard.Remove();

        markup.RemoveKeyboard.Should().BeTrue();
        markup.Selective.Should().BeFalse();
    }

    [Fact]
    public void Remove_Selective()
    {
        var markup = TgKeyboard.Remove(selective: true);

        markup.RemoveKeyboard.Should().BeTrue();
        markup.Selective.Should().BeTrue();
    }

    // ── Null guards ───────────────────────────────────────────────────

    [Fact]
    public void Inline_NullBuild_Throws()
    {
        var act = () => TgKeyboard.Inline(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Reply_NullBuild_Throws()
    {
        var act = () => TgKeyboard.Reply(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

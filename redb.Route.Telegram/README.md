# redb.Route.Telegram

Telegram Bot transport for [redb.Route](https://github.com/redbase-app/redb-route).  
Long-polling consumer, message / document / photo producer, webhook support, fluent DSL.

---

## Installation

```bash
dotnet add package redb.Route.Telegram
```

Register in DI:

```csharp
builder.Services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteTelegram();
    route.AddRouteBuilder<MyBotRoutes>();
});
```

---

## Quick start — echo bot (long polling)

```csharp
using redb.Route;
using redb.Route.Telegram;
using redb.Route.Telegram.Fluent;

public class EchoBotRoutes : RouteBuilder
{
    protected override void Configure()
    {
        From(Tg.Receive("${env:TELEGRAM_TOKEN}"))
            .Filter(Header(TelegramHeaders.MessageType).isEqualTo("Text"))
            .To(Tg.Send("${env:TELEGRAM_TOKEN}")
                   .ChatId(Header(TelegramHeaders.ChatId)));
    }
}
```

Set the environment variable and run — no other infrastructure needed.

---

## URI schemes

| URI | Direction | Description |
|---|---|---|
| `telegram://receive?token=TOKEN` | Consumer | Long-polling; receives all update types |
| `telegram://send?token=TOKEN&chatId=ID` | Producer | Sends a text message |
| `telegram://document?token=TOKEN&chatId=ID` | Producer | Sends a file (body: `Stream` or `byte[]`) |
| `telegram://photo?token=TOKEN&chatId=ID` | Producer | Sends a photo (body: `Stream` or `byte[]`) |
| `telegram://answer?token=TOKEN` | Producer | Answers a callback query (removes button spinner) |
| `telegram://edit?token=TOKEN&chatId=ID` | Producer | Edits text of an existing message |
| `telegram://delete?token=TOKEN&chatId=ID` | Producer | Deletes a message |

All query parameters support `${...}` expressions.

- **Per-message parameters** (`chatId`, `caption`, `fileName`, `parseMode`, …)
  support the full expression grammar — `${env:VAR}`, `${header.name}`,
  `${property.name}`, `${body}`, etc. They are resolved on every exchange.
- **`token`** is resolved once at endpoint start (before any exchange exists),
  so only `${env:VAR}` is supported. Missing environment variables throw at
  startup instead of silently sending an unresolved literal to Telegram.

```
telegram://send?token=${env:TELEGRAM_TOKEN}&chatId=${header.telegram.chatId}&parseMode=HTML
```

### Producer query parameters

| Parameter | Type | Description |
|---|---|---|
| `token` | string | **Required.** Bot token from @BotFather |
| `chatId` | string/long | Target chat ID or `@channelusername` |
| `parseMode` | string | `HTML` \| `Markdown` \| `MarkdownV2` |
| `caption` | string | Caption for document/photo |
| `fileName` | string | File name when body is `Stream` |
| `disableNotification` | bool | Send silently (default `false`) |
| `replyToMessageId` | string | Message to reply to (`send`/`document`/`photo`). Constant id or a `${...}` expression resolved per message; `.ReplyToIncoming()` emits `${header.telegram.messageId}` |
| `messageId` | string | Message to `edit`/`delete`. Constant id or a `${...}` expression, e.g. `${header.telegram.sentMessageId}` |
| `showAlert` | bool | `answer` mode: show the text as a modal alert instead of a toast (default `false`) |
| `bodyIsFileId` | bool | Treat string body as an existing Telegram `file_id` in `document`/`photo` mode (default `false`) |
| `sendTimeoutSeconds` | int | Per-send timeout, 1–600 (default `120`). Linked with the pipeline cancellation token. |

### Consumer query parameters

| Parameter | Type | Description |
|---|---|---|
| `token` | string | **Required.** Bot token from @BotFather |

The consumer has **no long-polling timeout knob** — the underlying `Telegram.Bot`
receiver manages long-poll timing internally and exposes no public setting, so a
fake option is not offered. To scale processing throughput use
`.Threads(N)` on the route (see [Concurrency](#concurrency)).

---

## Delivery semantics (at-most-once)

`Telegram.Bot`'s update receiver advances the update offset as soon as an update
is **dispatched** to a handler — not after your route finishes processing it.
There is therefore **no redelivery**: if a processor throws, the update is gone.

The consumer reflects this honestly — a failing exchange is **logged as a drop**
(`message dropped (at-most-once, no redelivery)`) and **not** rethrown, so one bad
message never stalls the single update stream. If you need at-least-once, persist
the update (e.g. to a queue) as the first route step and process from there.

---

## Rate limiting (HTTP 429)

Telegram enforces flood limits and answers with HTTP 429 plus a `retry_after`
hint. The producer honours it automatically: on 429 it waits `retry_after`
seconds (clamped to 1–60 s) and retries, up to 3 attempts, respecting the
pipeline cancellation token. This is mandatory — retrying a 429 immediately
escalates to a temporary token ban.

---

## Concurrency

The consumer runs a **single** update stream (Telegram delivers updates for a
bot sequentially per long-poll connection; running two receivers on one token
double-consumes and reorders). Starting a second consumer on the same token
fails fast at startup. To parallelise **processing** without breaking ordering
guarantees, use route threads:

```csharp
From(Tg.Receive(token))
    .Threads(4)          // 4 concurrent processing workers, one receiver
    .Process(...);
```

See [CONCURRENCY.md](../../CONCURRENCY.md) for the full model.

---

## Consumer headers

After receiving an update, the consumer populates these headers on the exchange:

### All updates

| Header | Type | Description |
|---|---|---|
| `telegram.updateId` | `long` | Unique update ID |
| `telegram.updateType` | `string` | `Message`, `EditedMessage`, `CallbackQuery`, … |

### Message updates (`updateType` = Message / EditedMessage / ChannelPost / …)

| Header | Type | Description |
|---|---|---|
| `telegram.messageId` | `int` | Message ID within the chat |
| `telegram.messageType` | `string` | `Text`, `Photo`, `Document`, `Sticker`, … |
| `telegram.text` | `string?` | Message text (omitted for non-text messages) |
| `telegram.chatId` | `long` | Chat ID — use this to reply |
| `telegram.chatType` | `string` | `Private`, `Group`, `Supergroup`, `Channel` |
| `telegram.userId` | `long` | Sender user ID |
| `telegram.firstName` | `string` | Sender first name |
| `telegram.lastName` | `string?` | Sender last name (omitted if not set) |
| `telegram.username` | `string?` | Sender `@username` without `@` (omitted if not set) |
| `telegram.languageCode` | `string?` | Sender language code, e.g. `ru`, `en` |

### Attachments (any message carrying a file)

Present only when the message has one; a plain text message gets none of them.

| Header | Type | Description |
|---|---|---|
| `telegram.attachment.kind` | `string` | `voice`, `audio`, `videoNote`, `video`, `animation`, `document`, `photo`, `sticker` |
| `telegram.attachment.fileId` | `string` | Telegram `file_id` — pass it to `getFile` to download |
| `telegram.attachment.mimeType` | `string?` | MIME type, when Telegram reports one |
| `telegram.attachment.fileSize` | `long?` | Size in bytes, when reported |
| `telegram.attachment.duration` | `int?` | Seconds — voice, audio, video, video note |
| `telegram.attachment.fileName` | `string?` | Original name of a document or audio file |
| `telegram.attachment.caption` | `string?` | Caption typed alongside the file |

Photos arrive as a size ladder; the **largest** size is reported — downscaling is
the caller's choice, not the connector's.

⚠️ **Not to be confused with the producer's `telegram.fileId`.** That one is an
instruction to *send* an already-hosted file, and headers travel with the exchange:
a route that consumed a voice note and then answered with a document would echo the
user's own recording back at them if both sides read one key.

The caption stays a header and does **not** become the body: promoting it would make
a captioned photo indistinguishable from a typed command downstream.

### Callback query updates (`updateType` = CallbackQuery)

| Header | Type | Description |
|---|---|---|
| `telegram.callbackQueryId` | `string` | Pass to `bot.AnswerCallbackQuery(...)` |
| `telegram.callbackData` | `string?` | Data from the `InlineKeyboardButton` |
| `telegram.chatId` | `long?` | Chat ID (omitted for inline messages) |
| `telegram.userId` | `long` | User who tapped the button |
| `telegram.firstName` | `string` | User first name |
| `telegram.username` | `string?` | User `@username` (omitted if not set) |

### Mini App data (`messageType` = WebAppData)

| Header | Type | Description |
|---|---|---|
| `telegram.webAppData` | `string` | Payload passed to `WebApp.sendData(...)` inside the mini app |
| `telegram.webAppButtonText` | `string?` | Text of the reply-keyboard button that opened the mini app |

Delivered as a regular message update, so all message headers above
(`telegram.chatId`, `telegram.userId`, …) are present too — except
`telegram.text`, which such a message does not carry.

Exchange body = message text (or callback data for CallbackQuery, or the mini app
payload for WebAppData).

---

## Producer headers

Set these on the exchange before `.To(Tg.Send/...)` to control per-message behaviour:

| Header | Type | Description |
|---|---|---|
| `telegram.chatId` | `long` / `string` | Overrides `chatId` option. Wins over the URI value. |
| `telegram.parseMode` | `string` | Per-message `HTML` / `Markdown` / `MarkdownV2` override |
| `telegram.disableNotification` | `bool` | Per-message silent send override |
| `telegram.replyToMessageId` | `int` | Send the message as a reply |
| `telegram.replyMarkup` | `InlineKeyboardMarkup` / `ReplyKeyboardMarkup` / `ReplyKeyboardRemove` | Inline / reply keyboard attached to the message |
| `telegram.messageId` | `int` | Target for `edit` / `delete` modes. Wins over the `messageId` option |
| `telegram.caption` | `string` | Per-message caption override for `document` / `photo` |
| `telegram.showAlert` | `bool` | Per-message override for `showAlert` in `answer` mode |
| `telegram.fileId` | `string` | Reuse an existing Telegram file in `document` / `photo` mode instead of uploading |
| `telegram.fileName` | `string` | File name when body is `Stream` / `byte[]` |
| `telegram.callbackQueryId` | `string` | Required for `answer` mode (set by consumer on `CallbackQuery`) |

After a successful `send` / `document` / `photo` / `edit` the producer writes the
resulting message id back to the exchange:

| Header | Type | Description |
|---|---|---|
| `telegram.sentMessageId` | `int` | `Message.MessageId` returned by Telegram — use for follow-up `edit` / `delete` |

**Note on `edit` mode:** the Bot API's `editMessageText` does not accept
`disable_notification`; editing a message never produces a notification, so the
option and header are intentionally ignored in `edit` mode.

**Body → file mapping for `document` / `photo`:**

- `Stream` or `byte[]` — uploaded as a new file (use `fileName` for a sensible name).
- HTTP(S) URL `string` — passed to Telegram to fetch by URL.
- To reuse an existing Telegram file, set header `telegram.fileId` **or** add
  `bodyIsFileId=true` to the URI (then a string body is treated as a `file_id`).
  A plain string body without either of these is rejected with a clear error —
  the transport does not guess.

---

## Fluent DSL

```csharp
using redb.Route.Telegram.Fluent;

// Consumer (no timeout knob — use .Threads(N) on the route to scale processing)
From(Tg.Receive(token))

// Text producer
.To(Tg.Send(token).ChatId(chatId).ParseMode("HTML"))

// Document producer — body must be Stream or byte[]
.To(Tg.Document(token).ChatId(chatId).FileName("report.pdf").Caption("Daily report"))

// Photo producer
.To(Tg.Photo(token).ChatId(chatId).Caption("Screenshot"))

// Reuse an existing Telegram file_id instead of uploading
.To(Tg.Document(token).ChatId(chatId).BodyIsFileId())

// Tighter per-send timeout (default 120s)
.To(Tg.Send(token).ChatId(chatId).Timeout(30))

// Reply to the incoming message (sugar for replyToMessageId=${header.telegram.messageId})
.To(Tg.Send(token).ChatId(chatId).ReplyToIncoming())

// Expressions are supported everywhere
Tg.Send("${env:TELEGRAM_TOKEN}").ChatId(Header("telegram.chatId"))
```

---

## Reply to a message

To reply to the message that triggered the exchange, use `.ReplyToIncoming()` —
sugar for `replyToMessageId=${header.telegram.messageId}`, the id the consumer put
on the exchange:

```csharp
From(Tg.Receive(token))
    .To(Tg.Send(token).ChatId(Header(TelegramHeaders.ChatId)).ReplyToIncoming())
```

`ReplyTo(...)` targets any message — a constant id or an expression resolved per
message. The `telegram.replyToMessageId` header, when set, wins over the option:

```csharp
.To(Tg.Send(token).ChatId(chatId).ReplyTo(Header("myApp.threadRootId")))
```

If the expression resolves to nothing (e.g. `.ReplyToIncoming()` on an update
without `telegram.messageId`), the message is sent unthreaded rather than failing.

---

## Answer a callback query

Must be called after any inline button tap — otherwise the button shows an infinite spinner.

```csharp
From(Tg.Receive(token))
    .Filter(Header(TelegramHeaders.UpdateType).isEqualTo("CallbackQuery"))
    .SetBody("✅ Done")                                         // optional toast text
    .To(Tg.Answer(token))                                       // clears the spinner
    .Process(e => e.In.SetBody(HandleAction(
        e.In.GetHeader<string>(TelegramHeaders.CallbackData)!)))
    .To(Tg.Send(token).ChatId(Header(TelegramHeaders.ChatId))); // send follow-up
```

`Answer` reads `telegram.callbackQueryId` from headers (set automatically by the consumer).
`.ShowAlert()` turns the toast into a modal alert.

---

## Edit a message

Body = new text. The target comes from the `telegram.messageId` header or the
`messageId=` option — e.g. `.MessageId(Header(TelegramHeaders.SentMessageId))`
to edit the message just sent by an upstream step.

```csharp
.SetBody("Updated text")
.Process(e =>
{
    // optionally update the inline keyboard too
    e.In.Headers[TelegramHeaders.ReplyMarkup] = new InlineKeyboardMarkup(
        InlineKeyboardButton.WithCallbackData("🔄 Refresh", "action:refresh"));
})
.To(Tg.Edit(token).ChatId(Header(TelegramHeaders.ChatId)))
```

---

## Delete a message

Target: `telegram.messageId` header or the `messageId=` option.

```csharp
.To(Tg.Delete(token).ChatId(Header(TelegramHeaders.ChatId)))
```

---

## Webhook

Use `.UnpackTelegramUpdate()` to process Telegram webhook POSTs.  
The exchange headers are identical to the long-polling path — downstream routes work unchanged.

```csharp
// Requires redb.Route.Http for Http.Listen(...)
From(Http.Listen("/tg/webhook")
         .Header("X-Telegram-Bot-Api-Secret-Token", webhookSecret)
         .InOut())
    .UnpackTelegramUpdate()          // deserialise JSON → Update + populate headers
    .To("direct://tg-dispatch");

// The same dispatcher handles both polling and webhook traffic
From("direct://tg-dispatch")
    .Choice()
        .When(Header(TelegramHeaders.UpdateType).isEqualTo("Message"))
            .To("direct://handle-message")
        ...
    .EndChoice();
```

---

## Sending buttons (reply markup)

Use the fluent keyboard builder — it constructs the markup for you and needs no
`using Telegram.Bot.Types.ReplyMarkups` in your route code. Under the hood it just
sets the `telegram.replyMarkup` header, so the raw-header approach (below) keeps working.

### Inline keyboard (callback buttons)

```csharp
using redb.Route.Telegram.Fluent;

From(Tg.Receive(token))
    .SetBody("Choose an option:")
    .WithInlineKeyboard(k => k
        .Row(r => r.Callback("✅ Confirm", "action:confirm")
                   .Callback("❌ Cancel",  "action:cancel"))
        .Row(r => r.Callback("📊 Stats",   "action:stats")))
    .To(Tg.Send(token).ChatId(Header(TelegramHeaders.ChatId)).ParseMode("HTML"));
```

Inline row buttons: `Callback(text, data)`, `Url(text, url)`, `WebApp(text, url)`,
`SwitchInlineQuery(text, query)`, `SwitchInlineQueryCurrentChat(text, query)`.

Telegram delivers the user's tap as a `CallbackQuery` update — handle it on the consumer side:

```csharp
From(Tg.Receive(token))
    .Choice()
        .When(Header(TelegramHeaders.UpdateType).isEqualTo("CallbackQuery"))
            .Process(async (e, ct) =>
            {
                var data = e.In.GetHeader<string>(TelegramHeaders.CallbackData);
                // respond with AnswerCallbackQuery via injected ITelegramBotClient
            })
        ...
    .EndChoice();
```

### Reply keyboard (keyboard shown below text input)

```csharp
.WithReplyKeyboard(k => k
    .Row(r => r.Button("📋 Menu").Button("❓ Help"))
    .Resize()
    .OneTime())
```

Reply row buttons: `Button(text)`, `ContactButton(text)`, `LocationButton(text)`,
`WebAppButton(text, url)`. Keyboard options: `Resize()`, `OneTime()`, `Persistent()`,
`Selective()`, `Placeholder(text)`.

### Mini Apps (Web Apps)

A web app button opens your HTTPS page inside the Telegram client:

```csharp
// inline — opens the mini app, cannot send data back to the bot
.WithInlineKeyboard(k => k.Row(r => r.WebApp("🌟 Open", "https://app.example/")))

// reply keyboard — the mini app may call WebApp.sendData(...)
.WithReplyKeyboard(k => k.Row(r => r.WebAppButton("🌟 Open", "https://app.example/")))
```

`sendData` closes the mini app and delivers a message update with
`telegram.messageType = "WebAppData"`; the payload lands in the body and in
`telegram.webAppData`:

```csharp
From(Tg.Receive(token))
    .Filter(Header(TelegramHeaders.MessageType).isEqualTo("WebAppData"))
    .Process((e, ct) =>
    {
        var payload = e.In.GetHeader<string>(TelegramHeaders.WebAppData);
        // ... deserialise and handle
        return Task.CompletedTask;
    })
    .SetBody("✅ Получено")
    .To(Tg.Send(token).ChatId(Header(TelegramHeaders.ChatId)));
```

`sendData` is one-way and limited to 4096 bytes — the mini app gets no response
back. That is a Bot API property, not a connector limitation.

### Remove keyboard

```csharp
.WithoutKeyboard()
```

### Dynamic keyboards (built from runtime data)

When rows depend on data available only at processing time, build the markup with
`TgKeyboard.Inline(...)` / `TgKeyboard.Reply(...)` inside a `.Process(...)` step and
assign it to the header directly:

```csharp
using redb.Route.Telegram.Fluent;

.Process((e, ct) =>
{
    e.In.Headers[TelegramHeaders.ReplyMarkup] = TgKeyboard.Inline(k =>
    {
        foreach (var o in orders)
            k.Row(r => r.Callback(o.Title, $"order:{o.Id}"));
    });
    return Task.CompletedTask;
})
.To(Tg.Send(token).ChatId(Header(TelegramHeaders.ChatId)))
```

### Raw header (still supported)

The builder is optional. You can always set the `telegram.replyMarkup` header to a
`Telegram.Bot` `InlineKeyboardMarkup` / `ReplyKeyboardMarkup` / `ReplyKeyboardRemove`
instance yourself before the `.To(Tg.Send(...))` step.

---

## What is NOT included

| Feature | Where to find it |
|---|---|
| Webhook HTTP server | `redb.Route.Http` — use `Http.Listen(...)` |
| FSM / conversation state | Application layer — use your own state store |
| At-least-once delivery / redelivery | Persist updates to a queue as the first route step (see [Delivery semantics](#delivery-semantics-at-most-once)) |
| Batching | Not supported by Telegram Bot API — each message is a separate HTTP call |

using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telegram;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgDsl = redb.Route.Telegram.Fluent.Tg;
using TgParseMode = Telegram.Bot.Types.Enums.ParseMode;
using Message = redb.Route.Core.Message;
using TgMessage = Telegram.Bot.Types.Message;

namespace redb.Route.Tests.Telegram;

/// <summary>
/// Unit tests for the Telegram connector — no network required. Cover URI→options binding,
/// <c>${env:}</c> token resolution, validation (incl. parseMode enum), the fluent DSL round-trip
/// (esp. the previously-lost <c>.Timeout()</c> / <c>.BodyIsFileId()</c>), header helpers, the update
/// mapper, the webhook serializer, component scheme + endpoint mode routing, and the single-stream guard.
/// </summary>
public class TelegramUnitTests
{
    private const string Token = "123456:AAAA-Test_Token";

    private static TelegramEndpointOptions Bind(string query)
    {
        var uri = EndpointUriParser.Parse($"telegram://send?{query}");
        var options = new TelegramEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        return options;
    }

    // ── Options binding ───────────────────────────────────────────────

    [Fact]
    public void Options_BindsAllProducerParams()
    {
        var o = Bind($"token={Token}&chatId=987654&parseMode=HTML&caption=hi"
            + "&fileName=report.pdf&bodyIsFileId=true&disableNotification=true"
            + "&replyToMessageId=77&messageId=12&showAlert=true&sendTimeoutSeconds=45");

        o.Token.Should().Be(Token);
        o.ChatId.Should().Be("987654");
        o.ParseMode.Should().Be("HTML");
        o.Caption.Should().Be("hi");
        o.FileName.Should().Be("report.pdf");
        o.BodyIsFileId.Should().BeTrue();
        o.DisableNotification.Should().BeTrue();
        o.ReplyToMessageId.Should().Be("77");
        o.MessageId.Should().Be("12");
        o.ShowAlert.Should().BeTrue();
        o.SendTimeoutSeconds.Should().Be(45);
    }

    [Fact]
    public void Options_Defaults()
    {
        var o = Bind($"token={Token}");
        o.ChatId.Should().BeNull();
        o.ParseMode.Should().BeNull();
        o.BodyIsFileId.Should().BeFalse();
        o.DisableNotification.Should().BeFalse();
        o.ReplyToMessageId.Should().BeNull();
        o.MessageId.Should().BeNull();
        o.ShowAlert.Should().BeFalse();
        o.SendTimeoutSeconds.Should().Be(120);
    }

    // ── Token expression resolution ───────────────────────────────────

    [Fact]
    public void ResolveTokenExpression_ResolvesEnvVar()
    {
        var name = "TG_TEST_TOKEN_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, Token);
        try
        {
            TelegramEndpointOptions.ResolveTokenExpression($"${{env:{name}}}").Should().Be(Token);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void ResolveTokenExpression_LiteralUnchanged()
    {
        TelegramEndpointOptions.ResolveTokenExpression(Token).Should().Be(Token);
    }

    [Fact]
    public void ResolveTokenExpression_MissingEnvVar_Throws()
    {
        var name = "TG_MISSING_" + Guid.NewGuid().ToString("N");
        var act = () => TelegramEndpointOptions.ResolveTokenExpression($"${{env:{name}}}");
        act.Should().Throw<InvalidOperationException>().WithMessage("*not set*");
    }

    // ── Validation ────────────────────────────────────────────────────

    [Theory]
    [InlineData("chatId=1")]                              // missing token
    [InlineData("token=&chatId=1")]                       // empty token
    [InlineData("token=t&parseMode=Markdwn")]             // typo in parseMode
    [InlineData("token=t&sendTimeoutSeconds=0")]          // out of range (low)
    [InlineData("token=t&sendTimeoutSeconds=601")]        // out of range (high)
    [InlineData("token=t&replyToMessageId=abc")]          // constant that is not a message id
    [InlineData("token=t&messageId=abc")]                 // constant that is not a message id
    public void Validate_RejectsBadConfig(string query)
    {
        var act = () => Bind(query).Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("token=t")]
    [InlineData("token=t&parseMode=HTML")]
    [InlineData("token=t&parseMode=Markdown")]
    [InlineData("token=t&parseMode=MarkdownV2")]
    [InlineData("token=t&parseMode=none")]
    [InlineData("token=t&sendTimeoutSeconds=600")]
    public void Validate_AcceptsValidConfig(string query)
    {
        var act = () => Bind(query).Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null, true, TgParseMode.None)]
    [InlineData("", true, TgParseMode.None)]
    [InlineData("HTML", true, TgParseMode.Html)]
    [InlineData("html", true, TgParseMode.Html)]
    [InlineData("Markdown", true, TgParseMode.Markdown)]
    [InlineData("MarkdownV2", true, TgParseMode.MarkdownV2)]
    [InlineData("none", true, TgParseMode.None)]
    [InlineData("Markdwn", false, TgParseMode.None)]
    [InlineData("bogus", false, TgParseMode.None)]
    public void TryParseParseMode_TruthTable(string? raw, bool expectedOk, TgParseMode expectedMode)
    {
        var ok = TelegramEndpointOptions.TryParseParseMode(raw, out var mode);
        ok.Should().Be(expectedOk);
        if (expectedOk) mode.Should().Be(expectedMode);
    }

    // ── Fluent DSL round-trip ─────────────────────────────────────────

    [Fact]
    public void Dsl_Send_RoundTripsThroughOptions()
    {
        string uri = TgDsl.Send(Token)
            .ChatId(987654)
            .ParseMode("HTML")
            .DisableNotification()
            .ReplyToIncoming()
            .Timeout(30)
            .BodyIsFileId();

        var parsed = EndpointUriParser.Parse(uri);
        var o = new TelegramEndpointOptions();
        o.BindFromUri(parsed.RawParameters);

        o.Token.Should().Be(Token);
        o.ChatId.Should().Be("987654");
        o.ParseMode.Should().Be("HTML");
        o.DisableNotification.Should().BeTrue();
        o.ReplyToMessageId.Should().Be("${header.telegram.messageId}",
            ".ReplyToIncoming() is sugar for the incoming-message-id expression");
        // Previously lost by the builder — these two are the point of the test.
        o.SendTimeoutSeconds.Should().Be(30);
        o.BodyIsFileId.Should().BeTrue();
    }

    [Fact]
    public void Dsl_Document_EmitsExpectedUri()
    {
        string uri = TgDsl.Document(Token).ChatId(1).FileName("report.pdf").Caption("Daily");
        uri.Should().StartWith("telegram://document?");
        uri.Should().Contain("fileName=report.pdf");
        uri.Should().Contain("caption=Daily");
    }

    [Fact]
    public void Dsl_Edit_MessageId_RoundTripsThroughOptions()
    {
        string uri = TgDsl.Edit(Token).ChatId(1).MessageId(12);

        var parsed = EndpointUriParser.Parse(uri);
        var o = new TelegramEndpointOptions();
        o.BindFromUri(parsed.RawParameters);

        o.MessageId.Should().Be("12");
    }

    [Fact]
    public void Dsl_Answer_ShowAlert_EmitsExpectedUri()
    {
        string uri = TgDsl.Answer(Token).ShowAlert();
        uri.Should().StartWith("telegram://answer?");
        uri.Should().Contain("showAlert=true");
    }

    // ── Header helpers ────────────────────────────────────────────────

    [Fact]
    public void Headers_PrefixAndGuard()
    {
        TelegramHeaders.ChatId.Should().StartWith("telegram.");
        TelegramHeaders.IsRedbHeader(TelegramHeaders.MessageId).Should().BeTrue();
        TelegramHeaders.IsRedbHeader("customHeader").Should().BeFalse();
    }

    // ── Update mapper ─────────────────────────────────────────────────

    [Fact]
    public void MapMessage_TextMessage_PopulatesBodyAndHeaders()
    {
        var msg = new TgMessage
        {
            Id = 42,
            Text = "hello",
            Chat = new Chat { Id = 123, Type = ChatType.Private },
            From = new User { Id = 7, FirstName = "Ann", Username = "ann", LanguageCode = "en" }
        };
        var m = new Message();

        TelegramUpdateMapper.MapMessage(msg, UpdateType.Message, m);

        m.Body.Should().Be("hello");
        m.Headers[TelegramHeaders.MessageId].Should().Be(42);
        m.Headers[TelegramHeaders.ChatId].Should().Be(123L);
        m.Headers[TelegramHeaders.ChatType].Should().Be("Private");
        m.Headers[TelegramHeaders.UpdateType].Should().Be("Message");
        m.Headers[TelegramHeaders.UserId].Should().Be(7L);
        m.Headers[TelegramHeaders.FirstName].Should().Be("Ann");
        m.Headers[TelegramHeaders.Username].Should().Be("ann");
        m.Headers[TelegramHeaders.LanguageCode].Should().Be("en");
    }

    [Fact]
    public void MapUpdate_TextMessage_ReturnsTrue()
    {
        var update = new Update
        {
            Id = 1,
            Message = new TgMessage
            {
                Id = 10,
                Text = "hi",
                Chat = new Chat { Id = 5, Type = ChatType.Group }
            }
        };
        var m = new Message();

        TelegramUpdateMapper.MapUpdate(update, m).Should().BeTrue();
        m.Body.Should().Be("hi");
        m.Headers[TelegramHeaders.UpdateId].Should().Be(1L);
        m.Headers[TelegramHeaders.ChatId].Should().Be(5L);
    }

    [Fact]
    public void MapUpdate_EditedMessage_ReturnsTrue()
    {
        var update = new Update
        {
            Id = 2,
            EditedMessage = new TgMessage
            {
                Id = 11,
                Text = "edited",
                Chat = new Chat { Id = 6, Type = ChatType.Supergroup }
            }
        };
        var m = new Message();

        TelegramUpdateMapper.MapUpdate(update, m).Should().BeTrue();
        m.Headers[TelegramHeaders.UpdateType].Should().Be("EditedMessage");
        m.Body.Should().Be("edited");
    }

    [Fact]
    public void MapUpdate_CallbackQuery_PopulatesCallbackHeaders()
    {
        var update = new Update
        {
            Id = 3,
            CallbackQuery = new CallbackQuery
            {
                Id = "cbq-1",
                Data = "action:approve",
                From = new User { Id = 9, FirstName = "Bob", Username = "bob" },
                Message = new TgMessage
                {
                    Id = 12,
                    Chat = new Chat { Id = 77, Type = ChatType.Private }
                }
            }
        };
        var m = new Message();

        TelegramUpdateMapper.MapUpdate(update, m).Should().BeTrue();
        m.Body.Should().Be("action:approve");
        m.Headers[TelegramHeaders.CallbackQueryId].Should().Be("cbq-1");
        m.Headers[TelegramHeaders.CallbackData].Should().Be("action:approve");
        m.Headers[TelegramHeaders.UserId].Should().Be(9L);
        m.Headers[TelegramHeaders.ChatId].Should().Be(77L);
    }

    [Fact]
    public void MapMessage_WebAppData_PopulatesBodyAndHeaders()
    {
        var msg = new TgMessage
        {
            Id = 43,
            Chat = new Chat { Id = 123, Type = ChatType.Private },
            From = new User { Id = 7, FirstName = "Ann" },
            WebAppData = new WebAppData { Data = """{"city":"Moscow"}""", ButtonText = "Собрать" }
        };
        var m = new Message();

        TelegramUpdateMapper.MapMessage(msg, UpdateType.Message, m);

        // No Text on a web_app_data message — the payload becomes the body.
        m.Body.Should().Be("""{"city":"Moscow"}""");
        m.Headers[TelegramHeaders.WebAppData].Should().Be("""{"city":"Moscow"}""");
        m.Headers[TelegramHeaders.WebAppButtonText].Should().Be("Собрать");
        m.Headers.Should().NotContainKey(TelegramHeaders.Text);
        m.Headers[TelegramHeaders.ChatId].Should().Be(123L);
    }

    [Fact]
    public void MapUpdate_TextMessage_HasNoWebAppHeaders()
    {
        var update = new Update
        {
            Id = 4,
            Message = new TgMessage
            {
                Id = 13,
                Text = "plain",
                Chat = new Chat { Id = 5, Type = ChatType.Private }
            }
        };
        var m = new Message();

        TelegramUpdateMapper.MapUpdate(update, m).Should().BeTrue();
        m.Body.Should().Be("plain");
        m.Headers.Should().NotContainKey(TelegramHeaders.WebAppData);
        m.Headers.Should().NotContainKey(TelegramHeaders.WebAppButtonText);
    }

    [Fact]
    public void MapUpdate_UnsupportedType_ReturnsFalse()
    {
        // An update with no recognised payload (only the id) maps to Unknown → skipped.
        var update = new Update { Id = 99 };
        TelegramUpdateMapper.MapUpdate(update, new Message()).Should().BeFalse();
    }

    // ── Webhook serializer ────────────────────────────────────────────

    [Fact]
    public void Serializer_DeserializesWebhookJsonToUpdate()
    {
        var json = System.Text.Encoding.UTF8.GetBytes(
            """{"update_id":55,"message":{"message_id":10,"date":0,"chat":{"id":123,"type":"private"},"text":"webhook-hi"}}""");

        var serializer = new TelegramUpdateSerializer();
        var update = serializer.Deserialize<Update>(json);

        update.Should().NotBeNull();
        update!.Id.Should().Be(55);
        update.Message!.Text.Should().Be("webhook-hi");
        update.Message.Chat.Id.Should().Be(123);
    }

    [Fact]
    public void Serializer_Serialize_IsNotSupported()
    {
        var act = () => new TelegramUpdateSerializer().Serialize(new Update());
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public async Task UnpackProcessor_MapsUpdateFromBody()
    {
        var exchange = new Exchange(new Message
        {
            Body = new Update
            {
                Id = 4,
                Message = new TgMessage
                {
                    Id = 1,
                    Text = "unpack-me",
                    Chat = new Chat { Id = 8, Type = ChatType.Private }
                }
            }
        });

        await new TelegramUpdateUnpackProcessor().Process(exchange);

        exchange.In.Body.Should().Be("unpack-me");
        exchange.In.Headers[TelegramHeaders.ChatId].Should().Be(8L);
    }

    // ── Component + endpoint ──────────────────────────────────────────

    [Fact]
    public void Component_SchemeAndEndpointCreation()
    {
        var component = new TelegramComponent();
        component.Scheme.Should().Be("telegram");

        var endpoint = component.CreateEndpoint(EndpointUriParser.Parse($"telegram://receive?token={Token}"));
        endpoint.Should().BeOfType<TelegramEndpoint>();
        ((TelegramEndpoint)endpoint).Mode.Should().Be("receive");
        endpoint.CreateConsumer(Substitute.For<IProcessor>()).Should().BeAssignableTo<IConsumer>();
    }

    [Theory]
    [InlineData("send")]
    [InlineData("document")]
    [InlineData("photo")]
    [InlineData("answer")]
    [InlineData("edit")]
    [InlineData("delete")]
    public void Endpoint_ProducerModes_CreateProducer(string mode)
    {
        var endpoint = new TelegramComponent()
            .CreateEndpoint(EndpointUriParser.Parse($"telegram://{mode}?token={Token}"));
        ((TelegramEndpoint)endpoint).Mode.Should().Be(mode);
        endpoint.CreateProducer().Should().BeAssignableTo<IProducer>();
    }

    [Fact]
    public void Endpoint_NonReceiveMode_CannotCreateConsumer()
    {
        var endpoint = new TelegramComponent()
            .CreateEndpoint(EndpointUriParser.Parse($"telegram://send?token={Token}"));
        var act = () => endpoint.CreateConsumer(Substitute.For<IProcessor>());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Endpoint_UnknownMode_Throws()
    {
        var act = () => new TelegramComponent()
            .CreateEndpoint(EndpointUriParser.Parse($"telegram://bogus?token={Token}"));
        act.Should().Throw<ArgumentException>();
    }

    // ── Single-stream guard ───────────────────────────────────────────

    [Fact]
    public void RegisterConsumer_SecondForSameToken_Throws()
    {
        var component = new TelegramComponent();
        component.RegisterConsumer(Token);
        var act = () => component.RegisterConsumer(Token);
        act.Should().Throw<InvalidOperationException>().WithMessage("*one consumer per bot token*");
    }

    [Fact]
    public void RegisterConsumer_AfterUnregister_Succeeds()
    {
        var component = new TelegramComponent();
        component.RegisterConsumer(Token);
        component.UnregisterConsumer(Token);
        var act = () => component.RegisterConsumer(Token);
        act.Should().NotThrow();
    }
}

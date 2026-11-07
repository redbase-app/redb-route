using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telegram;
using Telegram.Bot;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Message = redb.Route.Core.Message;
using TgDsl = redb.Route.Telegram.Fluent.Tg;

namespace redb.Route.Tests.Telegram;

/// <summary>
/// The <c>download</c> producer mode: <c>getFile</c> plus the transfer, against a mocked
/// <see cref="ITelegramBotClient"/> — no network, no real bot.
/// <para>
/// What the mode is for is one line of a voice route ("the user sent a recording, fetch it"),
/// and everything worth testing about it is what happens when that line is not the happy path:
/// which file id wins, and what stops a large upload from becoming a large allocation.
/// </para>
/// </summary>
public sealed class TelegramDownloadTests
{
    private const string Token = "123456:AAAA-Test_Token";

    private static TelegramEndpoint Endpoint(string extra = "") =>
        (TelegramEndpoint)new TelegramComponent()
            .CreateEndpoint(EndpointUriParser.Parse($"telegram://download?token={Token}{extra}"));

    /// <summary>
    /// A client that answers <c>getFile</c> with <paramref name="file"/> and streams
    /// <paramref name="payload"/> on the transfer, recording the file id it was asked for.
    /// </summary>
    private static ITelegramBotClient Client(
        TGFile file, byte[] payload, Action<string>? onDownload = null)
    {
        var mock = Substitute.For<ITelegramBotClient>();

        mock.SendRequest(Arg.Any<IRequest<TGFile>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(file));

        mock.DownloadFile(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                onDownload?.Invoke(call.ArgAt<string>(0));
                var destination = call.ArgAt<Stream>(1);
                destination.Write(payload, 0, payload.Length);
                return Task.CompletedTask;
            });

        return mock;
    }

    private static TGFile File(string path = "voice/file_1.oga", long? size = null) => new()
    {
        FileId = "file-id",
        FileUniqueId = "unique-id",
        FilePath = path,
        FileSize = size
    };

    private static async Task<IExchange> RunAsync(
        TelegramEndpoint endpoint, ITelegramBotClient client, IExchange exchange)
    {
        var producer = (TelegramProducer)endpoint.CreateProducer();
        producer.UseTestClient(client);
        await producer.Start();
        try
        {
            await producer.Process(exchange);
            return exchange;
        }
        finally { await producer.Stop(); }
    }

    // ── The ordinary case ─────────────────────────────────────────────────────

    [Fact]
    public async Task Download_PutsTheBytesInOut_AndDescribesTheFile()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var exchange = new Exchange(new Message(null));
        exchange.In.Headers[TelegramHeaders.AttachmentFileId] = "voice-file-id";

        await RunAsync(Endpoint(), Client(File(), payload), exchange);

        exchange.Out.Should().NotBeNull("download is the one producer mode that returns something");
        exchange.Out!.Body.Should().BeOfType<byte[]>().Which.Should().Equal(payload);

        exchange.Out.Headers[TelegramHeaders.FilePath].Should().Be("voice/file_1.oga");
        exchange.Out.Headers[TelegramHeaders.FileSize].Should().Be(5L);
        exchange.Out.Headers[TelegramHeaders.FileUniqueId].Should().Be("unique-id");
    }

    [Fact]
    public async Task Download_TakesTheAttachmentWithNoConfigurationAtAll()
    {
        string? requested = null;
        var exchange = new Exchange(new Message(null));
        exchange.In.Headers[TelegramHeaders.AttachmentFileId] = "voice-file-id";

        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<IRequest<TGFile>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                // The request object carries the file id the producer resolved; reading it back
                // is the only way to prove WHICH file was asked for, not merely that one was.
                requested = call.ArgAt<IRequest<TGFile>>(0)
                    .GetType().GetProperty("FileId")?.GetValue(call.ArgAt<IRequest<TGFile>>(0)) as string;
                return Task.FromResult(File());
            });
        client.DownloadFile(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.CompletedTask);

        await RunAsync(Endpoint(), client, exchange);

        requested.Should().Be("voice-file-id",
            "the header the consumer already writes is what a voice route has, and needing to " +
            "restate it in the URI would make the common case the configured one");
    }

    [Fact]
    public async Task Download_ExplicitHeaderBeatsTheAttachment()
    {
        string? requested = null;
        var exchange = new Exchange(new Message(null));
        exchange.In.Headers[TelegramHeaders.AttachmentFileId] = "the-users-recording";
        exchange.In.Headers[TelegramHeaders.FileId] = "the-one-we-asked-for";

        var client = Client(File(), [7], onDownload: _ => { });
        client.SendRequest(Arg.Any<IRequest<TGFile>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.ArgAt<IRequest<TGFile>>(0);
                requested = request.GetType().GetProperty("FileId")?.GetValue(request) as string;
                return Task.FromResult(File());
            });

        await RunAsync(Endpoint(), client, exchange);

        requested.Should().Be("the-one-we-asked-for",
            "a route that names the file it wants must be obeyed; the attachment is the default");
    }

    // ── Refusals ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Download_RefusesADeclaredOversizeBeforeTransferring()
    {
        var transferred = false;
        var exchange = new Exchange(new Message(null));
        exchange.In.Headers[TelegramHeaders.AttachmentFileId] = "big-file";

        var client = Client(File(size: 5_000), new byte[5_000], onDownload: _ => transferred = true);

        var act = async () => await RunAsync(Endpoint("&maxDownloadBytes=1024"), client, exchange);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ceiling*");
        transferred.Should().BeFalse(
            "when Telegram states the size up front there is no reason to start a transfer that " +
            "is going to be rejected at the end of it");
    }

    [Fact]
    public async Task Download_StopsATransferThatPassesTheCeilingWithNoDeclaredSize()
    {
        var exchange = new Exchange(new Message(null));
        exchange.In.Headers[TelegramHeaders.AttachmentFileId] = "undeclared-file";

        // file_size is optional in the Bot API. Trusting it alone would leave "how much memory
        // does one message cost" to the sender.
        var client = Client(File(size: null), new byte[4096]);

        var act = async () => await RunAsync(Endpoint("&maxDownloadBytes=1024"), client, exchange);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ceiling*");
    }

    [Fact]
    public async Task Download_WithNoFileIdAnywhere_SaysWhereToPutOne()
    {
        var exchange = new Exchange(new Message(null));

        var act = async () => await RunAsync(Endpoint(), Client(File(), []), exchange);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{TelegramHeaders.AttachmentFileId}*");
    }

    [Fact]
    public async Task Download_WithoutAFilePath_SaysTheFileIsUnreachable()
    {
        var exchange = new Exchange(new Message(null));
        exchange.In.Headers[TelegramHeaders.AttachmentFileId] = "expired-file";

        // getFile answers without a file_path for a file a bot may not download — expired, or
        // over the Bot API's own 20 MB ceiling. Failing here beats downloading from "null".
        var act = async () => await RunAsync(Endpoint(), Client(File(path: ""), []), exchange);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*file_path*");
    }

    // ── URI and DSL ───────────────────────────────────────────────────────────

    [Fact]
    public void Dsl_CarriesTheDownloadOptions()
    {
        var uri = TgDsl.Download(Token).FileId("abc").MaxDownloadBytes(2048).Build();

        uri.Should().StartWith("telegram://download?");
        uri.Should().Contain("fileId=abc").And.Contain("maxDownloadBytes=2048");

        var parsed = EndpointUriParser.Parse(uri);
        var options = new TelegramEndpointOptions();
        options.BindFromUri(parsed.RawParameters);

        ((TelegramEndpoint)new TelegramComponent().CreateEndpoint(parsed)).Mode.Should().Be("download");
        options.FileId.Should().Be("abc");
        options.MaxDownloadBytes.Should().Be(2048);
    }

    [Fact]
    public void Options_DefaultCeilingMatchesWhatTelegramItselfAllows()
    {
        // 20 MB is the Bot API's limit on what a bot may download, so the default refuses
        // exactly what Telegram would refuse — and no route has to know the number.
        new TelegramEndpointOptions().MaxDownloadBytes.Should().Be(20L * 1024 * 1024);
    }

    [Fact]
    public void Options_ANegativeCeilingIsRejectedAtStart()
    {
        var act = () => new TelegramComponent()
            .CreateEndpoint(EndpointUriParser.Parse($"telegram://download?token={Token}&maxDownloadBytes=-1"));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

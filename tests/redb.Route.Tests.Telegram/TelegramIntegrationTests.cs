using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;
using redb.Route.Telegram;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Message = redb.Route.Core.Message;
using TgMessage = Telegram.Bot.Types.Message;

namespace redb.Route.Tests.Telegram;

/// <summary>
/// In-process E2E tests for the Telegram connector — no network, no real bot. The producer runs
/// against a mocked <see cref="ITelegramBotClient"/> (send calls are extension methods over
/// <c>SendRequest</c>, so NSubstitute can intercept them); the consumer is driven through its
/// internal test seams (<c>AttachToLiveBot=false</c> + <c>DeliverMessageForTest</c> /
/// <c>RaiseErrorForTest</c>). Each test proves one of the review's required fixes.
/// </summary>
public sealed class TelegramIntegrationTests
{
    private const string Token = "123456:AAAA-Test_Token";

    private static TelegramEndpoint Endpoint(string mode, string extra = "") =>
        (TelegramEndpoint)new TelegramComponent()
            .CreateEndpoint(EndpointUriParser.Parse($"telegram://{mode}?token={Token}{extra}"));

    private static IProcessor Recorder(Func<IExchange, Task> onEach)
    {
        var p = Substitute.For<IProcessor>();
        p.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(call => onEach(call.Arg<IExchange>()));
        return p;
    }

    private static TgMessage SentMessage(int id = 100) => new()
    {
        Id = id,
        Chat = new Chat { Id = 1, Type = ChatType.Private }
    };

    private static TgMessage IncomingText(string text, long chatId = 123) => new()
    {
        Id = 10,
        Text = text,
        Chat = new Chat { Id = chatId, Type = ChatType.Private },
        From = new User { Id = 7, FirstName = "Ann", Username = "ann" }
    };

    // ── Consumer: roundtrip ───────────────────────────────────────────

    [Fact]
    public async Task Consumer_Roundtrip_PopulatesBodyAndHeaders()
    {
        object? body = null;
        long? chatId = null;
        var consumer = (TelegramConsumer)Endpoint("receive").CreateConsumer(Recorder(ex =>
        {
            body = ex.In.Body;
            chatId = ex.In.Headers.TryGetValue(TelegramHeaders.ChatId, out var v) ? (long?)v : null;
            return Task.CompletedTask;
        }));
        consumer.AttachToLiveBot = false;
        await consumer.Start();
        try
        {
            await consumer.DeliverMessageForTest(IncomingText("hello"), UpdateType.Message, CancellationToken.None);
            body.Should().Be("hello");
            chatId.Should().Be(123L);
        }
        finally { await consumer.Stop(); }
    }

    // ── Consumer: graceful drain (fix §3) ─────────────────────────────

    [Fact]
    public async Task Consumer_Stop_DrainsInFlight_NoHang()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = false;

        var consumer = (TelegramConsumer)Endpoint("receive").CreateConsumer(Recorder(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            completed = true;
        }));
        consumer.AttachToLiveBot = false;
        await consumer.Start();

        var delivery = Task.Run(() =>
            consumer.DeliverMessageForTest(IncomingText("slow"), UpdateType.Message, CancellationToken.None));

        await entered.Task; // in-flight incremented, processor blocked

        var sw = Stopwatch.StartNew();
        var stop = consumer.Stop();

        await Task.Delay(150);
        stop.IsCompleted.Should().BeFalse("Stop must wait for the in-flight message to drain");

        release.SetResult();     // let the processor finish
        await stop;
        await delivery;
        sw.Stop();

        completed.Should().BeTrue("the in-flight message was processed to completion, not force-cancelled");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "drain must not burn the full 30s DrainTimeout — handlers detach in OnStopAccepting");
    }

    // ── Consumer: error policy = at-most-once (fix §4) ────────────────

    [Fact]
    public async Task Consumer_ProcessorThrows_MessageDropped_StreamSurvives()
    {
        var attempts = 0;
        var secondDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var consumer = (TelegramConsumer)Endpoint("receive").CreateConsumer(Recorder(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new InvalidOperationException("boom");
            secondDone.TrySetResult();
            return Task.CompletedTask;
        }));
        consumer.AttachToLiveBot = false;
        await consumer.Start();
        try
        {
            // First update: the processor throws — the consumer must swallow it (at-most-once, no rethrow).
            var act = async () =>
                await consumer.DeliverMessageForTest(IncomingText("bad"), UpdateType.Message, CancellationToken.None);
            await act.Should().NotThrowAsync("a processor failure is logged as a drop, never rethrown into the poll loop");

            // Second update proves the stream is still alive after the drop.
            await consumer.DeliverMessageForTest(IncomingText("good"), UpdateType.Message, CancellationToken.None);
            await secondDone.Task;
            attempts.Should().Be(2);
        }
        finally { await consumer.Stop(); }
    }

    // ── Consumer: fatal vs transient polling error (fix §4) ───────────

    [Fact]
    public async Task Consumer_FatalPollingError_IsDetected()
    {
        var consumer = (TelegramConsumer)Endpoint("receive").CreateConsumer(Recorder(_ => Task.CompletedTask));
        consumer.AttachToLiveBot = false;
        await consumer.Start();
        try
        {
            await consumer.RaiseErrorForTest(
                new ApiRequestException("Unauthorized: bot token revoked", 401), HandleErrorSource.PollingError);
            consumer.FatalPollingErrorDetected.Should().BeTrue();
        }
        finally { await consumer.Stop(); }
    }

    [Fact]
    public async Task Consumer_TransientPollingError_IsNotFatal()
    {
        var consumer = (TelegramConsumer)Endpoint("receive").CreateConsumer(Recorder(_ => Task.CompletedTask));
        consumer.AttachToLiveBot = false;
        await consumer.Start();
        try
        {
            await consumer.RaiseErrorForTest(new HttpRequestException("network blip"), HandleErrorSource.PollingError);
            consumer.FatalPollingErrorDetected.Should().BeFalse();
        }
        finally { await consumer.Stop(); }
    }

    // ── Consumer: concurrency via .Threads(N) (fix §6) ────────────────

    [Fact]
    public async Task Consumer_ThreadsProcessor_ProcessesUpToPoolSizeInParallel()
    {
        const int pool = 5, total = 20;
        await using var context = new RouteContext();

        var current = 0; var max = 0; var maxLock = new object();

        var body = Recorder(async _ =>
        {
            var c = Interlocked.Increment(ref current);
            lock (maxLock) { if (c > max) max = c; }
            await Task.Delay(50);
            Interlocked.Decrement(ref current);
        });
        var threads = new ThreadsProcessor(body, pool, maxQueueSize: 0, context);

        var consumer = (TelegramConsumer)Endpoint("receive").CreateConsumer(threads);
        consumer.AttachToLiveBot = false;
        await consumer.Start();
        try
        {
            // A serial source (mirrors the single getUpdates stream): each hand-off returns fast, so the
            // pool still saturates to N concurrent workers.
            for (var i = 0; i < total; i++)
                await consumer.DeliverMessageForTest(IncomingText($"m{i}"), UpdateType.Message, CancellationToken.None);

            // Drain the pool deterministically (completes the backlog, then joins the workers).
            await ((IRouteLifecycleListener)threads).OnContextStopping(context, CancellationToken.None);

            threads.ProcessedCount.Should().Be(total, "no message is lost across the pool");
            max.Should().Be(pool, "the pool saturates to exactly N concurrent workers via .Threads(N)");
        }
        finally { await consumer.Stop(); }
    }

    // ── Producer: 429 RetryAfter is honoured (fix §5) ─────────────────

    [Fact]
    public async Task Producer_RateLimited429_WaitsRetryAfter_ThenRetries()
    {
        var mock = Substitute.For<ITelegramBotClient>();
        var calls = 0;
        mock.SendRequest(Arg.Any<IRequest<TgMessage>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new ApiRequestException("Too Many Requests", 429, new ResponseParameters { RetryAfter = 1 });
                return Task.FromResult(SentMessage());
            });

        var producer = (TelegramProducer)Endpoint("send", "&chatId=555").CreateProducer();
        producer.UseTestClient(mock);
        await producer.Start();
        try
        {
            var sw = Stopwatch.StartNew();
            var exchange = new Exchange(new Message("hi"));
            await producer.Process(exchange);
            sw.Stop();

            calls.Should().Be(2, "the producer must retry once after a 429");
            sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900),
                "the producer must wait the retry_after hint (~1s) before retrying");
            exchange.In.Headers.Should().ContainKey(TelegramHeaders.SentMessageId);
        }
        finally { await producer.Stop(); }
    }

    // ── Producer: non-429 errors propagate (fix §4/§5) ────────────────

    [Fact]
    public async Task Producer_NonRateLimitError_Propagates()
    {
        var mock = Substitute.For<ITelegramBotClient>();
        mock.SendRequest(Arg.Any<IRequest<TgMessage>>(), Arg.Any<CancellationToken>())
            .Returns<Task<TgMessage>>(_ => throw new ApiRequestException("Bad Request: chat not found", 400));

        var producer = (TelegramProducer)Endpoint("send", "&chatId=555").CreateProducer();
        producer.UseTestClient(mock);
        await producer.Start();
        try
        {
            var act = async () => await producer.Process(new Exchange(new Message("hi")));
            await act.Should().ThrowAsync<ApiRequestException>().Where(e => e.ErrorCode == 400);
        }
        finally { await producer.Stop(); }
    }

    // ── Producer: chatId precedence header > option (fix §5) ──────────

    [Fact]
    public async Task Producer_ChatId_HeaderOverridesOption()
    {
        var mock = Substitute.For<ITelegramBotClient>();
        long? sentTo = null;
        mock.SendRequest(Arg.Any<IRequest<TgMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var req = (IChatTargetable)ci.Arg<IRequest<TgMessage>>();
                sentTo = req.ChatId.Identifier;
                return Task.FromResult(SentMessage());
            });

        var producer = (TelegramProducer)Endpoint("send", "&chatId=111").CreateProducer();
        producer.UseTestClient(mock);
        await producer.Start();
        try
        {
            var exchange = new Exchange(new Message("hi"));
            exchange.In.Headers[TelegramHeaders.ChatId] = 999L; // header overrides option
            await producer.Process(exchange);

            sentTo.Should().Be(999L, "the telegram.chatId header takes precedence over the URI option");
        }
        finally { await producer.Stop(); }
    }

    [Fact]
    public async Task Producer_ChatId_FallsBackToOption()
    {
        var mock = Substitute.For<ITelegramBotClient>();
        long? sentTo = null;
        mock.SendRequest(Arg.Any<IRequest<TgMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var req = (IChatTargetable)ci.Arg<IRequest<TgMessage>>();
                sentTo = req.ChatId.Identifier;
                return Task.FromResult(SentMessage());
            });

        var producer = (TelegramProducer)Endpoint("send", "&chatId=111").CreateProducer();
        producer.UseTestClient(mock);
        await producer.Start();
        try
        {
            await producer.Process(new Exchange(new Message("hi")));
            sentTo.Should().Be(111L, "with no header the URI chatId option is used");
        }
        finally { await producer.Stop(); }
    }

    [Fact]
    public async Task Producer_NoChatId_Throws()
    {
        var mock = Substitute.For<ITelegramBotClient>();
        var producer = (TelegramProducer)Endpoint("send").CreateProducer(); // no chatId option
        producer.UseTestClient(mock);
        await producer.Start();
        try
        {
            var act = async () => await producer.Process(new Exchange(new Message("hi")));
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*chatId*");
        }
        finally { await producer.Stop(); }
    }

    // ── Producer: records outbound statistics (fix §5) ────────────────

    [Fact]
    public async Task Producer_RecordsMessageOut()
    {
        var mock = Substitute.For<ITelegramBotClient>();
        mock.SendRequest(Arg.Any<IRequest<TgMessage>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SentMessage()));

        var endpoint = Endpoint("send", "&chatId=555");
        var producer = (TelegramProducer)endpoint.CreateProducer();
        producer.UseTestClient(mock);
        await producer.Start();
        try
        {
            await producer.Process(new Exchange(new Message("hi")));
            await producer.Process(new Exchange(new Message("hi2")));
            endpoint.MessagesOut.Should().Be(2, "the producer records each successful send on the endpoint");
        }
        finally { await producer.Stop(); }
    }
}

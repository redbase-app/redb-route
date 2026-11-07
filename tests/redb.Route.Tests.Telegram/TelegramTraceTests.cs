using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Telegram;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Message = redb.Route.Core.Message;
using TgMessage = Telegram.Bot.Types.Message;

namespace redb.Route.Tests.Telegram;

/// <summary>
/// An update carries no trace context: the consumer opens a root span per routed update, never a child of the activity
/// the polling loop holds, with <c>redb.route.endpoint</c>; a failed route marks it red, whether the failure stays on
/// the exchange or escapes it. The producer's span marks a failed send red. <c>EnableTelemetry=false</c> opens none of
/// these spans. In process, through the connector's test seams; each test uses a chat of its own.
/// </summary>
public sealed class TelegramTraceTests
{
    private const string Token = "123456:AAAA-Test_Token";
    private readonly long _chat = Random.Shared.NextInt64(1_000_000, long.MaxValue);

    private RouteTelemetryProbe Spans(ActivityKind kind)
        => new(a => a.Kind == kind && Equals(a.GetTagItem("messaging.telegram.chat.id"), _chat));

    private TgMessage Incoming() => new()
    {
        Id = 10,
        Text = "hello",
        Chat = new Chat { Id = _chat, Type = ChatType.Private },
        From = new User { Id = 7, FirstName = "Ann" },
    };

    /// <summary>A started consumer in a context of its own, driven through the delivery seam.</summary>
    private static async Task<(RouteContext Context, TelegramConsumer Consumer)> StartConsumer(
        Func<IExchange, Task> route, bool telemetry = true)
    {
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new TelegramComponent());
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(call => route(call.Arg<IExchange>()));
        var consumer = (TelegramConsumer)ctx.GetEndpoint($"telegram://receive?token={Token}").CreateConsumer(processor);
        consumer.AttachToLiveBot = false;
        await consumer.Start();
        return (ctx, consumer);
    }

    [Fact]
    public async Task Each_update_opens_a_root_span_even_under_an_ambient_activity()
    {
        using var probe = Spans(ActivityKind.Consumer);
        var (ctx, consumer) = await StartConsumer(_ => Task.CompletedTask);
        try
        {
            using (new Activity("polling loop").SetIdFormat(ActivityIdFormat.W3C).Start())
            {
                await consumer.DeliverMessageForTest(Incoming(), UpdateType.Message, CancellationToken.None);
                await consumer.DeliverMessageForTest(Incoming(), UpdateType.Message, CancellationToken.None);
            }
        }
        finally
        {
            await consumer.Stop();
            await ctx.DisposeAsync();
        }

        probe.Activities.Should().HaveCount(2);
        probe.Activities.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "an update carries no context, so its span starts a trace rather than joining the loop's");
        RouteTelemetryProbe.Tag(probe.Activities[0], RouteTelemetryProbe.EndpointTag).Should().StartWith("telegram:");
    }

    [Fact]
    public async Task A_failed_route_marks_the_span_red_whether_the_failure_stays_on_the_exchange_or_escapes()
    {
        using var probe = Spans(ActivityKind.Consumer);
        var calls = 0;
        var (ctx, consumer) = await StartConsumer(e =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                e.Exception = new InvalidOperationException("route failed");   // left on the exchange
                return Task.CompletedTask;
            }
            throw new InvalidOperationException("escaped");
        });
        try
        {
            await consumer.DeliverMessageForTest(Incoming(), UpdateType.Message, CancellationToken.None);
            await consumer.DeliverMessageForTest(Incoming(), UpdateType.Message, CancellationToken.None);
        }
        finally
        {
            await consumer.Stop();
            await ctx.DisposeAsync();
        }

        probe.Activities.Should().HaveCount(2).And.OnlyContain(a => a.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Tracing_off_opens_no_span_on_either_side()
    {
        using var probe = new RouteTelemetryProbe(a => Equals(a.GetTagItem("messaging.telegram.chat.id"), _chat));
        var (ctx, consumer) = await StartConsumer(_ => Task.CompletedTask, telemetry: false);
        using var outer = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var sends = new RouteTelemetryProbe(a => a.Kind == ActivityKind.Producer && a.TraceId == outer.TraceId);
        try
        {
            await consumer.DeliverMessageForTest(Incoming(), UpdateType.Message, CancellationToken.None);

            var bot = Substitute.For<ITelegramBotClient>();
            bot.SendRequest(Arg.Any<IRequest<TgMessage>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new TgMessage { Id = 1, Chat = new Chat { Id = _chat, Type = ChatType.Private } }));
            var producer = (TelegramProducer)ctx.GetEndpoint($"telegram://send?token={Token}&chatId={_chat}").CreateProducer();
            producer.UseTestClient(bot);
            await producer.Start();
            await producer.Process(new Exchange(new Message("hi")));
            await producer.Stop();
        }
        finally
        {
            await consumer.Stop();
            await ctx.DisposeAsync();
        }

        probe.Activities.Should().BeEmpty();
        sends.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_send_marks_the_producer_span_red()
    {
        using var outer = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var sends = new RouteTelemetryProbe(a => a.Kind == ActivityKind.Producer && a.TraceId == outer.TraceId);
        await using var ctx = new RouteContext();
        ctx.AddComponent(new TelegramComponent());
        var bot = Substitute.For<ITelegramBotClient>();
        bot.SendRequest(Arg.Any<IRequest<TgMessage>>(), Arg.Any<CancellationToken>())
            .Returns<Task<TgMessage>>(_ => throw new ApiRequestException("Bad Request: chat not found", 400));
        var producer = (TelegramProducer)ctx.GetEndpoint($"telegram://send?token={Token}&chatId={_chat}").CreateProducer();
        producer.UseTestClient(bot);
        await producer.Start();

        var act = () => producer.Process(new Exchange(new Message("hi")));

        await act.Should().ThrowAsync<ApiRequestException>();
        await producer.Stop();
        var send = sends.Activities.Should().ContainSingle().Subject;
        send.Status.Should().Be(ActivityStatusCode.Error);
        RouteTelemetryProbe.Tag(send, RouteTelemetryProbe.EndpointTag).Should().StartWith("telegram:");
    }
}

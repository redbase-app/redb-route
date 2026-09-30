using System.Diagnostics;
using System.Text;
using MailKit.Net.Smtp;
using MimeKit;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Mail;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.Mail;

/// <summary>
/// A mailbox message carries no trace context the connector reads: the IMAP and POP3 consumers open a root span per
/// routed message, never a child of the activity the poll loop inherited from whoever started it, and none for an empty
/// mailbox. A failed route marks it red. The SMTP producer marks a failed send red. <c>EnableTelemetry=false</c> opens
/// none of these spans. Each test has a mailbox of its own and reads only the spans of that endpoint.
/// Expects GreenMail at SMTP:3025, IMAP:3143, POP3:3110, API:8080.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MailTraceTests
{
    private static readonly HttpClient Http = new();
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _user = $"trace-{Guid.NewGuid():N}";

    public static TheoryData<string> Protocols => new() { "imap", "pop3" };

    private string ConsumerUri(string protocol) =>
        $"{protocol}://localhost?port={(protocol == "imap" ? 3143 : 3110)}&username={_user}&password=secret" +
        "&security=None&delay=300&fetchFilter=All&postProcess=Delete";

    private RouteTelemetryProbe Spans(ActivityKind kind)
        => new(a => a.Kind == kind && RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_user, StringComparison.Ordinal) == true);

    private static async Task Provision(string login)
    {
        using var content = new StringContent(
            $"{{\"email\":\"{login}@localhost\",\"login\":\"{login}\",\"password\":\"secret\"}}", Encoding.UTF8, "application/json");
        using var _ = await Http.PostAsync("http://localhost:8080/api/user", content);
    }

    private async Task Deliver(string subject)
    {
        await Provision("sender");
        await Provision(_user);
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("sender", "sender@localhost"));
        mime.To.Add(new MailboxAddress(_user, $"{_user}@localhost"));
        mime.Subject = subject;
        mime.Body = new TextPart("plain") { Text = "payload" };
        using var client = new SmtpClient();
        await client.ConnectAsync("localhost", 3025, MailKit.Security.SecureSocketOptions.None);
        await client.AuthenticateAsync("sender", "secret");
        await client.SendAsync(mime);
        await client.DisconnectAsync(true);
    }

    private async Task<RouteContext> StartConsumer(string protocol, Action<IExchange> step, bool telemetry = true)
    {
        await Provision(_user);
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(protocol == "imap" ? new ImapComponent() : new Pop3Component());
        ctx.AddRoutes(r => r.From(ConsumerUri(protocol)).Process(step));
        using (new Activity("host startup").SetIdFormat(ActivityIdFormat.W3C).Start())
            await ctx.Start();
        return ctx;
    }

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(100);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Each_message_opens_a_root_span_even_under_an_ambient_activity(string protocol)
    {
        await Deliver("a");
        await Deliver("b");
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(protocol, _ => { }))
            await Until(() => probe.Activities.Count >= 2);

        probe.Activities.Should().HaveCount(2, "one span per routed message");
        probe.Activities.Should().OnlyContain(a => a.ParentSpanId == default(ActivitySpanId),
            "a mailbox message carries no context the connector reads, so its span starts a trace");
        probe.Activities[0].GetTagItem("messaging.system").Should().Be(protocol);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task An_empty_mailbox_opens_no_span(string protocol)
    {
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(protocol, _ => { }))
            await Task.Delay(1500);   // several polls of an empty mailbox

        probe.Activities.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_failed_route_marks_the_span_red(string protocol)
    {
        await Deliver("fail");
        using var probe = Spans(ActivityKind.Consumer);

        await using (await StartConsumer(protocol, _ => throw new InvalidOperationException("route failed")))
            await Until(() => probe.Activities.Count >= 1);

        probe.Activities.Should().NotBeEmpty().And.OnlyContain(a => a.Status == ActivityStatusCode.Error);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Tracing_off_opens_no_span(string protocol)
    {
        await Deliver("off");
        using var probe = new RouteTelemetryProbe(a =>
            RouteTelemetryProbe.Tag(a, RouteTelemetryProbe.EndpointTag)?.Contains(_user, StringComparison.Ordinal) == true);
        var routed = new TaskCompletionSource();

        await using (await StartConsumer(protocol, _ => routed.TrySetResult(), telemetry: false))
            (await Task.WhenAny(routed.Task, Task.Delay(Wait))).Should().Be(routed.Task);

        probe.Activities.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_send_marks_the_producer_span_red_and_tracing_off_opens_none(bool telemetry)
    {
        using var probe = Spans(ActivityKind.Producer);
        await using var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new SmtpComponent());
        await ctx.Start();
        using var template = new ProducerTemplate(ctx);
        template.Start();
        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers[MailHeaders.To] = "nobody@localhost";

        // Nothing listens on port 1: the connection fails inside the send.
        var act = () => template.SendAsync(
            $"smtp://localhost?port=1&username={_user}&password=secret&security=None&from={_user}@localhost", exchange);

        await act.Should().ThrowAsync<Exception>();
        if (telemetry)
            probe.Activities.Should().ContainSingle().Which.Status.Should().Be(ActivityStatusCode.Error);
        else
            probe.Activities.Should().BeEmpty();
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using FluentAssertions;
using redb.Route.Core;
using redb.Route.Soap;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// Stopping one route on a shared listener must wait for the calls it is already serving. It used to
/// unregister the route and ask the listener to stop when empty — and when another route still holds
/// the port, the listener stays up, so nobody waited and a deploy cut the call mid-pipeline. Reported
/// by the AS4 agent 2026-09-25; the drain is the engine's <see cref="InflightDrainGuard"/>.
/// </summary>
public class SoapStopDrainsInflightTests
{
    private static int FreePort() => global::redb.Route.Tests.Shared.TestPorts.Next();

    [Fact]
    public async Task Stop_waits_for_a_call_already_in_the_pipeline_while_the_port_stays_busy()
    {
        var port = FreePort();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());

        var slow = ctx.GetEndpoint(SoapDsl.Listen("/slow").Host("127.0.0.1").Port(port).Build())
            .CreateConsumer(new redb.Route.Processors.DelegateProcessor(async (e, ct) =>
            {
                entered.TrySetResult();
                await release.Task;
                finished = true;
                e.In.Body = "<Ack xmlns=\"urn:t\">ok</Ack>";
            }));
        // A second route keeps the listener alive after the first one is stopped — the case where
        // nothing used to wait.
        var other = ctx.GetEndpoint(SoapDsl.Listen("/other").Host("127.0.0.1").Port(port).Build())
            .CreateConsumer(new redb.Route.Processors.DelegateProcessor(_ => { }));

        await slow.Start();
        await other.Start();

        using var http = new HttpClient();
        var content = new ByteArrayContent(SoapEnvelope.Build("<Op xmlns=\"urn:t\"/>", SoapVersion.Soap12));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(SoapEnvelope.ContentType(SoapVersion.Soap12, "urn:t/Op"));
        var call = http.PostAsync($"http://127.0.0.1:{port}/slow", content);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = slow.Stop();
        (await Task.WhenAny(stop, Task.Delay(500)) == stop)
            .Should().BeFalse("Stop must not return while a call of this route is still running");

        release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        finished.Should().BeTrue();

        (await call).StatusCode.Should().Be(HttpStatusCode.OK);
        await other.Stop();
    }
}

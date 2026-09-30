using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Processors;

namespace redb.Route.Tests.Http;

/// <summary>
/// Stopping one route on a shared listener must wait for the requests it is already serving. Until
/// now it only unregistered the route and asked the listener to stop when empty — and when another
/// route still holds the port, the listener stays up, so nobody waits: a deploy that removes one
/// route cuts its in-flight requests mid-pipeline while the port keeps serving. Reported by the AS4
/// agent 2026-09-25; the drain itself is the engine's <see cref="InflightDrainGuard"/>, already used
/// by every broker consumer and by SignalR and WebSocket.
/// </summary>
public class HttpStopDrainsInflightTests
{
    private static int FreePort() => global::redb.Route.Tests.Shared.TestPorts.Next();

    [Fact]
    public async Task Stop_waits_for_a_request_already_in_the_pipeline_while_the_port_stays_busy()
    {
        var port = FreePort();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;

        await using var context = new RouteContext();
        context.AddComponent(new HttpComponent { ServerManager = new SharedHttpServerManager() });

        // Two routes on one port: stopping the first leaves the listener up, which is exactly the
        // case where nothing used to wait.
        var slow = (HttpConsumer)context.GetEndpoint($"http://127.0.0.1:{port}/slow")
            .CreateConsumer(new DelegateProcessor(async (e, ct) =>
            {
                entered.TrySetResult();
                await release.Task;
                finished = true;
                e.In.Body = "done";
            }));
        var other = context.GetEndpoint($"http://127.0.0.1:{port}/other")
            .CreateConsumer(new DelegateProcessor(_ => { }));

        await slow.Start();
        await other.Start();

        using var client = new HttpClient();
        var call = client.GetAsync($"http://127.0.0.1:{port}/slow");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = slow.Stop();
        var stoppedEarly = await Task.WhenAny(stop, Task.Delay(500)) == stop;
        stoppedEarly.Should().BeFalse("Stop must not return while a request of this route is still running");

        release.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        finished.Should().BeTrue("the request finished before Stop returned");

        (await call).StatusCode.Should().Be(HttpStatusCode.OK);
        await other.Stop();
    }

    [Fact]
    public async Task Stop_returns_promptly_when_nothing_is_in_flight()
    {
        var port = FreePort();
        await using var context = new RouteContext();
        context.AddComponent(new HttpComponent { ServerManager = new SharedHttpServerManager() });

        var consumer = context.GetEndpoint($"http://127.0.0.1:{port}/idle")
            .CreateConsumer(new DelegateProcessor(_ => { }));
        await consumer.Start();

        var stop = consumer.Stop();
        var completed = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5))) == stop;

        completed.Should().BeTrue("an idle route has nothing to wait for");
        await stop;
    }
}

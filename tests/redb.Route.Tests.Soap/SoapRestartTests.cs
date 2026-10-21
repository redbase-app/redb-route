using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Soap;
using SoapDsl = redb.Route.Soap.Fluent.Soap;

namespace redb.Route.Tests.Soap;

/// <summary>
/// A SOAP consumer and producer keep working after <c>Stop()</c> and <c>Start()</c> of the same route context: the stop
/// gives the listener path back, the start registers it again on the cached endpoint.
/// </summary>
public class SoapRestartTests
{
    [Fact]
    public async Task Consumer_and_producer_work_after_the_context_is_stopped_and_started_again()
    {
        var port = global::redb.Route.Tests.Shared.TestPorts.Next();
        var received = 0;

        await using var ctx = new RouteContext();
        ctx.AddComponent(new SoapComponent());
        ctx.AddRoutes(r =>
        {
            r.From(SoapDsl.Listen("/svc").Host("127.0.0.1").Port(port)).Process(e =>
            {
                Interlocked.Increment(ref received);
                e.In.Body = "<Reply xmlns=\"urn:test\"><status>ok</status></Reply>";
            });
            r.From("direct://call").To(SoapDsl.Call($"http://127.0.0.1:{port}/svc").Operation("Ping"));
        });

        async Task CheckAsync(int round)
        {
            var producer = ctx.GetEndpoint("direct://call").CreateProducer();
            await producer.Start();
            var exchange = new Exchange(new Message("<Ping xmlns=\"urn:test\"/>"));
            await producer.Process(exchange);
            exchange.Exception.Should().BeNull("SOAP-вызов после рестарта доходит");
            exchange.Out!.Body!.ToString().Should().Contain("ok");
            Volatile.Read(ref received).Should().Be(round);
        }

        await ctx.Start();
        await CheckAsync(1);

        await ctx.Stop();
        await ctx.Start();
        await CheckAsync(2);
    }
}

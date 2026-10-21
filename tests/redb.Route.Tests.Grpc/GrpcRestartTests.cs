using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc;

namespace redb.Route.Tests.Grpc;

/// <summary>
/// A gRPC consumer and producer keep working after <c>Stop()</c> and <c>Start()</c> of the same route context: the stop
/// gives the listener back, the start takes it again, and the producer of the cached endpoint still calls.
/// </summary>
public sealed class GrpcRestartTests
{
    private static string? Text(object? body) => body is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : body?.ToString();

    [Fact]
    public async Task Consumer_and_producer_work_after_the_context_is_stopped_and_started_again()
    {
        var port = global::redb.Route.Tests.Shared.TestPorts.Next();
        await using var ctx = new RouteContext();
        ctx.AddComponent(new GrpcComponent());
        ctx.AddRoutes(r => r.From($"grpc://127.0.0.1:{port}?inOut=true").Process(e => e.Out = new Message($"re:{Text(e.In.Body)}")));
        ctx.AddRoutes(r => r.From("direct://call").To($"grpc://127.0.0.1:{port}?plaintext=true"));

        async Task CheckAsync(string body)
        {
            var producer = ctx.GetEndpoint("direct://call").CreateProducer();
            await producer.Start();
            var exchange = new Exchange(new Message(body));
            await producer.Process(exchange);
            exchange.Exception.Should().BeNull("gRPC-вызов после рестарта доходит");
            var reply = (exchange.HasOut ? exchange.Out! : exchange.In).Body;
            Text(reply).Should().Be($"re:{body}");
        }

        await ctx.Start();
        await CheckAsync("first");

        await ctx.Stop();
        await ctx.Start();
        await CheckAsync("second");
    }
}

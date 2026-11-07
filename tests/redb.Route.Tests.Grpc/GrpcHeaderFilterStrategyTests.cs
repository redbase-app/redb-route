using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc;

namespace redb.Route.Tests.Grpc;

/// <summary>
/// <c>headerFilterStrategy=#name</c> on gRPC endpoints: on a producer it decides which exchange headers go out as call
/// metadata and envelope headers and which reply envelope headers come back, on a consumer which request headers
/// come in and which go out in the reply envelope. The reserved prefixes and the consumer's redbGrpc.* call metadata
/// stay outside the strategy.
/// </summary>
public sealed class GrpcHeaderFilterStrategyTests
{
    private static async Task<(Dictionary<string, object?> Request, Dictionary<string, object?> Reply)> Call(
        string producerOptions, string consumerOptions)
    {
        var port = global::redb.Route.Tests.Shared.TestPorts.Next();
        var received = new TaskCompletionSource<Dictionary<string, object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = new RouteContext();
        ctx.AddComponent(new GrpcComponent());
        ctx.AddToRegistry("correlationOnly", new DefaultHeaderFilterStrategy { OutFilter = ["Correlation-ID"], FilterOnMatch = false });
        ctx.AddToRegistry("noSecrets", new DefaultHeaderFilterStrategy { InFilter = ["Authorization"] });
        ctx.AddRoutes(r => r.From($"grpc://127.0.0.1:{port}?inOut=true{consumerOptions}").Process(e =>
        {
            received.TrySetResult(new Dictionary<string, object?>(e.In.Headers, StringComparer.OrdinalIgnoreCase));
            e.Out = new Message("reply");
            e.Out.Headers["Correlation-ID"] = "c-42";
            e.Out.Headers["X-Reply"] = "r1";
            e.Out.Headers["Authorization"] = "Bearer secret";
        }));
        await ctx.Start();

        var producer = ctx.GetEndpoint($"grpc://127.0.0.1:{port}?plaintext=true{producerOptions}").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("body"));
        exchange.In.Headers["Correlation-ID"] = "c-42";
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["Authorization"] = "Bearer secret";
        exchange.In.Headers["redbHttp.Method"] = "DELETE";
        await producer.Process(exchange);
        await producer.Stop();

        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var reply = new Dictionary<string, object?>((exchange.HasOut ? exchange.Out! : exchange.In).Headers,
            StringComparer.OrdinalIgnoreCase);
        return (request, reply);
    }

    [Fact]
    public async Task OnAProducer_AWhitelistSendsOnlyTheNamedHeader()
    {
        var (request, _) = await Call("&headerFilterStrategy=#correlationOnly", "");

        request["Correlation-ID"].Should().Be("c-42");
        request.Should().NotContainKey("X-Corr");
        request.Should().NotContainKey("Authorization");
        request.Should().ContainKey(GrpcHeaders.Route, "the consumer's own call metadata is set whatever the strategy says");
    }

    [Fact]
    public async Task OnAConsumer_TheStrategyDecidesWhatComesIn_AndTheReservedPrefixesStayOut()
    {
        var (request, _) = await Call("", "&headerFilterStrategy=#noSecrets");

        request["X-Corr"].Should().Be("c1");
        request.Should().NotContainKey("Authorization", "the consumer's strategy drops it on the way in");
        request.Should().NotContainKey("redbHttp.Method",
            "a reserved prefix is refused whatever the strategy says, unless allowClientReservedHeaders");
    }

    [Fact]
    public async Task OnAReplyingConsumer_TheStrategyDecidesWhatTheReplyCarries()
    {
        var (_, reply) = await Call("", "&headerFilterStrategy=#correlationOnly");

        reply["Correlation-ID"].Should().Be("c-42");
        reply.Should().NotContainKey("X-Reply");
        reply.Should().NotContainKey("Authorization");
    }

    [Fact]
    public async Task OnAProducer_TheStrategyDecidesWhatComesBackWithTheReply()
    {
        var (_, reply) = await Call("&headerFilterStrategy=#noSecrets", "");

        reply["X-Reply"].Should().Be("r1");
        reply.Should().NotContainKey("Authorization", "the producer's strategy drops it on the way back in");
    }
}

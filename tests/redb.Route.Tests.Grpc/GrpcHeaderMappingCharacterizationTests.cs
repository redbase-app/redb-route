using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc;

namespace redb.Route.Tests.Grpc;

/// <summary>
/// What crosses a gRPC call today, all four points: pinned before the header filter strategy
/// (docs/V4/headers/01-HEADER-FILTER-STRATEGY.md) takes the rule over, so the transport's default keeps it exactly.
/// The producer sends every exchange header but its own <c>redbGrpc.*</c>, the reserved <c>grpc-*</c> keys and null
/// values as request metadata (keys lowercased by gRPC); the consumer takes every request metadata entry but the
/// pseudo-headers, <c>grpc-*</c> and the reserved keys (the control prefixes of every transport: <c>redbHttp.</c>,
/// <c>redbSoap.</c>...), so unlike the brokers a control header cannot come in this way. The consumer answers with every exchange header but
/// <c>redbGrpc.*</c> as response metadata, <c>redbGrpc.Trailer.X</c> as trailer <c>X</c>; the producer takes the
/// response metadata back.
/// </summary>
public sealed class GrpcHeaderMappingCharacterizationTests
{
    [Fact]
    public async Task RequestAndResponse_TheMetadataThatCrosses()
    {
        var port = global::redb.Route.Tests.Shared.TestPorts.Next();
        var received = new TaskCompletionSource<Dictionary<string, object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = new RouteContext();
        ctx.AddComponent(new GrpcComponent());
        ctx.AddRoutes(r => r.From($"grpc://127.0.0.1:{port}?inOut=true").Process(e =>
        {
            received.TrySetResult(new Dictionary<string, object?>(e.In.Headers, StringComparer.OrdinalIgnoreCase));
            e.Out = new Message("reply");
            e.Out.Headers["X-Reply"] = "r1";
            e.Out.Headers["X-Int"] = 7;
            e.Out.Headers["X-Null"] = null;
            e.Out.Headers[GrpcHeaders.Prefix + "Custom"] = "own";
            e.Out.Headers[GrpcHeaders.TrailerPrefix + "x-trail"] = "t1";
        }));
        await ctx.Start();

        var producer = ctx.GetEndpoint($"grpc://127.0.0.1:{port}?plaintext=true").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("body"));
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["X-Int"] = 42;
        exchange.In.Headers["X-Null"] = null;
        exchange.In.Headers["Authorization"] = "Bearer secret";
        exchange.In.Headers["redbHttp.Method"] = "DELETE";
        exchange.In.Headers[GrpcHeaders.Prefix + "Custom"] = "own";
        await producer.Process(exchange);
        await producer.Stop();

        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        request["x-corr"].Should().Be("c1", "gRPC lowercases metadata keys");
        request["x-int"].Should().Be("42");
        request.Should().NotContainKey("x-null", "a null value is skipped");
        request["authorization"].Should().Be("Bearer secret", "nothing filters a credential");
        request.Should().NotContainKey("redbhttp.method",
            "the consumer drops the control prefixes of every transport (GrpcWire.IsReservedKey) unless allowClientReservedHeaders");
        request.Should().NotContainKey(GrpcHeaders.Prefix + "custom", "the producer keeps its own redbGrpc.* back");

        var response = (exchange.HasOut ? exchange.Out! : exchange.In).Headers;
        response["x-reply"].Should().Be("r1");
        response["x-int"].Should().Be("7");
        response.Should().NotContainKey("x-null");
        response.Keys.Should().NotContain(k => k.Equals(GrpcHeaders.Prefix + "Custom", StringComparison.OrdinalIgnoreCase));
    }
}

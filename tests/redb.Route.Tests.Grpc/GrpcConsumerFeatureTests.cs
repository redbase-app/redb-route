using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Grpc;
using redb.Route.Grpc.Proto;

namespace redb.Route.Tests.Grpc;

/// <summary>
/// Consumer behaviour introduced with the shared-host rewrite: real gRPC statuses, trailers, header
/// hygiene, client address, per-method routing, streaming, health and the camel-grpc URI spelling.
/// Every test drives a real <see cref="GrpcChannel"/>, so these assert the wire, not our own bookkeeping.
/// </summary>
public sealed class GrpcConsumerFeatureTests : IAsyncLifetime
{
    private readonly GrpcComponent _component = new();
    private readonly List<GrpcConsumer> _consumers = [];
    private readonly List<GrpcChannel> _channels = [];
    private int _port;

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var channel in _channels) channel.Dispose();
        foreach (var consumer in _consumers) await consumer.Stop();
    }

    // ── statuses (G2) ────────────────────────────────────────

    [Fact]
    public async Task Neutral_status_code_becomes_a_real_grpc_status()
    {
        await Start(ex =>
        {
            ex.Out = new Message("denied");
            ex.Out.Headers["status.code"] = 403;          // what every controller dispatcher writes
            return Task.CompletedTask;
        });

        var act = async () => await Client().ProcessAsync(new RedbMessage());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Explicit_grpc_status_wins_over_the_neutral_one()
    {
        await Start(ex =>
        {
            ex.Out = new Message("nope");
            ex.Out.Headers["status.code"] = 500;
            ex.Out.Headers[GrpcHeaders.StatusCode] = (int)StatusCode.ResourceExhausted;
            ex.Out.Headers[GrpcHeaders.StatusDetail] = "quota exhausted";
            return Task.CompletedTask;
        });

        var act = async () => await Client().ProcessAsync(new RedbMessage());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.ResourceExhausted);
        ex.Which.Status.Detail.Should().Be("quota exhausted");
    }

    [Fact]
    public async Task SuppressStatusMapping_keeps_the_old_ok_plus_body_behaviour()
    {
        await Start(ex =>
            {
                ex.Out = new Message("error-document");
                ex.Out.Headers["status.code"] = 404;
                return Task.CompletedTask;
            },
            new Dictionary<string, string> { ["suppressStatusMapping"] = "true" });

        var reply = await Client().ProcessAsync(new RedbMessage());

        reply.Payload.ToStringUtf8().Should().Be("error-document");
    }

    [Fact]
    public async Task Processor_exception_reaches_the_caller_as_internal()
    {
        await Start(_ => throw new InvalidOperationException("boom"));

        var act = async () => await Client().ProcessAsync(new RedbMessage());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Internal);
        ex.Which.Status.Detail.Should().Contain("boom");
    }

    [Fact]
    public async Task Trailer_headers_reach_the_client()
    {
        await Start(ex =>
        {
            ex.Out = new Message("body");
            ex.Out.Headers[GrpcHeaders.TrailerPrefix + "error"] = "invalid_scope";
            return Task.CompletedTask;
        });

        using var call = Client().ProcessAsync(new RedbMessage());
        await call.ResponseAsync;

        call.GetTrailers().GetValue("error").Should().Be("invalid_scope");
    }

    // ── header hygiene + client address (G3, G4) ─────────────

    [Fact]
    public async Task Caller_cannot_forge_transport_headers()
    {
        IExchange? seen = null;
        await Start(ex => { seen = ex; return Task.CompletedTask; });

        var request = new RedbMessage();
        request.Headers["redbHttp.RemoteAddress"] = "1.2.3.4";   // would hijack the rate-limit bucket
        request.Headers["redbGrpc.RemoteIp"] = "1.2.3.4";
        request.Headers["dispatch-method"] = "Echo";             // legitimate: a Controllers dispatch key

        await Client().ProcessAsync(request);

        seen!.In.Headers.Should().NotContainKey("redbHttp.RemoteAddress");
        seen.In.GetHeader<string>(GrpcHeaders.RemoteIp).Should().Be("127.0.0.1");
        seen.In.GetHeader<string>("dispatch-method").Should().Be("Echo");
    }

    [Fact]
    public async Task Client_address_is_resolved_and_optionally_bridged()
    {
        IExchange? seen = null;
        await Start(ex => { seen = ex; return Task.CompletedTask; },
            new Dictionary<string, string> { ["emitHttpCompatHeaders"] = "true" });

        await Client().ProcessAsync(new RedbMessage());

        seen!.In.GetHeader<string>(GrpcHeaders.RemoteIp).Should().Be("127.0.0.1");
        seen.In.Headers.Should().ContainKey(GrpcHeaders.RemotePort);
        seen.In.GetHeader<string>(GrpcHeaders.RemotePeer).Should().StartWith("ipv4:127.0.0.1:");

        // The bridge is what makes IP-keyed processors written for HTTP (rate limiting, lockout,
        // device metadata) work behind a gRPC facade unchanged.
        seen.In.GetHeader<string>("redbHttp.RemoteAddress").Should().Be("127.0.0.1");
    }

    [Fact]
    public async Task Route_address_is_visible_to_the_pipeline()
    {
        IExchange? seen = null;
        await Start(ex => { seen = ex; return Task.CompletedTask; });

        await Client().ProcessAsync(new RedbMessage());

        seen!.In.GetHeader<string>(GrpcHeaders.Route).Should().Be(GrpcEndpointOptions.DefaultMethodPath);
        seen.In.GetHeader<string>(GrpcHeaders.Service).Should().Be("redb.route.grpc.RedbService");
        seen.In.GetHeader<string>(GrpcHeaders.Method).Should().Be("Process");
    }

    // ── one port, many methods (G1) ──────────────────────────

    [Fact]
    public async Task Two_consumers_serve_two_methods_on_one_port()
    {
        // The whole reason for the rewrite: this used to be impossible — the second consumer bound its
        // own Kestrel to the same port.
        await StartAt("/identity.v1.Identity/Token",
            ex => { ex.Out = new Message("token-reply"); return Task.CompletedTask; });
        await StartAt("/identity.v1.Identity/Introspect",
            ex => { ex.Out = new Message("introspect-reply"); return Task.CompletedTask; });

        (await CallRaw("/identity.v1.Identity/Token")).Should().Be("token-reply");
        (await CallRaw("/identity.v1.Identity/Introspect")).Should().Be("introspect-reply");
    }

    [Fact]
    public async Task Unknown_method_address_is_unimplemented()
    {
        await StartAt("/identity.v1.Identity/Token",
            ex => { ex.Out = new Message("token"); return Task.CompletedTask; });

        var act = async () => await CallRaw("/identity.v1.Identity/Nope");

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unimplemented);
    }

    [Fact]
    public async Task Camel_style_service_and_method_address_the_same_route()
    {
        // grpc://host:port/my.Service?method=Call — the camel-grpc spelling.
        await Start(ex => { ex.Out = new Message("camel-reply"); return Task.CompletedTask; },
            new Dictionary<string, string> { ["service"] = "identity.v1.Identity", ["method"] = "Token" },
            path: "/identity.v1.Identity");

        (await CallRaw("/identity.v1.Identity/Token")).Should().Be("camel-reply");
    }

    // ── streaming (G8) ───────────────────────────────────────

    [Fact]
    public async Task Async_enumerable_body_streams_one_frame_per_yield()
    {
        await Start(ex =>
        {
            ex.Out = new Message(Pages());
            return Task.CompletedTask;
        });

        using var call = Client().ProcessStream(new RedbMessage());

        var items = new List<string>();
        while (await call.ResponseStream.MoveNext(CancellationToken.None))
            items.Add(call.ResponseStream.Current.Payload.ToStringUtf8());

        items.Should().Equal("page-1", "page-2", "page-3");

        static async IAsyncEnumerable<object?> Pages()
        {
            for (var i = 1; i <= 3; i++)
            {
                await Task.Yield();
                yield return $"page-{i}";
            }
        }
    }

    [Fact]
    public async Task Stream_body_on_a_unary_method_fails_loudly()
    {
        // A unary call carries exactly one message, so a stream body cannot be delivered. Without the
        // guard the enumerable's type name would go on the wire as the payload — silent garbage.
        await Start(ex =>
        {
            ex.Out = new Message(Empty());
            return Task.CompletedTask;
        });

        var act = async () => await Client().ProcessAsync(new RedbMessage());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.Status.Detail.Should().Contain("Streaming");

        static async IAsyncEnumerable<object?> Empty()
        {
            await Task.Yield();
            yield return "x";
        }
    }

    // ── compression ──────────────────────────────────────────

    [Fact]
    public async Task Gzip_is_accepted_inbound_and_used_outbound_when_the_caller_allows_it()
    {
        // Asserted at the wire level on purpose: Grpc.Net.Client consumes grpc-encoding /
        // grpc-accept-encoding itself and inflates transparently, so a client-side assertion would pass
        // even if we never compressed anything.
        string? received = null;
        await Start(ex =>
            {
                // In envelope mode the consumer has already unwrapped RedbMessage — the body is the
                // payload itself, not the envelope.
                received = System.Text.Encoding.UTF8.GetString((byte[])ex.In.Body!);
                ex.Out = new Message(new string('z', 4096));
                return Task.CompletedTask;
            },
            new Dictionary<string, string> { ["compression"] = "Gzip" });

        var request = new RedbMessage { Payload = ByteString.CopyFromUtf8(new string('q', 2048)) };
        var (headers, frame) = await PostFrame(GrpcEndpointOptions.DefaultMethodPath, request.ToByteArray(), compressRequest: true);

        received.Should().Be(new string('q', 2048), "the server must inflate a compressed request");

        headers.GetValues("grpc-accept-encoding").Should().Contain(v => v.Contains("gzip"));
        headers.GetValues("grpc-encoding").Should().Contain("gzip");

        frame[0].Should().Be(1, "the reply frame must carry the compressed flag");
        var payload = Gunzip(frame[5..]);
        RedbMessage.Parser.ParseFrom(payload).Payload.ToStringUtf8().Should().Be(new string('z', 4096));

        // Compression has to actually pay for itself on repetitive content.
        frame.Length.Should().BeLessThan(4096);
    }

    [Fact]
    public async Task Uncompressed_reply_when_the_caller_does_not_advertise_gzip()
    {
        await Start(ex => { ex.Out = new Message("plain"); return Task.CompletedTask; },
            new Dictionary<string, string> { ["compression"] = "Gzip" });

        var (headers, frame) = await PostFrame(GrpcEndpointOptions.DefaultMethodPath,
            new RedbMessage().ToByteArray(), acceptEncoding: "identity");

        headers.Contains("grpc-encoding").Should().BeFalse();
        frame[0].Should().Be(0);
    }

    [Fact]
    public async Task Producer_reads_a_server_stream_as_an_async_enumerable()
    {
        // Client side of streaming: the reply lands in Out.Body as an IAsyncEnumerable, the framework's
        // streaming shape — so a gRPC stream can flow straight into a streaming consumer (the HTTP one
        // turns it into SSE) without being buffered in between.
        await Start(ex =>
        {
            ex.Out = new Message(Pages());
            return Task.CompletedTask;
        });

        var producerUri = new EndpointUri("grpc",
            $"/127.0.0.1:{_port}{GrpcEndpointOptions.DefaultStreamMethodPath}",
            $"grpc:127.0.0.1:{_port}{GrpcEndpointOptions.DefaultStreamMethodPath}",
            new Dictionary<string, string> { ["plaintext"] = "true", ["streaming"] = "true" });
        var producerEndpoint = (GrpcEndpoint)_component.CreateEndpoint(producerUri);

        var producer = new GrpcProducer(producerEndpoint, producerEndpoint.EndpointOptions);
        await producer.Start();

        var exchange = new Exchange(new Message(Array.Empty<byte>()));
        await producer.Process(exchange);

        var items = new List<string>();
        await foreach (var item in (IAsyncEnumerable<object?>)exchange.Out!.Body!)
            items.Add(System.Text.Encoding.UTF8.GetString((byte[])item!));

        items.Should().Equal("page-1", "page-2", "page-3");
        await producer.Stop();

        static async IAsyncEnumerable<object?> Pages()
        {
            for (var i = 1; i <= 3; i++)
            {
                await Task.Yield();
                yield return $"page-{i}";
            }
        }
    }

    // ── health (G8) ──────────────────────────────────────────

    [Fact]
    public async Task Health_check_answers_serving()
    {
        await Start(_ => Task.CompletedTask, new Dictionary<string, string> { ["health"] = "true" });

        var reply = await CallRawBytes("/grpc.health.v1.Health/Check");

        // HealthCheckResponse { status = SERVING } → field 1, varint 1.
        reply.Should().Equal((byte)0x08, (byte)0x01);
    }

    // ── helpers ───────────────────────────────────────────────


    [Fact]
    public async Task A_malformed_envelope_is_the_callers_fault_not_ours()
    {
        // Envelope mode parses caller-supplied bytes as a RedbMessage. Garbage there used to fall through
        // the catch-all as Internal — which tells the caller "our fault, retry" about input only they can
        // fix — and put the protobuf parser's own wording into grpc-message. Their mistake, their status.
        await Start(ex => { ex.Out = new Message("never reached"); return Task.CompletedTask; },
            new Dictionary<string, string> { ["envelope"] = "true" });

        // Not a protobuf message: a wire type nothing defines, then trailing junk.
        var garbage = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0x7A, 0x7A };

        var act = async () => await CallRawBytesWith(GrpcEndpointOptions.DefaultMethodPath, garbage);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument,
            "a request the caller malformed is not an internal server failure");
        ex.Which.Status.Detail.Should().Contain("RedbMessage",
            "the answer should name what was expected, so the caller can fix it");
    }

    /// <summary>Calls a method address with an arbitrary request body.</summary>
    private async Task<byte[]> CallRawBytesWith(string methodPath, byte[] payload)
    {
        var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{_port}");
        _channels.Add(channel);

        var slash = methodPath.LastIndexOf('/');
        var marshaller = Marshallers.Create(b => (byte[])b, b => b);
        var method = new Method<byte[], byte[]>(
            MethodType.Unary, methodPath[1..slash], methodPath[(slash + 1)..], marshaller, marshaller);

        return await channel.CreateCallInvoker()
            .AsyncUnaryCall(method, null, new CallOptions(), payload);
    }


    [Fact]
    public async Task A_server_stream_that_breaks_mid_flight_is_recorded_not_swallowed()
    {
        // A server-streaming call reports failure while the consumer enumerates — long after Process has
        // returned. That is inherent to lazy streaming: .OnException, retry and dead-letter cannot see it,
        // and pretending otherwise would be a lie. What is NOT inherent is the failure being invisible: it
        // used to escape with no log and no metric, so a stream that broke every time looked like a stream
        // that simply ended early.
        await Start(ex =>
        {
            ex.Out = new Message(FailingPages());
            return Task.CompletedTask;
        });

        var producerUri = new EndpointUri("grpc",
            $"/127.0.0.1:{_port}{GrpcEndpointOptions.DefaultStreamMethodPath}",
            $"grpc:127.0.0.1:{_port}{GrpcEndpointOptions.DefaultStreamMethodPath}",
            new Dictionary<string, string> { ["plaintext"] = "true", ["streaming"] = "true" });
        var producerEndpoint = (GrpcEndpoint)_component.CreateEndpoint(producerUri);

        var producer = new GrpcProducer(producerEndpoint, producerEndpoint.EndpointOptions);
        await producer.Start();

        var before = producerEndpoint.Errors;

        var exchange = new Exchange(new Message(Array.Empty<byte>()));
        await producer.Process(exchange);

        var received = new List<string>();
        var act = async () =>
        {
            await foreach (var item in (IAsyncEnumerable<object?>)exchange.Out!.Body!)
                received.Add(System.Text.Encoding.UTF8.GetString((byte[])item!));
        };

        // The break still reaches whoever is reading — swallowing it would be worse than not logging it.
        await act.Should().ThrowAsync<RpcException>();
        received.Should().NotBeEmpty("the frames sent before the break are real and were delivered");

        producerEndpoint.Errors.Should().BeGreaterThan(before,
            "a broken stream must show up on the endpoint, not only in the reader's stack trace");

        await producer.Stop();

        static async IAsyncEnumerable<object?> FailingPages()
        {
            yield return System.Text.Encoding.UTF8.GetBytes("page-1");
            await Task.Yield();
            throw new InvalidOperationException("upstream died mid-stream");
        }
    }

    private Task Start(Func<IExchange, Task> onProcess, Dictionary<string, string>? extra = null, string path = "")
        => StartConsumer(path, onProcess, extra);

    private Task StartAt(string methodPath, Func<IExchange, Task> onProcess)
        => StartConsumer(methodPath, onProcess, null);

    private async Task StartConsumer(string path, Func<IExchange, Task> onProcess, Dictionary<string, string>? extra)
    {
        // One component per scheme, as a RouteContext holds it — that is what makes several gRPC routes
        // share a single Kestrel host. A second component instance would bring its own fallback manager
        // and the two would fight over the port.
        var component = _component;
        var parameters = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["port"] = _port.ToString(),
        };
        if (extra is not null)
            foreach (var (key, value) in extra) parameters[key] = value;

        var uriPath = $"/127.0.0.1:{_port}{path}";
        var uri = new EndpointUri("grpc", uriPath, $"grpc:127.0.0.1:{_port}{path}", parameters);
        var endpoint = (GrpcEndpoint)component.CreateEndpoint(uri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await onProcess(ci.Arg<IExchange>()));

        var consumer = new GrpcConsumer(endpoint, processor, endpoint.EndpointOptions);
        _consumers.Add(consumer);
        await consumer.Start();
    }

    private RedbService.RedbServiceClient Client()
    {
        var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{_port}");
        _channels.Add(channel);
        return new RedbService.RedbServiceClient(channel);
    }

    /// <summary>
    /// Calls a typed method address. Such an address is served in raw mode (Envelope=Auto), so the reply
    /// is the route's body verbatim — no <c>RedbMessage</c> wrapper, which is exactly what a client
    /// generated from a real <c>.proto</c> expects.
    /// </summary>
    private async Task<string> CallRaw(string methodPath)
        => System.Text.Encoding.UTF8.GetString(await CallRawBytes(methodPath));

    /// <summary>Calls any method address with the generic message, bypassing generated stubs.</summary>
    private async Task<byte[]> CallRawBytes(string methodPath)
    {
        var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{_port}");
        _channels.Add(channel);

        var slash = methodPath.LastIndexOf('/');
        var marshaller = Marshallers.Create(b => (byte[])b, b => b);
        var method = new Method<byte[], byte[]>(
            MethodType.Unary, methodPath[1..slash], methodPath[(slash + 1)..], marshaller, marshaller);

        return await channel.CreateCallInvoker()
            .AsyncUnaryCall(method, null, new CallOptions(), new RedbMessage().ToByteArray());
    }

    /// <summary>
    /// Sends one gRPC frame over raw HTTP/2 and returns the response headers plus the reply frame, so a
    /// test can inspect the bytes a generated client would hide.
    /// </summary>
    private async Task<(System.Net.Http.Headers.HttpResponseHeaders Headers, byte[] Frame)> PostFrame(
        string methodPath, byte[] message, string acceptEncoding = "identity,gzip", bool compressRequest = false)
    {
        var payload = compressRequest ? Gzip(message) : message;

        var frame = new byte[5 + payload.Length];
        frame[0] = compressRequest ? (byte)1 : (byte)0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(frame, 5);

        using var client = new System.Net.Http.HttpClient();
        using var content = new System.Net.Http.ByteArrayContent(frame);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");

        using var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Post, $"http://127.0.0.1:{_port}{methodPath}")
        {
            Content = content,
            Version = System.Net.HttpVersion.Version20,
            VersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.TryAddWithoutValidation("te", "trailers");
        request.Headers.TryAddWithoutValidation("grpc-accept-encoding", acceptEncoding);
        if (frame[0] == 1) request.Headers.TryAddWithoutValidation("grpc-encoding", "gzip");

        var response = await client.SendAsync(request);

        // Surface the gRPC outcome: a failed call answers HTTP 200 with a grpc-status trailer, so a bare
        // status assertion downstream would say nothing useful.
        var body = await response.Content.ReadAsByteArrayAsync();
        var grpcStatus = response.TrailingHeaders.TryGetValues("grpc-status", out var st) ? string.Join(",", st) : "(none)";
        var grpcMessage = response.TrailingHeaders.TryGetValues("grpc-message", out var msg) ? string.Join(",", msg) : "";
        response.IsSuccessStatusCode.Should().BeTrue($"HTTP {(int)response.StatusCode}");
        grpcStatus.Should().Be("0", $"call failed: {Uri.UnescapeDataString(grpcMessage)}");

        return (response.Headers, body);
    }


    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output,
                   System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

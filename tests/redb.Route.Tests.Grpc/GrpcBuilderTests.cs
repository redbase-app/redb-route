using redb.Route.Core;
using redb.Route.Grpc;

namespace redb.Route.Tests.Grpc;

public class GrpcBuilderTests
{
    // ── Factory ─────────────────────────────────────────────────────

    [Fact]
    public void Call_StartsWithGrpcScheme()
    {
        var uri = GrpcDsl.Call("localhost:50051").Build();
        uri.Should().StartWith("grpc:localhost:50051");
    }

    [Fact]
    public void Listen_StartsWithGrpcScheme()
    {
        var uri = GrpcDsl.Listen("0.0.0.0:50051").Build();
        uri.Should().StartWith("grpc:0.0.0.0:50051");
    }

    [Fact]
    public void NullHostPort_Throws()
    {
        var act = () => GrpcDsl.Call(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EmptyHostPort_Throws()
    {
        var act = () => GrpcDsl.Listen("");
        act.Should().Throw<ArgumentException>();
    }

    // ── Common ──────────────────────────────────────────────────────

    [Fact]
    public void Deadline_SetsParam()
    {
        var uri = GrpcDsl.Call("h:50051").Deadline(5000).Build();
        uri.Should().Contain("deadline=5000");
    }

    // ── Producer ────────────────────────────────────────────────────

    [Fact]
    public void Plaintext_SetsParam()
    {
        var uri = GrpcDsl.Call("h:50051").Plaintext().Build();
        uri.Should().Contain("plaintext=true");
    }

    [Fact]
    public void MaxSendMessageSize_SetsParam()
    {
        var uri = GrpcDsl.Call("h:50051").MaxSendMessageSize(4096).Build();
        uri.Should().Contain("maxSendMessageSize=4096");
    }

    [Fact]
    public void MaxReceiveMessageSize_SetsParam()
    {
        var uri = GrpcDsl.Call("h:50051").MaxReceiveMessageSize(8192).Build();
        uri.Should().Contain("maxReceiveMessageSize=8192");
    }

    // ── Consumer ────────────────────────────────────────────────────

    [Fact]
    public void Host_SetsParam()
    {
        var uri = GrpcDsl.Listen("0.0.0.0:50051").Host("127.0.0.1").Build();
        uri.Should().Contain("host=127.0.0.1");
    }

    [Fact]
    public void Port_SetsParam()
    {
        var uri = GrpcDsl.Listen("0.0.0.0:50051").Port(50052).Build();
        uri.Should().Contain("port=50052");
    }

    [Fact]
    public void Ssl_SetsParam()
    {
        var uri = GrpcDsl.Listen("0.0.0.0:50051").Ssl().Build();
        uri.Should().Contain("ssl=true");
    }

    [Fact]
    public void SslCertPath_SetsParam()
    {
        var uri = GrpcDsl.Listen("0.0.0.0:50051").SslCertPath("/certs/server.pfx").Build();
        uri.Should().Contain("sslCertPath=");
    }

    [Fact]
    public void InOut_SetsParam()
    {
        var uri = GrpcDsl.Listen("0.0.0.0:50051").InOut().Build();
        uri.Should().Contain("inOut=true");
    }

    [Fact]
    public void MaxRequestMessageSize_SetsParam()
    {
        var uri = GrpcDsl.Listen("0.0.0.0:50051").MaxRequestMessageSize(16384).Build();
        uri.Should().Contain("maxRequestMessageSize=16384");
    }

    // ── Conversion ──────────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = GrpcDsl.Call("h:50051").Plaintext().Deadline(10000);
        uri.Should().StartWith("grpc:h:50051?");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = GrpcDsl.Call("h:50051").Plaintext();
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full chain ──────────────────────────────────────────────────

    [Fact]
    public void FullChain_Producer_BuildsCorrectUri()
    {
        var uri = GrpcDsl.Call("api.example.com:50051")
            .Plaintext()
            .Deadline(10000)
            .MaxSendMessageSize(4096)
            .MaxReceiveMessageSize(8192)
            .Build();

        uri.Should().StartWith("grpc:api.example.com:50051?");
        uri.Should().Contain("plaintext=true");
        uri.Should().Contain("deadline=10000");
    }

    [Fact]
    public void FullChain_Consumer_BuildsCorrectUri()
    {
        var uri = GrpcDsl.Listen("0.0.0.0:50051")
            .Ssl()
            .SslCertPath("/certs/server.pfx")
            .SslCertPassword("pass")
            .MaxRequestMessageSize(16384)
            .InOut()
            .Build();

        uri.Should().StartWith("grpc:0.0.0.0:50051?");
        uri.Should().Contain("ssl=true");
        uri.Should().Contain("inOut=true");
    }

    // ── Round-trip ──────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = GrpcDsl.Call("h:50051").Plaintext().Deadline(5000).Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("grpc");
        parsed.Path.Should().Contain("50051");
        parsed.RawParameters["plaintext"].Should().Be("true");
        parsed.RawParameters["deadline"].Should().Be("5000");
    }
}

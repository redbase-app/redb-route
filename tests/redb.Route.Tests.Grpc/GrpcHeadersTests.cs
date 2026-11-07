using redb.Route.Grpc;

namespace redb.Route.Tests.Grpc;

public class GrpcHeadersTests
{
    [Theory]
    [InlineData("redbGrpc.Method")]
    [InlineData("redbGrpc.StatusCode")]
    [InlineData("redbGrpc.Port")]
    [InlineData("redbGrpc.RemotePeer")]
    [InlineData("redbGrpc.Authority")]
    [InlineData("redbGrpc.Deadline")]
    [InlineData("redbGrpc.Service")]
    [InlineData("redbGrpc.StatusDetail")]
    public void IsRedbHeader_WithPrefix_ReturnsTrue(string key)
    {
        GrpcHeaders.IsRedbHeader(key).Should().BeTrue();
    }

    [Theory]
    [InlineData("Content-Type")]
    [InlineData("X-Custom")]
    [InlineData("grpc-status")]
    [InlineData("method")]
    [InlineData("")]
    public void IsRedbHeader_WithoutPrefix_ReturnsFalse(string key)
    {
        GrpcHeaders.IsRedbHeader(key).Should().BeFalse();
    }

    [Fact]
    public void Constants_HaveCorrectPrefix()
    {
        GrpcHeaders.Method.Should().StartWith("redbGrpc.");
        GrpcHeaders.StatusCode.Should().StartWith("redbGrpc.");
        GrpcHeaders.StatusDetail.Should().StartWith("redbGrpc.");
        GrpcHeaders.RemotePeer.Should().StartWith("redbGrpc.");
        GrpcHeaders.Port.Should().StartWith("redbGrpc.");
        GrpcHeaders.Authority.Should().StartWith("redbGrpc.");
        GrpcHeaders.Deadline.Should().StartWith("redbGrpc.");
        GrpcHeaders.Service.Should().StartWith("redbGrpc.");
    }
}

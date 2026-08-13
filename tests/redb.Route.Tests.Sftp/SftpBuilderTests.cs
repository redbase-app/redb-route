using redb.Route.Core;
using redb.Route.Expressions;
using SftpDsl = redb.Route.Sftp.Sftp;

namespace redb.Route.Tests.Sftp;

public class SftpBuilderTests
{
    private static ConstantExpression C(string s) => new(s);
    // ── Factory ─────────────────────────────────────────────────────

    [Fact]
    public void Directory_StartsWithSftpScheme()
    {
        var uri = SftpDsl.Directory("/incoming").Build();
        uri.Should().StartWith("sftp:///incoming");
    }

    [Fact]
    public void NullPath_Throws()
    {
        var act = () => SftpDsl.Directory(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EmptyPath_Throws()
    {
        var act = () => SftpDsl.Directory("");
        act.Should().Throw<ArgumentException>();
    }

    // ── Connection ──────────────────────────────────────────────────

    [Fact]
    public void Host_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").Host(C("sftp.example.com")).Build();
        uri.Should().Contain("host=sftp.example.com");
    }

    [Fact]
    public void Port_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").Port(2222).Build();
        uri.Should().Contain("port=2222");
    }

    [Fact]
    public void Username_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").Username(C("admin")).Build();
        uri.Should().Contain("username=admin");
    }

    [Fact]
    public void Password_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").Password(C("secret")).Build();
        uri.Should().Contain("password=secret");
    }

    [Fact]
    public void PrivateKey_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").PrivateKey(C("/keys/id_rsa")).Build();
        uri.Should().Contain("privateKeyPath=");
    }

    [Fact]
    public void PrivateKey_WithPassphrase_SetsBothParams()
    {
        var uri = SftpDsl.Directory("/d").PrivateKey(C("/keys/id_rsa"), C("pp")).Build();
        uri.Should().Contain("privateKeyPath=");
        uri.Should().Contain("privateKeyPassphrase=pp");
    }

    [Fact]
    public void ServerFingerprint_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").ServerFingerprint(C("SHA256:abc")).Build();
        uri.Should().Contain("serverFingerprint=");
    }

    [Fact]
    public void StrictHostKeyChecking_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").StrictHostKeyChecking().Build();
        uri.Should().Contain("strictHostKeyChecking=true");
    }

    [Fact]
    public void Compression_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").Compression().Build();
        uri.Should().Contain("compression=true");
    }

    // ── Consumer ────────────────────────────────────────────────────

    [Fact]
    public void Include_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").Include("*.csv").Build();
        uri.Should().Contain("include=%2A.csv");
    }

    [Fact]
    public void Exclude_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").Exclude("*.tmp").Build();
        uri.Should().Contain("exclude=%2A.tmp");
    }

    [Fact]
    public void Recursive_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").Recursive().Build();
        uri.Should().Contain("recursive=true");
    }

    [Fact]
    public void Delay_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").Delay(5000).Build();
        uri.Should().Contain("delay=5000");
    }

    [Fact]
    public void SortBy_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").SortBy("name").Build();
        uri.Should().Contain("sortBy=name");
    }

    [Fact]
    public void MaxMessagesPerPoll_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").MaxMessagesPerPoll(10).Build();
        uri.Should().Contain("maxMessagesPerPoll=10");
    }

    // ── Post-processing ─────────────────────────────────────────────

    [Fact]
    public void Noop_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").Noop().Build();
        uri.Should().Contain("noop=true");
    }

    [Fact]
    public void Delete_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").Delete().Build();
        uri.Should().Contain("delete=true");
    }

    [Fact]
    public void MoveTo_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").MoveTo(C("/done")).Build();
        uri.Should().Contain("moveTo=");
    }

    [Fact]
    public void Idempotent_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").Idempotent().Build();
        uri.Should().Contain("idempotent=true");
    }

    // ── Producer ────────────────────────────────────────────────────

    [Fact]
    public void AutoCreate_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").AutoCreate().Build();
        uri.Should().Contain("autoCreate=true");
    }

    [Fact]
    public void TempPrefix_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").TempPrefix(C(".tmp")).Build();
        uri.Should().Contain("tempPrefix=.tmp");
    }

    [Fact]
    public void Chmod_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").Chmod("644").Build();
        uri.Should().Contain("chmod=644");
    }

    [Fact]
    public void Flatten_SetsParam()
    {
        var uri = SftpDsl.Directory("/d").Flatten().Build();
        uri.Should().Contain("flatten=true");
    }

    // ── Conversion ──────────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = SftpDsl.Directory("/incoming").Host(C("sftp.example.com")).Username(C("admin"));
        uri.Should().StartWith("sftp:///incoming?");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = SftpDsl.Directory("/d").Host(C("h")).Include("*.csv");
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full chain ──────────────────────────────────────────────────

    [Fact]
    public void FullChain_Consumer_BuildsCorrectUri()
    {
        var uri = SftpDsl.Directory("/incoming")
            .Host(C("sftp.example.com"))
            .Port(22)
            .Username(C("admin"))
            .Password(C("secret"))
            .Include("*.csv")
            .Recursive()
            .Delete()
            .Delay(5000)
            .Build();

        uri.Should().StartWith("sftp:///incoming?");
        uri.Should().Contain("host=sftp.example.com");
        uri.Should().Contain("include=%2A.csv");
        uri.Should().Contain("recursive=true");
        uri.Should().Contain("delete=true");
    }

    [Fact]
    public void FullChain_Producer_BuildsCorrectUri()
    {
        var uri = SftpDsl.Directory("/outgoing")
            .Host(C("sftp.example.com"))
            .Username(C("admin"))
            .Password(C("secret"))
            .AutoCreate()
            .TempPrefix(C(".uploading-"))
            .Chmod("644")
            .Build();

        uri.Should().StartWith("sftp:///outgoing?");
        uri.Should().Contain("autoCreate=true");
        uri.Should().Contain("chmod=644");
    }

    // ── Round-trip ──────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = SftpDsl.Directory("/data").Host(C("sftp.example.com")).Include("*.csv").Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("sftp");
        parsed.RawParameters["host"].Should().Be("sftp.example.com");
        parsed.RawParameters["include"].Should().Be("*.csv");
    }

    // ── StreamBody ──────────────────────────────────────────────────

    [Fact]
    public void StreamBody_SetsParam()
    {
        var uri = SftpDsl.Directory("/data").StreamBody().Build();
        uri.Should().Contain("streamBody=true");
    }

    [Fact]
    public void StreamBody_RoundTrip()
    {
        var uri = SftpDsl.Directory("/data").StreamBody().Build();
        var parsed = EndpointUriParser.Parse(uri);
        parsed.RawParameters["streamBody"].Should().Be("true");
    }
}

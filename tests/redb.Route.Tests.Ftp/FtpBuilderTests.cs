using redb.Route.Core;
using redb.Route.Expressions;
using FtpDsl = redb.Route.Ftp.Ftp;

namespace redb.Route.Tests.Ftp;

public class FtpBuilderTests
{
    private static ConstantExpression C(string s) => new(s);

    // ── Factory ─────────────────────────────────────────────────────

    [Fact]
    public void Directory_StartsWithFtpScheme()
    {
        var uri = FtpDsl.Directory("/incoming").Build();
        uri.Should().StartWith("ftp:///incoming");
    }

    [Fact]
    public void NullPath_Throws()
    {
        var act = () => FtpDsl.Directory(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EmptyPath_Throws()
    {
        var act = () => FtpDsl.Directory("");
        act.Should().Throw<ArgumentException>();
    }

    // ── Connection ──────────────────────────────────────────────────

    [Fact]
    public void Host_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").Host(C("ftp.example.com")).Build();
        uri.Should().Contain("host=ftp.example.com");
    }

    [Fact]
    public void Port_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").Port(2121).Build();
        uri.Should().Contain("port=2121");
    }

    [Fact]
    public void Username_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").Username(C("admin")).Build();
        uri.Should().Contain("username=admin");
    }

    [Fact]
    public void Password_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").Password(C("secret")).Build();
        uri.Should().Contain("password=secret");
    }

    [Fact]
    public void PassiveMode_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").PassiveMode().Build();
        uri.Should().Contain("passiveMode=true");
    }

    [Fact]
    public void ActiveMode_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").ActiveMode().Build();
        uri.Should().Contain("passiveMode=false");
    }

    // ── TLS ─────────────────────────────────────────────────────────

    [Fact]
    public void UseFtps_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").UseFtps().Build();
        uri.Should().Contain("useFtps=true");
    }

    [Fact]
    public void ValidateCertificate_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").ValidateCertificate(false).Build();
        uri.Should().Contain("validateCertificate=false");
    }

    // ── Consumer ────────────────────────────────────────────────────

    [Fact]
    public void Include_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").Include("*.csv").Build();
        uri.Should().Contain("include=%2A.csv");
    }

    [Fact]
    public void Exclude_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").Exclude("*.tmp").Build();
        uri.Should().Contain("exclude=%2A.tmp");
    }

    [Fact]
    public void Recursive_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").Recursive().Build();
        uri.Should().Contain("recursive=true");
    }

    [Fact]
    public void Delay_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").Delay(5000).Build();
        uri.Should().Contain("delay=5000");
    }

    [Fact]
    public void SortBy_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").SortBy("name").Build();
        uri.Should().Contain("sortBy=name");
    }

    [Fact]
    public void MaxMessagesPerPoll_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").MaxMessagesPerPoll(10).Build();
        uri.Should().Contain("maxMessagesPerPoll=10");
    }

    // ── Post-processing ─────────────────────────────────────────────

    [Fact]
    public void Noop_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").Noop().Build();
        uri.Should().Contain("noop=true");
    }

    [Fact]
    public void Delete_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").Delete().Build();
        uri.Should().Contain("delete=true");
    }

    [Fact]
    public void MoveTo_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").MoveTo(C("/done")).Build();
        uri.Should().Contain("moveTo=");
    }

    [Fact]
    public void Idempotent_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").Idempotent().Build();
        uri.Should().Contain("idempotent=true");
    }

    // ── Producer ────────────────────────────────────────────────────

    [Fact]
    public void AutoCreate_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").AutoCreate().Build();
        uri.Should().Contain("autoCreate=true");
    }

    [Fact]
    public void TempPrefix_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").TempPrefix(C(".tmp")).Build();
        uri.Should().Contain("tempPrefix=.tmp");
    }

    [Fact]
    public void Flatten_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").Flatten().Build();
        uri.Should().Contain("flatten=true");
    }

    [Fact]
    public void TransferType_SetsParam()
    {
        var uri = FtpDsl.Directory("/d").TransferType("Ascii").Build();
        uri.Should().Contain("transferType=Ascii");
    }

    // ── Conversion ──────────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = FtpDsl.Directory("/incoming").Host(C("ftp.example.com")).Username(C("admin"));
        uri.Should().StartWith("ftp:///incoming?");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = FtpDsl.Directory("/d").Host(C("h")).Include("*.csv");
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full chain ──────────────────────────────────────────────────

    [Fact]
    public void FullChain_Consumer_BuildsCorrectUri()
    {
        var uri = FtpDsl.Directory("/incoming")
            .Host(C("ftp.example.com"))
            .Port(21)
            .Username(C("admin"))
            .Password(C("secret"))
            .Include("*.csv")
            .Recursive()
            .Delete()
            .Delay(5000)
            .Build();

        uri.Should().StartWith("ftp:///incoming?");
        uri.Should().Contain("host=ftp.example.com");
        uri.Should().Contain("include=%2A.csv");
        uri.Should().Contain("recursive=true");
        uri.Should().Contain("delete=true");
    }

    [Fact]
    public void FullChain_Producer_BuildsCorrectUri()
    {
        var uri = FtpDsl.Directory("/outgoing")
            .Host(C("ftp.example.com"))
            .Username(C("admin"))
            .Password(C("secret"))
            .AutoCreate()
            .TempPrefix(C(".uploading-"))
            .Build();

        uri.Should().StartWith("ftp:///outgoing?");
        uri.Should().Contain("autoCreate=true");
    }

    // ── Round-trip ──────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = FtpDsl.Directory("/data").Host(C("ftp.example.com")).Include("*.csv").Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("ftp");
        parsed.RawParameters["host"].Should().Be("ftp.example.com");
        parsed.RawParameters["include"].Should().Be("*.csv");
    }

    // ── StreamBody ──────────────────────────────────────────────────

    [Fact]
    public void StreamBody_SetsParam()
    {
        var uri = FtpDsl.Directory("/data").StreamBody().Build();
        uri.Should().Contain("streamBody=true");
    }

    [Fact]
    public void StreamBody_RoundTrip()
    {
        var uri = FtpDsl.Directory("/data").StreamBody().Build();
        var parsed = EndpointUriParser.Parse(uri);
        parsed.RawParameters["streamBody"].Should().Be("true");
    }
}

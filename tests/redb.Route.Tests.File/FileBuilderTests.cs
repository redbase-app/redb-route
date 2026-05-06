using redb.Route.Core;
using redb.Route.File;
using redb.Route.Expressions;

namespace redb.Route.Tests.File;

public class FileBuilderTests
{
    private static ConstantExpression C(string s) => new(s);
    // ── Factory methods ─────────────────────────────────────────────

    [Fact]
    public void Read_StartsWithFileScheme()
    {
        var uri = FileDsl.Read("/data/input").Build();
        uri.Should().StartWith("file:///");
    }

    [Fact]
    public void Write_StartsWithFileScheme()
    {
        var uri = FileDsl.Write("/data/output").Build();
        uri.Should().StartWith("file:///");
    }

    [Fact]
    public void NullDirectory_Throws()
    {
        var act = () => FileDsl.Read(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PathIsNormalized_NoDoubleSlash()
    {
        var uri = FileDsl.Read("/incoming").Build();
        uri.Should().StartWith("file:///incoming");
    }

    // ── Consumer params ─────────────────────────────────────────────

    [Fact]
    public void Delay_SetsParam()
    {
        var uri = FileDsl.Read("/in").Delay(5000).Build();
        uri.Should().Contain("delay=5000");
    }

    [Fact]
    public void Include_SetsParam()
    {
        var uri = FileDsl.Read("/in").Include("*.csv").Build();
        uri.Should().Contain("include=*.csv");
    }

    [Fact]
    public void Exclude_SetsParam()
    {
        var uri = FileDsl.Read("/in").Exclude("*.tmp").Build();
        uri.Should().Contain("exclude=*.tmp");
    }

    [Fact]
    public void Recursive_SetsParam()
    {
        var uri = FileDsl.Read("/in").Recursive().Build();
        uri.Should().Contain("recursive=true");
    }

    [Fact]
    public void SortBy_SetsParam()
    {
        var uri = FileDsl.Read("/in").SortBy("Modified").Build();
        uri.Should().Contain("sortBy=Modified");
    }

    [Fact]
    public void MaxMessagesPerPoll_SetsParam()
    {
        var uri = FileDsl.Read("/in").MaxMessagesPerPoll(10).Build();
        uri.Should().Contain("maxMessagesPerPoll=10");
    }

    // ── Post-processing ─────────────────────────────────────────────

    [Fact]
    public void Noop_SetsParam()
    {
        var uri = FileDsl.Read("/in").Noop().Build();
        uri.Should().Contain("noop=true");
    }

    [Fact]
    public void Delete_SetsParam()
    {
        var uri = FileDsl.Read("/in").Delete().Build();
        uri.Should().Contain("delete=true");
    }

    [Fact]
    public void MoveTo_SetsParam()
    {
        var uri = FileDsl.Read("/in").MoveTo(C("/archive")).Build();
        uri.Should().Contain("moveTo=%2farchive");
    }

    // ── Idempotency ─────────────────────────────────────────────────

    [Fact]
    public void Idempotent_SetsParam()
    {
        var uri = FileDsl.Read("/in").Idempotent().Build();
        uri.Should().Contain("idempotent=true");
    }

    [Fact]
    public void IdempotentWithKey_SetsBothParams()
    {
        var uri = FileDsl.Read("/in").Idempotent(C("${file:name}")).Build();
        uri.Should().Contain("idempotent=true");
        uri.Should().Contain("idempotentKey=");
    }

    // ── Read locking ────────────────────────────────────────────────

    [Fact]
    public void ReadLock_SetsParam()
    {
        var uri = FileDsl.Read("/in").ReadLock("Changed").Build();
        uri.Should().Contain("readLock=Changed");
    }

    // ── Producer params ─────────────────────────────────────────────

    [Fact]
    public void FileExist_SetsParam()
    {
        var uri = FileDsl.Write("/out").FileExist("Append").Build();
        uri.Should().Contain("fileExist=Append");
    }

    [Fact]
    public void Charset_SetsParam()
    {
        var uri = FileDsl.Write("/out").Charset("windows-1251").Build();
        uri.Should().Contain("charset=windows-1251");
    }

    [Fact]
    public void TempPrefix_SetsParam()
    {
        var uri = FileDsl.Write("/out").TempPrefix(C(".tmp")).Build();
        uri.Should().Contain("tempPrefix=.tmp");
    }

    // ── Conversion ──────────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = FileDsl.Read("/data").Include("*.xml").Recursive();
        uri.Should().StartWith("file:///data?");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = FileDsl.Write("/out").FileExist("Fail");
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full chain ──────────────────────────────────────────────────

    [Fact]
    public void FullConsumerChain_BuildsCorrectUri()
    {
        var uri = FileDsl.Read("/incoming")
            .Include("*.csv")
            .Recursive()
            .Delay(5000)
            .SortBy("Modified")
            .Noop()
            .Idempotent()
            .ReadLock("Changed")
            .Build();

        uri.Should().StartWith("file:///incoming?");
        uri.Should().Contain("include=*.csv");
        uri.Should().Contain("recursive=true");
        uri.Should().Contain("delay=5000");
        uri.Should().Contain("noop=true");
        uri.Should().Contain("idempotent=true");
        uri.Should().Contain("readLock=Changed");
    }

    // ── StreamBody ────────────────────────────────────────────────

    [Fact]
    public void StreamBody_SetsParam()
    {
        var uri = FileDsl.Read("/in").StreamBody().Build();
        uri.Should().Contain("streamBody=true");
    }

    [Fact]
    public void StreamBody_RoundTripParseable()
    {
        var uri = FileDsl.Read("/in").StreamBody().Delete().Build();
        var parsed = EndpointUriParser.Parse(uri);
        parsed.RawParameters["streamBody"].Should().Be("true");
    }

    // ── Round-trip ──────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = FileDsl.Read("/data/input").Include("*.csv").Delay(5000).Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("file");
        parsed.RawParameters["include"].Should().Be("*.csv");
        parsed.RawParameters["delay"].Should().Be("5000");
    }
}

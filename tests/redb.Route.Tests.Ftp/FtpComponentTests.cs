using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Ftp;

namespace redb.Route.Tests.Ftp;

public sealed class FtpComponentTests
{
    [Fact]
    public void Scheme_IsFtp()
    {
        var component = new FtpComponent();
        component.Scheme.Should().Be("ftp");
    }

    [Fact]
    public void CreateEndpoint_ReturnsCorrectType()
    {
        var component = new FtpComponent();
        var uri = CreateUri("/upload", new Dictionary<string, string>
        {
            ["host"] = "myserver"
        });

        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().BeOfType<FtpEndpoint>();
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var component = new FtpComponent();
        var act = () => component.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_ParsesRemotePath()
    {
        var component = new FtpComponent();
        var uri = CreateUri("/upload/data", new Dictionary<string, string>
        {
            ["host"] = "myserver"
        });

        var endpoint = (FtpEndpoint)component.CreateEndpoint(uri);

        endpoint.RemotePath.Should().Be("/upload/data");
    }

    [Fact]
    public void CreateEndpoint_EmptyPath_DefaultsToRoot()
    {
        var component = new FtpComponent();
        var uri = CreateUri("", new Dictionary<string, string>
        {
            ["host"] = "myserver"
        });

        var endpoint = (FtpEndpoint)component.CreateEndpoint(uri);

        endpoint.RemotePath.Should().Be("/");
    }

    [Fact]
    public void CreateEndpoint_NormalizesDoubleSlashes()
    {
        var component = new FtpComponent();
        var uri = CreateUri("/upload//data//", new Dictionary<string, string>
        {
            ["host"] = "myserver"
        });

        var endpoint = (FtpEndpoint)component.CreateEndpoint(uri);

        endpoint.RemotePath.Should().Be("/upload/data");
    }

    [Fact]
    public void CreateEndpoint_BindsOptionsFromUri()
    {
        var component = new FtpComponent();
        var uri = CreateUri("/upload", new Dictionary<string, string>
        {
            ["host"] = "ftp.company.com",
            ["port"] = "2121",
            ["username"] = "admin",
            ["password"] = "secret",
            ["delay"] = "5000",
            ["include"] = "*.csv",
            ["noop"] = "true"
        });

        var endpoint = (FtpEndpoint)component.CreateEndpoint(uri);
        var opts = endpoint.EndpointOptions;

        opts.Host.Should().Be("ftp.company.com");
        opts.Port.Should().Be(2121);
        opts.Username.Should().Be("admin");
        opts.Password.Should().Be("secret");
        opts.Delay.Should().Be(5000);
        opts.Include.Should().Be("*.csv");
        opts.Noop.Should().BeTrue();
    }

    [Fact]
    public void CreateEndpoint_InvalidOptions_Throws()
    {
        var component = new FtpComponent();
        var uri = CreateUri("/upload", new Dictionary<string, string>
        {
            ["host"] = ""
        });

        var act = () => component.CreateEndpoint(uri);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateEndpoint_CreatesProducer()
    {
        var component = new FtpComponent();
        var uri = CreateUri("/upload", new Dictionary<string, string>
        {
            ["host"] = "myserver"
        });

        var endpoint = (FtpEndpoint)component.CreateEndpoint(uri);
        var producer = endpoint.CreateProducer();

        producer.Should().BeOfType<FtpProducer>();
    }

    [Fact]
    public void CreateEndpoint_CreatesConsumer()
    {
        var component = new FtpComponent();
        var uri = CreateUri("/upload", new Dictionary<string, string>
        {
            ["host"] = "myserver"
        });

        var endpoint = (FtpEndpoint)component.CreateEndpoint(uri);
        var processor = Substitute.For<IProcessor>();
        var consumer = endpoint.CreateConsumer(processor);

        consumer.Should().BeOfType<FtpConsumer>();
    }

    [Fact]
    public void CreateEndpoint_NullProcessor_Throws()
    {
        var component = new FtpComponent();
        var uri = CreateUri("/upload", new Dictionary<string, string>
        {
            ["host"] = "myserver"
        });

        var endpoint = (FtpEndpoint)component.CreateEndpoint(uri);
        var act = () => endpoint.CreateConsumer(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_PathWithoutLeadingSlash_Normalized()
    {
        var component = new FtpComponent();
        var uri = CreateUri("upload", new Dictionary<string, string>
        {
            ["host"] = "myserver"
        });

        var endpoint = (FtpEndpoint)component.CreateEndpoint(uri);

        endpoint.RemotePath.Should().Be("/upload");
    }

    private static EndpointUri CreateUri(string path, Dictionary<string, string> parameters)
    {
        return new EndpointUri("ftp", path, $"ftp://{path}", parameters);
    }
}

using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sftp;

namespace redb.Route.Tests.Sftp;

public sealed class SftpComponentTests
{
    [Fact]
    public void Scheme_IsSftp()
    {
        var component = new SftpComponent();
        component.Scheme.Should().Be("sftp");
    }

    [Fact]
    public void CreateEndpoint_ReturnsCorrectType()
    {
        var component = new SftpComponent();
        var uri = CreateUri("/upload", new Dictionary<string, string>
        {
            ["host"] = "myserver",
            ["username"] = "admin",
            ["password"] = "secret"
        });

        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().BeOfType<SftpEndpoint>();
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var component = new SftpComponent();
        var act = () => component.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_ParsesRemotePath()
    {
        var component = new SftpComponent();
        var uri = CreateUri("/upload/data", new Dictionary<string, string>
        {
            ["host"] = "myserver",
            ["username"] = "admin",
            ["password"] = "secret"
        });

        var endpoint = (SftpEndpoint)component.CreateEndpoint(uri);

        endpoint.RemotePath.Should().Be("/upload/data");
    }

    [Fact]
    public void CreateEndpoint_EmptyPath_DefaultsToRoot()
    {
        var component = new SftpComponent();
        var uri = CreateUri("", new Dictionary<string, string>
        {
            ["host"] = "myserver",
            ["username"] = "admin",
            ["password"] = "secret"
        });

        var endpoint = (SftpEndpoint)component.CreateEndpoint(uri);

        endpoint.RemotePath.Should().Be("/");
    }

    [Fact]
    public void CreateEndpoint_NormalizesDoubleSlashes()
    {
        var component = new SftpComponent();
        var uri = CreateUri("/upload//data//", new Dictionary<string, string>
        {
            ["host"] = "myserver",
            ["username"] = "admin",
            ["password"] = "secret"
        });

        var endpoint = (SftpEndpoint)component.CreateEndpoint(uri);

        endpoint.RemotePath.Should().Be("/upload/data");
    }

    [Fact]
    public void CreateEndpoint_BindsOptionsFromUri()
    {
        var component = new SftpComponent();
        var uri = CreateUri("/upload", new Dictionary<string, string>
        {
            ["host"] = "sftp.company.com",
            ["port"] = "2222",
            ["username"] = "admin",
            ["password"] = "secret",
            ["delay"] = "5000",
            ["include"] = "*.csv",
            ["noop"] = "true"
        });

        var endpoint = (SftpEndpoint)component.CreateEndpoint(uri);
        var opts = endpoint.EndpointOptions;

        opts.Host.Should().Be("sftp.company.com");
        opts.Port.Should().Be(2222);
        opts.Username.Should().Be("admin");
        opts.Password.Should().Be("secret");
        opts.Delay.Should().Be(5000);
        opts.Include.Should().Be("*.csv");
        opts.Noop.Should().BeTrue();
    }

    [Fact]
    public void CreateEndpoint_InvalidOptions_Throws()
    {
        var component = new SftpComponent();
        var uri = CreateUri("/upload", new Dictionary<string, string>
        {
            // Missing username and auth
            ["host"] = "myserver"
        });

        var act = () => component.CreateEndpoint(uri);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateEndpoint_CreatesProducer()
    {
        var component = new SftpComponent();
        var uri = CreateUri("/upload", new Dictionary<string, string>
        {
            ["host"] = "myserver",
            ["username"] = "admin",
            ["password"] = "secret"
        });

        var endpoint = (SftpEndpoint)component.CreateEndpoint(uri);
        var producer = endpoint.CreateProducer();

        producer.Should().BeOfType<SftpProducer>();
    }

    [Fact]
    public void CreateEndpoint_CreatesConsumer()
    {
        var component = new SftpComponent();
        var uri = CreateUri("/upload", new Dictionary<string, string>
        {
            ["host"] = "myserver",
            ["username"] = "admin",
            ["password"] = "secret"
        });

        var endpoint = (SftpEndpoint)component.CreateEndpoint(uri);
        var processor = Substitute.For<IProcessor>();
        var consumer = endpoint.CreateConsumer(processor);

        consumer.Should().BeOfType<SftpConsumer>();
    }

    [Fact]
    public void CreateEndpoint_NullProcessor_Throws()
    {
        var component = new SftpComponent();
        var uri = CreateUri("/upload", new Dictionary<string, string>
        {
            ["host"] = "myserver",
            ["username"] = "admin",
            ["password"] = "secret"
        });

        var endpoint = (SftpEndpoint)component.CreateEndpoint(uri);
        var act = () => endpoint.CreateConsumer(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_PathWithoutLeadingSlash_Normalized()
    {
        var component = new SftpComponent();
        var uri = CreateUri("upload", new Dictionary<string, string>
        {
            ["host"] = "myserver",
            ["username"] = "admin",
            ["password"] = "secret"
        });

        var endpoint = (SftpEndpoint)component.CreateEndpoint(uri);

        endpoint.RemotePath.Should().Be("/upload");
    }

    private static EndpointUri CreateUri(string path, Dictionary<string, string> parameters)
    {
        return new EndpointUri("sftp", path, $"sftp://{path}", parameters);
    }
}

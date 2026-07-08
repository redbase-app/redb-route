using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.File;
using redb.Route.GenericFile;

namespace redb.Route.Tests.File;

/// <summary>
/// Tests for FileComponent, FileEndpoint, and FileEndpointOptions.
/// </summary>
public class FileComponentTests
{
    [Fact]
    public void Scheme_ReturnsFile()
    {
        var component = new FileComponent();
        component.Scheme.Should().Be("file");
    }

    [Fact]
    public void CreateEndpoint_ValidUri_ReturnsFileEndpoint()
    {
        var component = new FileComponent();
        var uri = new EndpointUri("file", "/C:/input", "file:///C:/input", new Dictionary<string, string>());

        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().BeOfType<FileEndpoint>();
        endpoint.Uri.Should().BeSameAs(uri);
        endpoint.Component.Should().BeSameAs(component);
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var component = new FileComponent();

        var act = () => component.CreateEndpoint(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_WithOptions_BindsCorrectly()
    {
        var component = new FileComponent();
        var parameters = new Dictionary<string, string>
        {
            ["delay"] = "2000",
            ["include"] = "*.csv",
            ["noop"] = "true",
            ["recursive"] = "true",
            ["sortBy"] = "Modified",
            ["idempotent"] = "true"
        };
        var uri = new EndpointUri("file", "/C:/input", "file:///C:/input", parameters);

        var endpoint = (FileEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Delay.Should().Be(2000);
        endpoint.EndpointOptions.Include.Should().Be("*.csv");
        endpoint.EndpointOptions.Noop.Should().BeTrue();
        endpoint.EndpointOptions.Recursive.Should().BeTrue();
        endpoint.EndpointOptions.SortBy.Should().Be(GenericFileSortBy.Modified);
        endpoint.EndpointOptions.Idempotent.Should().BeTrue();
    }

    [Fact]
    public void CreateEndpoint_ProducerOptions_BindsCorrectly()
    {
        var component = new FileComponent();
        var parameters = new Dictionary<string, string>
        {
            ["fileExist"] = "Append",
            ["tempPrefix"] = ".redb_",
            ["charset"] = "utf-16",
            ["autoCreate"] = "true",
            ["allowNullBody"] = "true",
            ["appendChars"] = "\n"
        };
        var uri = new EndpointUri("file", "/C:/output", "file:///C:/output", parameters);

        var endpoint = (FileEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.FileExist.Should().Be(GenericFileExistStrategy.Append);
        endpoint.EndpointOptions.TempPrefix.Should().Be(".redb_");
        endpoint.EndpointOptions.Charset.Should().Be("utf-16");
        endpoint.EndpointOptions.AutoCreate.Should().BeTrue();
        endpoint.EndpointOptions.AllowNullBody.Should().BeTrue();
        endpoint.EndpointOptions.AppendChars.Should().Be("\n");
    }

    [Fact]
    public void CreateEndpoint_ReadLockOptions_BindsCorrectly()
    {
        var component = new FileComponent();
        var parameters = new Dictionary<string, string>
        {
            ["readLock"] = "Changed",
            ["readLockTimeout"] = "5000",
            ["readLockCheckInterval"] = "500",
            ["readLockMinAge"] = "2000"
        };
        var uri = new EndpointUri("file", "/C:/input", "file:///C:/input", parameters);

        var endpoint = (FileEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.ReadLock.Should().Be(ReadLockStrategy.Changed);
        endpoint.EndpointOptions.ReadLockTimeout.Should().Be(5000);
        endpoint.EndpointOptions.ReadLockCheckInterval.Should().Be(500);
        endpoint.EndpointOptions.ReadLockMinAge.Should().Be(2000);
    }

    [Fact]
    public void Endpoint_DirectoryPath_WindowsPath_NormalizesCorrectly()
    {
        var component = new FileComponent();
        var uri = new EndpointUri("file", "/C:/input/data", "file:///C:/input/data", new Dictionary<string, string>());

        var endpoint = (FileEndpoint)component.CreateEndpoint(uri);

        endpoint.DirectoryPath.Should().Be(Path.GetFullPath("C:/input/data"));
    }

    [Fact]
    public void Endpoint_CreateConsumer_ReturnsFileConsumer()
    {
        var component = new FileComponent();
        var uri = new EndpointUri("file", "/C:/input", "file:///C:/input", new Dictionary<string, string>());
        var endpoint = (FileEndpoint)component.CreateEndpoint(uri);
        var processor = Substitute.For<IProcessor>();

        var consumer = endpoint.CreateConsumer(processor);

        consumer.Should().BeOfType<FileConsumer>();
    }

    [Fact]
    public void Endpoint_CreateConsumer_NullProcessor_Throws()
    {
        var component = new FileComponent();
        var uri = new EndpointUri("file", "/C:/input", "file:///C:/input", new Dictionary<string, string>());
        var endpoint = (FileEndpoint)component.CreateEndpoint(uri);

        var act = () => endpoint.CreateConsumer(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Endpoint_CreateProducer_ReturnsFileProducer()
    {
        var component = new FileComponent();
        var uri = new EndpointUri("file", "/C:/output", "file:///C:/output", new Dictionary<string, string>());
        var endpoint = (FileEndpoint)component.CreateEndpoint(uri);

        var producer = endpoint.CreateProducer();

        producer.Should().BeOfType<FileProducer>();
    }
}

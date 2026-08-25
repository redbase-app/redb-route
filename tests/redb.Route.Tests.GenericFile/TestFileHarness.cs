using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.GenericFile;

namespace redb.Route.Tests.GenericFile;

/// <summary>Options for the in-memory test transport.</summary>
public sealed class TestFileEndpointOptions : GenericFileEndpointOptions
{
    /// <inheritdoc />
    public override void Validate() => ValidateCommon();
}

/// <summary>Component for the in-memory test transport. Scheme: "testfile".</summary>
public sealed class TestFileComponent : ComponentBase
{
    private readonly FakeFileOperations _ops;

    public TestFileComponent(FakeFileOperations ops) => _ops = ops;

    /// <inheritdoc />
    public override string Scheme => "testfile";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        var options = new TestFileEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();
        return new TestFileEndpoint(uri, this, options, _ops);
    }
}

/// <summary>Endpoint for the in-memory test transport.</summary>
public sealed class TestFileEndpoint : EndpointBase<TestFileEndpointOptions>
{
    private readonly FakeFileOperations _ops;

    public TestFileEndpoint(EndpointUri uri, TestFileComponent component, TestFileEndpointOptions options, FakeFileOperations ops)
        : base(uri, component, options)
        => _ops = ops;

    /// <summary>Base directory this endpoint polls or writes to.</summary>
    public string DirectoryPath => Uri.Path.Length == 0 ? "/" : Uri.Path;

    /// <summary>Read-lock outcome injected by tests (null = strategy grants the lock).</summary>
    public Func<GenericFileInfo, bool>? ReadLockGate { get; set; }

    /// <inheritdoc />
    public override IProducer CreateProducer() => new TestFileProducer(this, Options, _ops);

    /// <inheritdoc />
    public override IConsumer CreateConsumer(IProcessor processor)
        => new TestFileConsumer(this, processor, Options, _ops);
}

/// <summary>Consumer over <see cref="FakeFileOperations"/>, exercising the shared poll loop.</summary>
public sealed class TestFileConsumer : GenericFileConsumer<TestFileEndpointOptions>
{
    private readonly TestFileEndpoint _endpoint;

    /// <inheritdoc />
    protected override string ConsumerName => $"testfile:{_endpoint.DirectoryPath}";

    /// <inheritdoc />
    protected override string BasePath => _endpoint.DirectoryPath;

    public TestFileConsumer(TestFileEndpoint endpoint, IProcessor processor, TestFileEndpointOptions options, FakeFileOperations ops)
        : base(endpoint, processor, options, ops)
        => _endpoint = endpoint;

    /// <summary>Runs exactly one poll cycle, synchronously for the test.</summary>
    public Task PollOnceAsync(CancellationToken ct = default) => PollDirectory(ct);

    /// <inheritdoc />
    protected override void SetExchangeHeaders(IMessage message, GenericFileInfo file, string workPath)
    {
        message.Headers["testFile.Name"] = file.Name;
        message.Headers["testFile.AbsolutePath"] = workPath;
        message.Headers["testFile.Length"] = file.Length;
    }

    /// <inheritdoc />
    protected override Task<bool> AcquireReadLockAsync(GenericFileInfo file, CancellationToken ct)
        => Task.FromResult(_endpoint.ReadLockGate?.Invoke(file) ?? true);
}

/// <summary>Producer over <see cref="FakeFileOperations"/>, exercising the shared write flow.</summary>
public sealed class TestFileProducer : GenericFileProducer<TestFileEndpointOptions>
{
    private readonly TestFileEndpoint _endpoint;

    /// <inheritdoc />
    protected override string BasePath => _endpoint.DirectoryPath;

    /// <inheritdoc />
    protected override string FileNameProducedHeader => "testFile.NameProduced";

    public TestFileProducer(TestFileEndpoint endpoint, TestFileEndpointOptions options, FakeFileOperations ops)
        : base(endpoint, options, ops)
        => _endpoint = endpoint;
}

/// <summary>Shared scaffolding for the tests in this project.</summary>
public abstract class GenericFileTestBase
{
    protected const string BaseDir = "/in";

    protected FakeFileOperations Ops { get; } = new();

    protected GenericFileTestBase() => Ops.AddDirectory(BaseDir);

    protected TestFileEndpoint Endpoint(Dictionary<string, string>? parameters = null, string? dir = null)
    {
        var component = new TestFileComponent(Ops);
        var path = dir ?? BaseDir;
        var uri = new EndpointUri("testfile", path, "testfile://" + path, parameters ?? new Dictionary<string, string>());
        return (TestFileEndpoint)component.CreateEndpoint(uri);
    }

    /// <summary>Collecting processor: records every exchange body as UTF-8 text.</summary>
    protected static (IProcessor Processor, List<string> Bodies, List<IExchange> Exchanges) Collector(
        Action<IExchange>? onProcess = null)
    {
        var bodies = new List<string>();
        var exchanges = new List<IExchange>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(call =>
            {
                var exchange = call.Arg<IExchange>();
                exchanges.Add(exchange);
                bodies.Add(exchange.In.Body switch
                {
                    byte[] b => System.Text.Encoding.UTF8.GetString(b),
                    null => "<null>",
                    var other => other.ToString() ?? "<null>"
                });
                onProcess?.Invoke(exchange);
            });
        return (processor, bodies, exchanges);
    }
}

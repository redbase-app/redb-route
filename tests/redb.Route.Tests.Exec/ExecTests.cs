using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Exec;

namespace redb.Route.Tests.Exec;

/// <summary>
/// Unit tests for the exec connector. They actually spawn host processes — we use
/// portable commands (cmd.exe / sh) and short-running probes so the suite stays fast
/// and works on every CI runner.
/// </summary>
public sealed class ExecProducerTests
{
    private static bool IsWindows => OperatingSystem.IsWindows();
    private static string EchoCmd => IsWindows ? "cmd" : "sh";
    private static string[] EchoArgs(string text) =>
        IsWindows ? new[] { "/c", "echo", text } : new[] { "-c", $"echo {text}" };

    private static (ExecProducer producer, ExecEndpoint endpoint) NewProducer(ExecEndpointOptions options)
    {
        var component = new ExecComponent();
        var uri = new EndpointUri("exec", "run", "exec://run", new Dictionary<string, string>());
        var endpoint = new ExecEndpoint(uri, component, options);
        return (new ExecProducer(endpoint, options), endpoint);
    }

    private static IExchange NewExchange(string body = "")
        => Exchange.Create(new Message(body), scopeFactory: null);

    [Fact]
    public async Task Process_RunsCommand_AndPopulatesJsonBodyAndHeaders()
    {
        var options = new ExecEndpointOptions
        {
            Command = EchoCmd,
            Args = string.Join(' ', EchoArgs("hello")),
            TimeoutMs = 5000
        };
        var (producer, _) = NewProducer(options);
        await producer.Start();

        var exchange = NewExchange();

        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        var payload = JsonSerializer.Deserialize<JsonElement>((string)exchange.Out!.Body!);
        payload.GetProperty("exitCode").GetInt32().Should().Be(0);
        payload.GetProperty("stdout").GetString().Should().Contain("hello");
        payload.GetProperty("timedOut").GetBoolean().Should().BeFalse();

        exchange.Out.Headers[ExecHeaders.ExitCode].Should().Be(0);
        exchange.Out.Headers[ExecHeaders.TimedOut].Should().Be(false);
        exchange.Out.Headers[ExecHeaders.CommandLine].Should().NotBeNull();
    }

    [Fact]
    public async Task Process_HonoursAllowlist_AndRejectsForeignCommand()
    {
        var options = new ExecEndpointOptions
        {
            Command = EchoCmd,
            Args = string.Join(' ', EchoArgs("nope")),
            AllowedCommands = "git,ls",
            TimeoutMs = 5000
        };
        var (producer, _) = NewProducer(options);
        await producer.Start();

        var act = () => producer.Process(NewExchange());
        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*not on the allowlist*");
    }

    [Fact]
    public async Task Process_AllowlistMatch_IsCaseInsensitiveOnFileNameOnly()
    {
        var options = new ExecEndpointOptions
        {
            Command = EchoCmd,
            Args = string.Join(' ', EchoArgs("ok")),
            AllowedCommands = IsWindows ? "CMD" : "SH",
            TimeoutMs = 5000
        };
        var (producer, _) = NewProducer(options);
        await producer.Start();

        await producer.Process(NewExchange());
        // No exception => allowlist accepted the command despite case mismatch.
    }

    [Fact]
    public async Task Process_HonoursTimeout_AndKillsProcess()
    {
        // sleep 5 seconds — but we cap at 200ms.
        var options = new ExecEndpointOptions
        {
            Command = IsWindows ? "cmd" : "sh",
            Args = IsWindows ? "/c ping -n 6 127.0.0.1 > nul" : "-c \"sleep 5\"",
            TimeoutMs = 200
        };
        var (producer, _) = NewProducer(options);
        await producer.Start();

        var sw = Stopwatch.StartNew();
        var exchange = NewExchange();
        await producer.Process(exchange);
        sw.Stop();

        // Should bail out very quickly (< 2s) — well under the 5s sleep.
        sw.ElapsedMilliseconds.Should().BeLessThan(2_000);
        exchange.Out!.Headers[ExecHeaders.TimedOut].Should().Be(true);
        exchange.Out.Headers[ExecHeaders.ExitCode].Should().Be(-1);
    }

    [Fact]
    public async Task Process_AcceptsJsonBody_OverridingUriOptions()
    {
        var options = new ExecEndpointOptions
        {
            Command = "should-not-run",
            AllowedCommands = $"should-not-run,{(IsWindows ? "cmd" : "sh")}",
            TimeoutMs = 5000
        };
        var (producer, _) = NewProducer(options);
        await producer.Start();

        var argsJson = JsonSerializer.Serialize(EchoArgs("hi-from-json"));
        var jsonBody = $$"""
            {"command": "{{EchoCmd}}", "args": {{argsJson}} }
            """;

        var exchange = NewExchange(jsonBody);
        await producer.Process(exchange);

        var payload = JsonSerializer.Deserialize<JsonElement>((string)exchange.Out!.Body!);
        payload.GetProperty("stdout").GetString().Should().Contain("hi-from-json");
    }

    [Fact]
    public async Task Process_AcceptsHeaderInputs_OverridingUriOptions()
    {
        var options = new ExecEndpointOptions
        {
            Command = "should-not-run",
            AllowedCommands = $"should-not-run,{(IsWindows ? "cmd" : "sh")}",
            TimeoutMs = 5000
        };
        var (producer, _) = NewProducer(options);
        await producer.Start();

        var exchange = NewExchange();
        exchange.In.Headers[ExecHeaders.Command] = EchoCmd;
        exchange.In.Headers[ExecHeaders.Args] = string.Join(' ', EchoArgs("hi-from-header"));

        await producer.Process(exchange);

        var payload = JsonSerializer.Deserialize<JsonElement>((string)exchange.Out!.Body!);
        payload.GetProperty("stdout").GetString().Should().Contain("hi-from-header");
    }

    [Fact]
    public async Task Process_PlainTextOutput_WhenJsonResponseDisabled()
    {
        var options = new ExecEndpointOptions
        {
            Command = EchoCmd,
            Args = string.Join(' ', EchoArgs("plain-out")),
            JsonResponse = false,
            CaptureStderrInBody = false,
            TimeoutMs = 5000
        };
        var (producer, _) = NewProducer(options);
        await producer.Start();

        var exchange = NewExchange();
        await producer.Process(exchange);

        ((string)exchange.Out!.Body!).Should().Contain("plain-out");
        exchange.Out.ContentType.Should().Be("text/plain");
    }

    [Fact]
    public async Task Process_NoCommand_Throws()
    {
        var options = new ExecEndpointOptions { TimeoutMs = 5000 };
        var (producer, _) = NewProducer(options);
        await producer.Start();

        var act = () => producer.Process(NewExchange());
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no command resolved*");
    }
}

public sealed class ExecEndpointOptionsTests
{
    [Fact]
    public void Validate_RejectsNegativeTimeout()
    {
        var o = new ExecEndpointOptions { TimeoutMs = -1 };
        var act = () => o.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*TimeoutMs*");
    }

    [Fact]
    public void Validate_RejectsNonPositiveByteCaps()
    {
        new ExecEndpointOptions { MaxStdoutBytes = 0 }
            .Invoking(o => o.Validate()).Should().Throw<ArgumentException>();

        new ExecEndpointOptions { MaxStderrBytes = 0 }
            .Invoking(o => o.Validate()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_AcceptsZeroTimeoutAsUnlimited()
    {
        var o = new ExecEndpointOptions { TimeoutMs = 0 };
        var act = () => o.Validate();
        act.Should().NotThrow();
    }
}

public sealed class ExecConsumerTests
{
    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("30s", 30_000)]
    [InlineData("5m", 5 * 60 * 1000)]
    [InlineData("1h", 60 * 60 * 1000)]
    public void ParseInterval_AcceptsAllSupportedUnits(string schedule, int expectedMs)
    {
        var ts = ExecConsumer.ParseInterval(schedule);
        ts.Should().NotBeNull();
        ts!.Value.TotalMilliseconds.Should().Be(expectedMs);
    }

    [Theory]
    [InlineData("0 0 * * *")] // cron
    [InlineData("five-seconds")]
    [InlineData("")]
    public void ParseInterval_RejectsNonIntervalSchedules(string schedule)
    {
        ExecConsumer.ParseInterval(schedule).Should().BeNull();
    }
}

public sealed class ExecBuilderTests
{
    [Fact]
    public void Build_EmitsExpectedUri()
    {
        string uri = ExecDsl.Run("git")
            .Args("status", "--short")
            .AllowedCommands("git", "ls")
            .WorkingDirectory("/srv/app")
            .EnvOverride("LANG", "C")
            .TimeoutMs(5000)
            .MaxStdoutBytes(2048)
            .Schedule("30s");

        uri.Should().StartWith("exec://run?");
        uri.Should().Contain("command=git");
        uri.Should().Contain("args=status+--short");
        uri.Should().Contain("allowedCommands=git%2cls");
        uri.Should().Contain("workingDirectory=%2fsrv%2fapp");
        uri.Should().Contain("environmentOverrides=LANG%3dC");
        uri.Should().Contain("timeoutMs=5000");
        uri.Should().Contain("maxStdoutBytes=2048");
        uri.Should().Contain("schedule=30s");
    }

    [Fact]
    public void Build_OmitsUnsetParameters()
    {
        string uri = ExecDsl.Run("ls").Build();
        uri.Should().Be("exec://run?command=ls");
    }
}

public sealed class ExecComponentTests
{
    [Fact]
    public void CreateEndpoint_ReadsOptionsFromUriParameters()
    {
        var component = new ExecComponent();
        var rawParams = new Dictionary<string, string>
        {
            ["command"] = "git",
            ["allowedCommands"] = "git,ls",
            ["timeoutMs"] = "1234"
        };
        var uri = new EndpointUri("exec", "run", "exec://run", rawParams);

        var endpoint = component.CreateEndpoint(uri) as ExecEndpoint;
        endpoint.Should().NotBeNull();
        endpoint!.EndpointOptions.Command.Should().Be("git");
        endpoint.EndpointOptions.AllowedCommands.Should().Be("git,ls");
        endpoint.EndpointOptions.TimeoutMs.Should().Be(1234);
    }

    [Fact]
    public void Component_HasCorrectScheme()
    {
        new ExecComponent().Scheme.Should().Be("exec");
    }
}

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRedbRouteExec_RegistersComponent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRouteContext>(Substitute.For<IRouteContext>());
        services.AddRedbRouteExec();

        var sp = services.BuildServiceProvider();
        var component = sp.GetService<ExecComponent>();
        component.Should().NotBeNull();
        component!.Scheme.Should().Be("exec");
    }
}

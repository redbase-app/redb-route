using redb.Route.Configuration;
using redb.Route.Exec;
using redb.Route.Tests.Llm.TestHelpers;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Tests.Llm.DslShowcase;

/// <summary>
/// Live integration test: a Claude agent calls a <c>shell</c> tool that is wired through
/// the brand-new <see cref="ExecComponent"/>. The whole shape mirrors the existing
/// <see cref="ToolRouteTests"/> pattern — except that the tool's body is a real
/// <c>exec://run</c> producer with an allowlist on the host commands.
/// <para>
/// What this proves:
/// <list type="bullet">
///   <item>The exec connector is reachable from <c>direct:</c> via <c>.AsLlmTool</c>.</item>
///   <item>Claude correctly emits <c>{"command":"...","args":[...]}</c> and the producer
///         deserializes it without manual glue.</item>
///   <item>The allowlist enforces the security envelope — a model trying to call
///         anything other than the listed commands would be rejected.</item>
///   <item>The JSON response shape is what the agent loop expects, so the model can
///         read back stdout and reply to the user.</item>
/// </list>
/// </para>
/// </summary>
[Trait("Category", "LiveLlm")]
[Collection("LiveLlmSerial")]
public sealed class ExecShellToolTests
{
    private static bool IsWindows => OperatingSystem.IsWindows();

    [EnvFact("REDB_LLM_ANT_API03_KEY")]
    public async Task Claude_Haiku_45_ShellTool_RunsAllowedCommand_AndEchoesStdout()
    {
        var shell = new ShellToolRoute();

        await using var host = LiveLlmHost.Build();

        // Mount the exec component on the live host context — the LiveLlmHost
        // base build only registers the LLM component by default.
        host.Context.AddComponent(new ExecComponent());

        host.AddFactory("claude", new LlmConnectionFactory
        {
            Provider = "anthropic",
            ModelId = "claude-haiku-4-5",
            ApiKey = Environment.GetEnvironmentVariable("REDB_LLM_ANT_API03_KEY"),
            Temperature = 0.0,
            MaxTokens = 256
        });

        await host.StartAsync(shell, r =>
        {
            r.From("direct:agent")
                .Process(e => e.In.Headers[LlmHeaders.SystemPrompt] =
                    "Use the shell tool to print the literal token 'redb-shell-ok' to stdout. " +
                    "After receiving the tool's stdout, reply with that exact token and nothing else.")
                .To(LlmDsl.Factory("claude").Tools("shell").MaxIterations(4).AsUri())
                .To("mock:done");
        });

        await host.SendAsync("direct:agent", "Print 'redb-shell-ok'.");

        // The model must have invoked the shell tool at least once.
        shell.LastStdout.Should().NotBeNull("Claude must call the shell tool");
        shell.LastStdout.Should().Contain("redb-shell-ok",
            "the shell tool must have echoed our literal token");

        var sink = host.Mock("mock:done");
        sink.ReceivedCount.Should().Be(1);
        var final = ((string)sink.ReceivedExchanges[0].In.Body!).ToLowerInvariant();
        final.Should().Contain("redb-shell-ok",
            "the agent's final reply must echo what the shell tool produced");
    }

    /// <summary>
    /// Tool route that dispatches to <c>exec://run</c> with a tight allowlist. The route
    /// captures the producer's stdout for assertions — production usage would skip the
    /// capture and just chain to whatever sink the route needs.
    /// </summary>
    private sealed class ShellToolRoute : RouteBuilder
    {
        public string? LastStdout { get; private set; }

        protected override void Configure()
        {
            // Same description-and-schema shape any LLM tool ABI expects.
            // Allowlist is the security envelope — anything outside this list is rejected
            // before the OS process ever starts.
            From("direct:tool-shell")
                .AsLlmTool("shell")
                    .Description(
                        "Run a small shell command. Input: {\"command\":\"<name>\",\"args\":[\"...\"]}. " +
                        "Output: {\"stdout\":\"...\",\"stderr\":\"...\",\"exitCode\":N}. " +
                        $"Allowed commands: {(IsWindows ? "cmd" : "sh")}.")
                    .Input("""
                        {
                          "type": "object",
                          "properties": {
                            "command": { "type": "string" },
                            "args":    { "type": "array", "items": { "type": "string" } }
                          },
                          "required": ["command"]
                        }
                        """)
                    .SideEffect(ToolSideEffect.ReadOnly)
                    .Cost(ToolCostClass.Cheap)
                .Then()
                .To(ExecDsl.Run()
                    .AllowedCommands(IsWindows ? "cmd" : "sh")
                    .TimeoutMs(5_000)
                    .MaxStdoutBytes(8_192)
                    .MaxStderrBytes(8_192))
                .Process(e =>
                {
                    // Capture for the test — never required in production tool routes.
                    // PipelineProcessor merges Out → In between steps, so the producer's
                    // headers land on e.In here, not e.Out.
                    if (e.In.Headers.TryGetValue(ExecHeaders.Stdout, out var s) && s is string str)
                        LastStdout = str;
                });
        }
    }
}

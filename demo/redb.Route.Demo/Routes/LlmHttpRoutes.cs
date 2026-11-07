using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Exec;
using redb.Route.Llm;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Extensions;
using LlmDsl = redb.Route.Llm.Fluent.Llm;

namespace redb.Route.Demo.Routes;

/// <summary>
/// HTTP showcase for the LLM connector — designed to be the simplest possible
/// curl→Claude→reply round-trip, with full technical logging along the way.
///
/// <para>Two endpoints, both readable top-down like a Camel example:</para>
/// <list type="bullet">
///   <item>
///     <c>POST /api/llm/ask</c> — body is the user prompt, route asks Claude Haiku,
///     logs token usage + stop reason, returns the model's reply as plain text.
///   </item>
///   <item>
///     <c>POST /api/llm/shell</c> — same flow, but the agent has a <c>shell</c> tool
///     wired through <see cref="ExecComponent"/>. Try:
///     <c>curl -d "list the current directory" http://localhost:5088/api/llm/shell</c>.
///   </item>
/// </list>
///
/// <para>Wiring lives in <see cref="InitRoute.main"/>: <c>ExecComponent</c> is added
/// to the context and a <c>"haiku"</c> <see cref="LlmConnectionFactory"/> is placed
/// in the registry with <c>REDB_LLM_ANT_API03_KEY</c> as its API key.</para>
/// </summary>
internal sealed class LlmHttpRoutes : RouteBuilder
{
    private static bool IsWindows => OperatingSystem.IsWindows();

    private readonly ILogger? _log;
    public LlmHttpRoutes(ILogger? log) => _log = log;

    protected override void Configure()
    {
        ConfigureAskEndpoint();
        ConfigureShellAgentEndpoint();
        ConfigureShellTool();
    }

    /// <summary>
    /// Plain chat: prompt in, reply out. No tools, single iteration. The whole
    /// route is six steps — exactly what a Camel reader expects. Uses the
    /// <c>llm://</c> endpoint form so it works in any host without needing
    /// <c>IRouteContext</c> on the DI container.
    /// <para>Conversation memory: pass <c>-H "X-Chat-Id: my-chat"</c> with curl
    /// to keep history across calls; missing header falls back to "default".</para>
    /// </summary>
    private void ConfigureAskEndpoint()
    {
        From("http:0.0.0.0:5088/api/llm/ask?inOut=true")
            .RouteId("llm-http-ask")
            .ConvertBody<string>()
            .Process(e =>
            {
                e.In.Headers[LlmHeaders.SystemPrompt] = "Reply in one short, friendly sentence.";
                e.In.Headers[LlmHeaders.ConversationId] =
                    e.In.Headers.TryGetValue("X-Chat-Id", out var v) && v is string s && s.Length > 0
                        ? s : "default";
            })
            .Log("[LLM-ASK] ▶ chat=${header.llm.conversation.id} prompt=${body}")
            .To(LlmDsl.Factory("haiku")
                .ConversationFromHeader()
                .MaxIterations(1)
                .Temperature(0.2)
                .AsUri())
            .Log("[LLM-ASK] ◀ provider=${header.llm.provider.id} model=${header.llm.model.id} " +
                 "tokensIn=${header.llm.tokens.in} tokensOut=${header.llm.tokens.out} stop=${header.llm.stop_reason}")
            .Log("[LLM-ASK] ◀ reply=${body}");
    }

    /// <summary>
    /// Tool-using agent: Claude can call the <c>shell</c> tool to inspect the host.
    /// Same compact shape — the only extra knob is <c>.Tools("shell")</c>. Conversation
    /// memory works the same way: pass <c>-H "X-Chat-Id: my-chat"</c> across calls.
    /// </summary>
    private void ConfigureShellAgentEndpoint()
    {
        var systemPrompt =
            "You can run small shell commands through the 'shell' tool. " +
            $"The host is {(IsWindows ? "Windows (use cmd /c)" : "Linux (use sh -c)")}. " +
            "Use the tool when the user asks about the system, files, or commands; " +
            "then summarise what you learned in one short sentence.";

        From("http:0.0.0.0:5088/api/llm/shell?inOut=true")
            .RouteId("llm-http-shell")
            .ConvertBody<string>()
            .Process(e =>
            {
                e.In.Headers[LlmHeaders.SystemPrompt] = systemPrompt;
                e.In.Headers[LlmHeaders.ConversationId] =
                    e.In.Headers.TryGetValue("X-Chat-Id", out var v) && v is string s && s.Length > 0
                        ? s : "default";
            })
            .Log("[LLM-SHELL] ▶ chat=${header.llm.conversation.id} prompt=${body}")
            .To(LlmDsl.Factory("haiku")
                .Tools("shell")
                .ConversationFromHeader()
                .MaxIterations(10)
                .Temperature(0.0)
                .AsUri())
            .Log("[LLM-SHELL] ◀ iters=${header.llm.tool.iterations} stop=${header.llm.stop_reason} " +
                 "tokensIn=${header.llm.tokens.in} tokensOut=${header.llm.tokens.out}")
            .Log("[LLM-SHELL] ◀ reply=${body}");
    }

    /// <summary>
    /// The shell tool itself — a tiny tool route that just forwards to
    /// <c>exec://run</c> with a one-command allowlist. The allowlist is the
    /// security envelope: anything outside it is rejected before a process starts.
    /// </summary>
    private void ConfigureShellTool()
    {
        var allowed = IsWindows ? new[] { "cmd", "pwsh", "powershell" } : new[] { "sh", "bash" };

        // Pinned scratch dir so the agent's `Out-File out.html` / `curl -o page.html`
        // don't pollute the Worker's source tree (its CWD when launched via `dotnet run`).
        // Persists between iterations so the model can write then re-read on the next call.
        var scratchDir = Path.Combine(Path.GetTempPath(), "redb-llm-shell");
        Directory.CreateDirectory(scratchDir);

        From("direct:tool-shell")
            .AsLlmTool("shell")
                .Description(
                    "Run a small shell command on the host. Input: " +
                    "{\"command\":\"<name>\",\"args\":[\"...\"]}. Output: " +
                    "{\"stdout\":\"...\",\"stderr\":\"...\",\"exitCode\":N}. " +
                    $"Allowed commands: {string.Join(", ", allowed)}. " +
                    $"Working directory is pinned to '{scratchDir}' — relative paths in " +
                    "your commands resolve there, and files written in one call survive to the next. " +
                    (IsWindows
                        ? "Prefer 'pwsh' (PowerShell 7) for anything involving non-ASCII output; " +
                          "start its commands with `[Console]::OutputEncoding=[Text.Encoding]::UTF8; ` " +
                          "so stdout reaches you as UTF-8 instead of cp866."
                        : "Use 'sh -c \"…\"' for pipelines."))
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
            .Log("[SHELL-TOOL] ▶ in=${body}")
            .To(ExecDsl.Run()
                .AllowedCommands(allowed)
                .WorkingDirectory(scratchDir)
                .TimeoutMs(5_000)
                .MaxStdoutBytes(8_192)
                .MaxStderrBytes(8_192))
            .Log("[SHELL-TOOL] ◀ exit=${header.redbExec.ExitCode} stdoutBytes=${header.redbExec.StdoutBytes}");
    }
}

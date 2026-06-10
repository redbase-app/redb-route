// redb.Route.Demos.LlmHttpShell — single-file standalone demo for the redb.Route.Llm announcement.
// Builds against NuGet packages: redb.Route 3.1.0, redb.Route.Http, redb.Route.Llm, redb.Route.Exec.
//
//   curl -d "how much free disk space?" http://localhost:5088/api/llm/shell
//   curl -d "what did you just say?" -H "X-Chat-Id: my-chat" http://localhost:5088/api/llm/shell
//
// X-Chat-Id keeps history across calls (in-memory, lost on restart).

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Exec;
using redb.Route.Http;
using redb.Route.Llm;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Extensions;

// ─── REDB-BACKED MEMORY (optional) ───────────────────────────────────────────
// To swap the in-memory conversation store for a Postgres-backed one:
//   STEP 1: uncomment the two PackageReference lines in Llm.HttpShell.csproj.
//   STEP 2: uncomment the three `using` lines just below and the
//           `services.AddRedb(...)` line further down.
//   STEP 3: in the AgentEngine constructor, swap `new InMemoryConversationStore()`
//           for `new RedbConversationStore(ctx)` (see the comment there).
//
// RedbConversationStore takes the IRouteContext directly. On every call it asks
// `context.GetRedbService(name, exchange)`, which auto-creates a per-exchange
// IServiceScope (cached in exchange.Properties, disposed when the exchange ends)
// — so we don't need to plumb an IServiceScopeFactory ourselves.
// using redb.Core.Extensions;
// using redb.Postgres.Extensions;
// using redb.Route.Llm.Storage.Redb;

using LlmDsl = redb.Route.Llm.Fluent.Llm;

// ─── 1. Anthropic key ────────────────────────────────────────────────────────
const string ApiKey = "<your-key>";

// ─── 2. DI + RouteContext ────────────────────────────────────────────────────
RouteContext ctx = null!;

var services = new ServiceCollection();
services.AddLogging(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));
services.AddSingleton<IRouteContext>(_ => ctx);
services.AddSingleton<ILogger>(sp => sp.GetRequiredService<ILoggerFactory>().CreateLogger("redb.Route"));

// STEP 2 of 3 — register the default (unnamed) IRedbService. RedbConversationStore
// will pick it up via context.GetRedbService("", exchange) → per-exchange scope.
// services.AddRedb(o => o.UsePostgres("Host=localhost;Port=5432;Username=postgres;Password=1;Database=redb"));

var sp = services.BuildServiceProvider();
ctx = new RouteContext(sp, contextId: "llm-http-shell");

// RouteContext.GetService<T>() looks only in its own service map (no fallback to the
// IServiceProvider), so .Log(...) steps stay silent unless we hand it the logger factory:
ctx.AddService(typeof(ILoggerFactory), sp.GetRequiredService<ILoggerFactory>());

// ─── 3. Components: HTTP + LLM + Exec ────────────────────────────────────────
ctx.AddComponent(new HttpComponent { ServerManager = new SharedHttpServerManager() });
ctx.AddComponent(new LlmComponent());
ctx.AddComponent(new ExecComponent());

// ─── 4. Connection factory: Claude Haiku via the OpenAI-compat surface ──────
ctx.AddToRegistry("haiku", new LlmConnectionFactory
{
    Name        = "haiku",
    Provider    = "anthropic",
    ModelId     = "claude-haiku-4-5",
    ApiKey      = ApiKey,
    Temperature = 0.0,
    MaxTokens   = 512
});

// ─── 5. Agent engine + tool registry ─────────────────────────────────────────
var toolRegistry = new ToolDescriptorRegistry();
ctx.AddService(typeof(IToolDescriptorRegistry), toolRegistry);

var producerTemplate = new ProducerTemplate(ctx);
ctx.AddService(typeof(IProducerTemplate), producerTemplate);

var engine = new AgentEngine(
    logger:           null,
    producerTemplate: producerTemplate,
    observer:         new NoopAgentObserver(),
    budget:           new NoopBudgetEnforcer(),
    approval:         new AutoApproveGate(),
    redaction:        new NoopRedactionFilter(),
    shadow:           new NoopShadowRunner(),
    conversation:     new InMemoryConversationStore(),  // X-Chat-Id history (lost on restart)
    // STEP 3 of 3 — swap the line above for the line below to persist chat history in Postgres:
    // conversation:     new RedbConversationStore(ctx),
    idempotency:      null,
    approvalStore:    null);
ctx.AddService(typeof(IAgentEngine), engine);

// ─── 6. Routes ───────────────────────────────────────────────────────────────
var isWindows  = OperatingSystem.IsWindows();
var allowed    = isWindows ? new[] { "cmd", "pwsh", "powershell" } : new[] { "sh", "bash" };
var scratchDir = Path.Combine(Path.GetTempPath(), "redb-llm-shell");
Directory.CreateDirectory(scratchDir);

var systemPrompt =
    "You can run small shell commands through the 'shell' tool. " +
    $"The host is {(isWindows ? "Windows (use cmd /c)" : "Linux (use sh -c)")}. " +
    "Use the tool when the user asks about the system, files, or commands; " +
    "then summarise what you learned in one short sentence.";

ctx.AddRoutes(r =>
{
    // (a) HTTP endpoint — Claude Haiku with the 'shell' tool wired in.
    r.From("http:0.0.0.0:5088/api/llm/shell?inOut=true")
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

    // (b) The shell tool itself — a tiny route that forwards to exec://run with
    //     a one-command allowlist. The allowlist is the security envelope:
    //     anything outside it is rejected before a process starts.
    r.From("direct:tool-shell")
        .AsLlmTool("shell")
            .Description(
                "Run a small shell command on the host. Input: " +
                "{\"command\":\"<name>\",\"args\":[\"...\"]}. Output: " +
                "{\"stdout\":\"...\",\"stderr\":\"...\",\"exitCode\":N}. " +
                $"Allowed commands: {string.Join(", ", allowed)}. " +
                $"Working directory is pinned to '{scratchDir}'.")
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
        .Log("[SHELL-TOOL] ◀ exit=${header.redbExec.ExitCode} body=${body}");
});

// ─── 7. Start + block ────────────────────────────────────────────────────────
await ctx.Start();
producerTemplate.Start();

Console.WriteLine();
Console.WriteLine("redb.Route.Llm — HTTP + Claude + shell tool demo");
Console.WriteLine("Listening on http://localhost:5088/api/llm/shell");
Console.WriteLine();
Console.WriteLine("Try:");
Console.WriteLine("  curl -d \"how much free disk space?\" http://localhost:5088/api/llm/shell");
Console.WriteLine("  curl -d \"what did you just say?\" -H \"X-Chat-Id: my-chat\" http://localhost:5088/api/llm/shell");
Console.WriteLine();
Console.WriteLine("Press Ctrl+C to stop.");

var stop = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
stop.Wait();

await ctx.DisposeAsync();

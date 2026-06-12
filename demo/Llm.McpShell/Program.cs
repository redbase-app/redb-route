// redb.Route.Demos.LlmMcpShell — single-file standalone demo for the redb.Route.Llm.Mcp announcement.
// Builds against NuGet packages: redb.Route 3.1.1, redb.Route.Http, redb.Route.Llm, redb.Route.Llm.Mcp.
//
//   curl -d "what top-level types are in redb.Route/src/redb.Route.Llm.Mcp/McpProducer.cs?" \
//     http://localhost:5089/api/llm/mcp
//   curl -d "and what about McpEndpoint.cs?" -H "X-Chat-Id: my-chat" \
//     http://localhost:5089/api/llm/mcp
//
// X-Chat-Id keeps history across calls (in-memory, lost on restart).
//
// What this demo shows:
//   1. Spawn an external MCP server (Serena — https://github.com/oraios/serena) over stdio.
//   2. Run `initialize` + `tools/list` once at startup.
//   3. Project every discovered tool into the redb.Route.Llm tool registry as an
//      ILlmToolDescriptor. From the agent's point of view they look exactly like
//      `.AsLlmTool(...)` tools — full audit / governance / cancellation inherited.
//   4. Talk to Claude Haiku and let it call those tools to inspect this very codebase.
//
// The killer point: not a single line of Serena-specific C# is written here. Swap Serena
// for filesystem / git / fetch / github / sqlite / your-own — the same wiring works.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Llm;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Engine;
using redb.Route.Llm.Engine.Governance;
using redb.Route.Llm.Engine.Observability;
using redb.Route.Llm.Engine.Storage;
using redb.Route.Llm.Mcp;
using redb.Route.Llm.Mcp.Transport;

using LlmDsl = redb.Route.Llm.Fluent.Llm;

// ─── 1. Anthropic key ────────────────────────────────────────────────────────
const string ApiKey = "<your-key>";

// ─── 2. Serena launch — point at any project you want the agent to inspect ──
// Mirrors a `.mcp.json` "Serena" entry. Drop in any other MCP server here:
//   filesystem: `npx -y @modelcontextprotocol/server-filesystem /path/to/dir`
//   github:     `npx -y @modelcontextprotocol/server-github`
//   fetch:      `uvx mcp-server-fetch`
const string ProjectPath = @"C:\Work\redb_code\csharp\redb";  // EDIT to your repo
var serenaTransport = McpTransport.Stdio(
    command: "uvx",
    arguments:
    [
        "--from", "git+https://github.com/oraios/serena",
        "serena", "start-mcp-server",
        "--context", "ide",
        "--mode",    "editing",
        "--mode",    "interactive",
        "--mode",    "one-shot",
        "--project", ProjectPath,
    ]);

// ─── 3. DI + RouteContext ────────────────────────────────────────────────────
RouteContext ctx = null!;

var services = new ServiceCollection();
services.AddLogging(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));
services.AddSingleton<IRouteContext>(_ => ctx);
services.AddSingleton<ILogger>(sp => sp.GetRequiredService<ILoggerFactory>().CreateLogger("redb.Route"));

var sp = services.BuildServiceProvider();
ctx = new RouteContext(sp, contextId: "llm-mcp-shell");
ctx.AddService(typeof(ILoggerFactory), sp.GetRequiredService<ILoggerFactory>());

var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Demo");

// ─── 4. Spawn MCP server, initialize, list tools ─────────────────────────────
log.LogInformation("Spawning Serena MCP server (this may take ~30s on first run while uvx installs it)...");

var mcpRegistry = new McpRegistry();
var mcpClient   = new StdioMcpClient(
    serverName: "serena",
    transport:  serenaTransport,
    logger:     sp.GetRequiredService<ILoggerFactory>().CreateLogger("Mcp.Serena"));

using (var initCts = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
{
    await mcpClient.InitializeAsync(initCts.Token);
}
mcpRegistry.Register(mcpClient);
log.LogInformation("Serena initialized. Status={Status}", mcpClient.Status);

var tools = await mcpClient.ListToolsAsync();
log.LogInformation("Serena published {Count} tools.", tools.Count);

// ─── 5. Components: HTTP + LLM + MCP ─────────────────────────────────────────
ctx.AddComponent(new HttpComponent { ServerManager = new SharedHttpServerManager() });
ctx.AddComponent(new LlmComponent());
ctx.AddComponent(new McpComponent(mcpRegistry));

// ─── 6. Project MCP tools into the agent's tool registry ─────────────────────
// Same shape AddRedbRouteMcp() / McpDiscoveryService would use — just done by hand
// since this demo wires RouteContext manually (no IHost / hosted services).
var toolRegistry = new ToolDescriptorRegistry();
var modelToolNames = new List<string>();
foreach (var tool in tools)
{
    if (string.IsNullOrWhiteSpace(tool.Name)) continue;

    var modelName = McpToolDescriptor.BuildModelFacingName("serena", tool.Name);
    var capability = new LlmToolCapability
    {
        Name        = modelName,
        Description = tool.Description ?? $"MCP tool '{tool.Name}' from server 'serena'.",
        InputSchema = McpToolDescriptor.BuildInputSchema(tool),
        Safety      = new LlmToolSafety
        {
            SideEffect       = ToolSideEffect.External,
            Cost             = ToolCostClass.Cheap,
            RequiresApproval = false,
        },
    };
    toolRegistry.Register(new McpToolDescriptor("serena", tool.Name, capability));
    modelToolNames.Add(modelName);
}
ctx.AddService(typeof(IToolDescriptorRegistry), toolRegistry);

// Pick a focused subset for the demo — let the model call symbol-level tools only.
// Drop the filter (or pass the full list) to give the agent the whole catalogue.
string[] exposedTools =
[
    "serena__get_symbols_overview",
    "serena__find_symbol",
    "serena__search_for_pattern",
    "serena__list_dir",
    "serena__find_file",
];
exposedTools = exposedTools.Where(modelToolNames.Contains).ToArray();
log.LogInformation("Exposing {N} of {Total} tools to the agent: {Tools}",
    exposedTools.Length, modelToolNames.Count, string.Join(", ", exposedTools));

// ─── 7. Connection factory: Claude Haiku ─────────────────────────────────────
ctx.AddToRegistry("haiku", new LlmConnectionFactory
{
    Name        = "haiku",
    Provider    = "anthropic",
    ModelId     = "claude-haiku-4-5",
    ApiKey      = ApiKey,
    Temperature = 0.0,
    MaxTokens   = 1024,
});

// ─── 8. Agent engine ─────────────────────────────────────────────────────────
var producerTemplate = new ProducerTemplate(ctx);
ctx.AddService(typeof(IProducerTemplate), producerTemplate);

var engine = new AgentEngine(
    logger:           sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgentEngine>(),
    producerTemplate: producerTemplate,
    observer:         new NoopAgentObserver(),
    budget:           new NoopBudgetEnforcer(),
    approval:         new AutoApproveGate(),
    redaction:        new NoopRedactionFilter(),
    shadow:           new NoopShadowRunner(),
    conversation:     new InMemoryConversationStore(),
    idempotency:      null,
    approvalStore:    null);
ctx.AddService(typeof(IAgentEngine), engine);

// ─── 9. Routes ───────────────────────────────────────────────────────────────
var systemPrompt =
    "You are a code-archaeology assistant. The user asks questions about a C# repository. " +
    "To answer, call the Serena MCP tools — `serena__get_symbols_overview` for a file's " +
    "top-level types, `serena__find_symbol` to inspect a class/method by name path, " +
    "`serena__search_for_pattern` for free-text grep, `serena__list_dir` / `serena__find_file` " +
    "for navigation. Pass `relative_path` arguments relative to the repo root. " +
    "Reply with a single short paragraph summarising what you found.";

ctx.AddRoutes(r =>
{
    r.From("http:0.0.0.0:5089/api/llm/mcp?inOut=true")
        .RouteId("llm-http-mcp")
        .ConvertBody<string>()
        .Process(e =>
        {
            e.In.Headers[LlmHeaders.SystemPrompt] = systemPrompt;
            e.In.Headers[LlmHeaders.ConversationId] =
                e.In.Headers.TryGetValue("X-Chat-Id", out var v) && v is string s && s.Length > 0
                    ? s : "default";
        })
        .Log("[LLM-MCP] ▶ chat=${header.llm.conversation.id} prompt=${body}")
        .To(LlmDsl.Factory("haiku")
            .Tools(string.Join(",", exposedTools))
            .ConversationFromHeader()
            .MaxIterations(8)
            .Temperature(0.0)
            .AsUri())
        .Log("[LLM-MCP] ◀ iters=${header.llm.tool.iterations} stop=${header.llm.stop_reason} " +
             "tokensIn=${header.llm.tokens.in} tokensOut=${header.llm.tokens.out}")
        .Log("[LLM-MCP] ◀ reply=${body}");
});

// ─── 10. Start + block ───────────────────────────────────────────────────────
await ctx.Start();
producerTemplate.Start();

Console.WriteLine();
Console.WriteLine("redb.Route.Llm.Mcp — HTTP + Claude + Serena MCP demo");
Console.WriteLine($"Project root pinned to: {ProjectPath}");
Console.WriteLine("Listening on http://localhost:5089/api/llm/mcp");
Console.WriteLine();
Console.WriteLine("Try:");
Console.WriteLine("  curl -d \"what top-level types are in redb.Route/src/redb.Route.Llm.Mcp/McpProducer.cs?\" http://localhost:5089/api/llm/mcp");
Console.WriteLine("  curl -d \"find the StdioMcpClient class and tell me what its constructor takes\" http://localhost:5089/api/llm/mcp");
Console.WriteLine("  curl -d \"and the IMcpClient interface?\" -H \"X-Chat-Id: my-chat\" http://localhost:5089/api/llm/mcp");
Console.WriteLine();
Console.WriteLine("Press Ctrl+C to stop.");

var stop = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
stop.Wait();

await mcpClient.DisposeAsync();
await ctx.DisposeAsync();

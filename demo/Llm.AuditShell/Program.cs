// redb.Route.Demos.LlmAuditShell — single-file standalone demo for the redb.Route.Llm 3.1.1 audit extension.
// Builds against ProjectReferences (3.1.1 unreleased). Switch back to PackageReference after publishing.
//
//   curl -d "what is 2+2?" \
//        -H "X-User-Id: alice@acme.com" \
//        -H "X-Tenant: acme-prod" \
//        http://localhost:5090/api/llm/audit
//
//   curl -d "and 3+5?" \
//        -H "X-User-Id: bob@acme.com" \
//        -H "X-Tenant: acme-prod" \
//        -H "X-Chat-Id: bob" \
//        -H "llm.audit.region: eu-west" \
//        http://localhost:5090/api/llm/audit
//
// What this demo shows:
//   1. `.User("${header.X-User-Id}")` — principal stamped on every persisted row.
//   2. `.Audit(key, value)` (repeatable) — free-form tags, literals or `${header.X}` refs.
//   3. `llm.audit.<name>` headers — runtime-injected tags merged at producer time
//      (header wins on collision with the option-side value).
//   4. `.PromptTemplate("triage","v1")` — locks the prompt-template (name, version) pair
//      onto every row so auditors can pin which exact prompt drove an answer.
//
// Why this matters: with `RedbConversationStore` flipped on, the AuditTags column
// is a queryable Dictionary<string,string> on `MessageProps` — REDB Pro speaks
// LINQ-to-SQL over it, so compliance pulls become one-liners:
//
//   var goldUsage = await redb.Query<MessageProps>()
//       .Where(m => m.AuditTags!["tenant"] == "acme-prod" && m.UserId == "alice@acme.com")
//       .ToListAsync();
//
// No JSON marshalling, no client-side scan, no JOIN gymnastics — the dictionary is
// indexed natively (see redb.Examples/E060–E063).

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

// ─── REDB-BACKED AUDIT (optional) ────────────────────────────────────────────
// Swap the default in-memory store for the REDB-backed one to unlock the
// LINQ-to-SQL query above. Steps:
//   STEP 1: uncomment the two PackageReference lines in Llm.AuditShell.csproj.
//   STEP 2: uncomment the three `using` lines just below and the
//           `services.AddRedb(...)` line further down.
//   STEP 3: in the AgentEngine constructor, swap `new InMemoryConversationStore()`
//           for `new RedbConversationStore(ctx)`.
// using redb.Core.Extensions;
// using redb.Postgres.Extensions;
// using redb.Route.Llm.Storage.Redb;

using LlmDsl = redb.Route.Llm.Fluent.Llm;

// ─── 1. Provider key (Anthropic, OpenAI, xAI — anything redb.Route.Llm speaks) ─
const string ApiKey = "<your-key>";

// ─── 2. DI + RouteContext ────────────────────────────────────────────────────
RouteContext ctx = null!;

var services = new ServiceCollection();
services.AddLogging(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));
services.AddSingleton<IRouteContext>(_ => ctx);
services.AddSingleton<ILogger>(sp => sp.GetRequiredService<ILoggerFactory>().CreateLogger("redb.Route"));

// STEP 2 of 3 — register the default unnamed IRedbService.
// services.AddRedb(o => o.UsePostgres("Host=localhost;Port=5432;Username=postgres;Password=1;Database=redb"));

var sp = services.BuildServiceProvider();
ctx = new RouteContext(sp, contextId: "llm-audit-shell");
ctx.AddService(typeof(ILoggerFactory), sp.GetRequiredService<ILoggerFactory>());

// ─── 3. Components: HTTP + LLM ───────────────────────────────────────────────
ctx.AddComponent(new HttpComponent { ServerManager = new SharedHttpServerManager() });
ctx.AddComponent(new LlmComponent());

// ─── 4. Connection factory ───────────────────────────────────────────────────
ctx.AddToRegistry("haiku", new LlmConnectionFactory
{
    Name        = "haiku",
    Provider    = "anthropic",
    ModelId     = "claude-haiku-4-5",
    ApiKey      = ApiKey,
    Temperature = 0.0,
    MaxTokens   = 256
});

// ─── 5. Agent engine + persistent conversation store ─────────────────────────
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
    conversation:     new InMemoryConversationStore(),
    // STEP 3 of 3 — swap the line above for the line below to persist into Postgres,
    // then run the LINQ-by-AuditTags query at the top of the file.
    // conversation:     new RedbConversationStore(ctx),
    idempotency:      null,
    approvalStore:    null);
ctx.AddService(typeof(IAgentEngine), engine);

// ─── 6. Routes ───────────────────────────────────────────────────────────────
var systemPrompt =
    "You are a polite arithmetic assistant. Answer the user's question with " +
    "exactly one short sentence. Do not call tools.";

ctx.AddRoutes(r =>
{
    r.From("http:0.0.0.0:5090/api/llm/audit?inOut=true")
        .RouteId("llm-http-audit")
        .ConvertBody<string>()
        .Process(e =>
        {
            e.In.Headers[LlmHeaders.SystemPrompt] = systemPrompt;
            e.In.Headers[LlmHeaders.ConversationId] =
                e.In.Headers.TryGetValue("X-Chat-Id", out var v) && v is string s && s.Length > 0
                    ? s : "default";
        })
        .Log("[LLM-AUDIT] ▶ user=${header.X-User-Id} tenant=${header.X-Tenant} prompt=${body}")
        .To(LlmDsl.Factory("haiku")
            .ConversationFromHeader()
            // Principal — pulled from the X-User-Id header on each call.
            // Producer falls back to the `llm.user.id` header when the option is absent.
            .User("${header.X-User-Id}")
            // Static + dynamic audit tags. Repeatable — call once per tag.
            // Headers named `llm.audit.<name>` merge on top of these (header wins).
            .Audit("tenant", "${header.X-Tenant}")
            .Audit("env",    "prod")
            .Audit("app",    "audit-shell")
            // Locks (name, version) onto every row independent of the prompt body.
            .PromptTemplate("audit-shell-system", "v1")
            .Temperature(0.0)
            .MaxTokens(256)
            .AsUri())
        .Log("[LLM-AUDIT] ◀ tokensIn=${header.llm.tokens.in} tokensOut=${header.llm.tokens.out}")
        .Log("[LLM-AUDIT] ◀ reply=${body}");
});

// ─── 7. Start + block ────────────────────────────────────────────────────────
await ctx.Start();
producerTemplate.Start();

Console.WriteLine();
Console.WriteLine("redb.Route.Llm — audit-extension demo (UserId + AuditTags + PromptTemplate)");
Console.WriteLine("Listening on http://localhost:5090/api/llm/audit");
Console.WriteLine();
Console.WriteLine("Try:");
Console.WriteLine("  curl -d \"what is 2+2?\" \\");
Console.WriteLine("       -H \"X-User-Id: alice@acme.com\" \\");
Console.WriteLine("       -H \"X-Tenant: acme-prod\" \\");
Console.WriteLine("       http://localhost:5090/api/llm/audit");
Console.WriteLine();
Console.WriteLine("  curl -d \"and 3+5?\" \\");
Console.WriteLine("       -H \"X-User-Id: bob@acme.com\" \\");
Console.WriteLine("       -H \"X-Tenant: acme-prod\" \\");
Console.WriteLine("       -H \"X-Chat-Id: bob\" \\");
Console.WriteLine("       -H \"llm.audit.region: eu-west\" \\");
Console.WriteLine("       http://localhost:5090/api/llm/audit");
Console.WriteLine();
Console.WriteLine("Every persisted MessageProps row carries:");
Console.WriteLine("  UserId               = X-User-Id header value");
Console.WriteLine("  AuditTags[\"tenant\"]  = X-Tenant header value");
Console.WriteLine("  AuditTags[\"env\"]     = \"prod\"");
Console.WriteLine("  AuditTags[\"app\"]     = \"audit-shell\"");
Console.WriteLine("  AuditTags[\"region\"]  = injected per-call via llm.audit.region header");
Console.WriteLine("  PromptTemplateName   = \"audit-shell-system\"");
Console.WriteLine("  PromptTemplateVersion= \"v1\"");
Console.WriteLine();
Console.WriteLine("Press Ctrl+C to stop.");

var stop = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
stop.Wait();

await ctx.DisposeAsync();

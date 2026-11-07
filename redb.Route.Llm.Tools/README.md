# redb.Route.Llm.Tools

Optional batteries package for `redb.Route.Llm`. Ships reusable building blocks the agent engine can dispatch to as tools.

Every tool is shipped twice:

1. **As a DSL extension on `IRouteDefinition`** — the canonical, composable form. Plug into any existing route step (LLM tool, AMQP enrichment, HTTP pipeline) by adding `.HttpFetch(opts)` / `.JsonPath()` / etc. just like any other built-in route operator.
2. **As a standalone `RouteBuilder` wrapper** — a thin convenience class (`HttpFetchTool`, `JsonPathTool`, …) that mounts the same logic behind a `direct:` endpoint with the right `AsLlmTool(...)` aspect already attached. Use it when you want to hand a single object to `context.AddRoutes(...)` — for example for external/process tools (shell, RPC) where the `direct:` indirection is the whole point.

Pick **the DSL extension** for everything else.

## Pre-built tools

| DSL extension          | Standalone wrapper      | Endpoint URI (default)        | Side-effect | Cost      | Purpose                                                                        |
|------------------------|-------------------------|-------------------------------|-------------|-----------|--------------------------------------------------------------------------------|
| `.HttpFetch(opts)`     | `HttpFetchTool`         | `direct:llm.http_fetch`       | External    | Moderate  | HTTP GET a URL, return UTF-8 body. Host allowlist, max-bytes, timeout.         |
| `.JsonPath(opts?)`     | `JsonPathTool`          | `direct:llm.json_path`        | ReadOnly    | Cheap     | JSONPath query (Newtonsoft dialect — recursive `..`, wildcards, filters, slices). |
| `.XPath(opts?)`        | `XPathTool`             | `direct:llm.xpath`            | ReadOnly    | Cheap     | XPath 1.0 query backed by `redb.Route.Expressions.XPathExpression`.            |
| `.RegexExtract(opts?)` | `RegexExtractTool`      | `direct:llm.regex_extract`    | ReadOnly    | Cheap     | Extract a substring (or array of substrings) from text by a .NET regex.        |
| `.MathEval(opts?)`     | `MathEvalTool`          | `direct:llm.math_eval`        | ReadOnly    | Cheap     | Value expression eval via redb.Route's `ExpressionResolver` (cached lambdas).  |
| `.TavilyWebSearch(opts)`| `TavilyWebSearchTool`  | `direct:llm.web_search`       | External    | Expensive | Web search via the Tavily API; returns `{answer, results[]}`.                  |

The query/eval extensions (`JsonPath`, `XPath`, `MathEval`) are thin wrappers over redb.Route framework primitives — same engines the rest of the framework uses for `jpath()`/`xpath()`/expression evaluation in routes. Use as-is, or copy the pattern to author your own.

> **Why both forms?** The DSL extension is the canonical way to compose a tool into any route. The standalone wrapper class is kept (a) so external/process tools (HTTP, Tavily, shell) have a clean `direct:` mount point and (b) as a copy-paste reference — every wrapper in this package is literally a one-line bridge `From(uri).AsLlmTool(...).Then().Xxx(opts)`, so they double as the canonical template for authoring your own `RouteBuilder`-shaped tool.

## Wiring — the DSL way (preferred)

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteLlm();

    route.AddRouteBuilder(new InlineToolRoutes());
});

internal sealed class InlineToolRoutes : RouteBuilder
{
    protected override void Configure()
    {
        var fetchOpts = new HttpFetchOptions { HostAllowlist = ["api.example.com"] };
        var tavily    = new TavilyWebSearchOptions { ApiKey = Env.Var("TAVILY_API_KEY") };

        From("direct:llm.http_fetch")
            .AsLlmTool("http_fetch").Description("HTTP GET a URL.").Then()
            .HttpFetch(fetchOpts);

        From("direct:llm.json_path")
            .AsLlmTool("json_path").Description("Query a JSON document via JsonPath.").Then()
            .JsonPath();

        From("direct:llm.xpath")
            .AsLlmTool("xpath").Description("Query an XML document via XPath 1.0.").Then()
            .XPath();

        From("direct:llm.regex_extract")
            .AsLlmTool("regex_extract").Description("Extract text by a .NET regex.").Then()
            .RegexExtract();

        From("direct:llm.math_eval")
            .AsLlmTool("math_eval").Description("Evaluate an arithmetic expression.").Then()
            .MathEval();

        From("direct:llm.web_search")
            .AsLlmTool("web_search").Description("Search the web via Tavily.").Then()
            .TavilyWebSearch(tavily);
    }
}
```

DSL extensions are equally usable as plain enrichment steps in any pipeline — no `AsLlmTool(...)` required:

```csharp
From("amqp:queue:urls")
    .HttpFetch(opts)
    .JsonPath(new JsonPathOptions())   // pre-pull a field
    .To("amqp:queue:enriched");
```

## Wiring — the wrapper way (only for self-contained delivery)

When you want to ship a tool as a single object — typically because the route is exposed only via its `direct:` endpoint and never composed further:

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteLlm();
    route.AddRouteBuilder(new HttpFetchTool(new HttpFetchOptions
    {
        HostAllowlist = ["api.example.com"]
    }));
    route.AddRouteBuilder(new JsonPathTool());
    route.AddRouteBuilder(new XPathTool());
    route.AddRouteBuilder(new RegexExtractTool());
    route.AddRouteBuilder(new MathEvalTool());
    route.AddRouteBuilder(new TavilyWebSearchTool(new TavilyWebSearchOptions
    {
        ApiKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY")!
    }));
});
```

The wrappers do nothing more than `From(opts.EndpointUri).AsLlmTool(...).Then().Xxx(opts)` — the canonical idiom for external/process tools (e.g. the shell-out tool in `demos/Llm.HttpShell`).

Reference the registered tool name in any LLM step:

```csharp
.To(LlmDsl.Factory("openai")
    .Tools("http_fetch", "json_path", "xpath", "regex_extract", "math_eval", "web_search")
    .MaxIterations(6)
    .AsUri())
```

## Tool I/O contracts

### `json_path` / `.JsonPath()`
- **Input:** `{"json":"<document>","path":"$.foo[0].bar"}`
- **Output:** matched value re-serialised as JSON, or `null` if nothing matches.
- **Path syntax:** full Newtonsoft JsonPath dialect — recursive descent (`..`), wildcards (`[*]`), filters (`[?(@.x > 1)]`), slicing (`[1:5]`), plus the simple property/index forms. Same engine that backs `redb.Route.Expressions.JsonPathExpression`.

### `xpath` / `.XPath()`
- **Input:** `{"xml":"<document>","xpath":"//book[1]/title"}`
- **Output:** matched value as a string, or `null` if nothing matches. Use XPath functions like `string(...)` / `count(...)` to coerce node-sets to scalars.
- Backed by `redb.Route.Expressions.XPathExpression` (W3C XPath 1.0 via `System.Xml.XPath`).

### `regex_extract` / `.RegexExtract()`
- **Input:** `{"text":"...","pattern":"...","group":"name|number","all":bool}`
- **Output:** matched string, or array of strings (`all:true`), or `null`.
- `group` selects a capture group; `"0"` or omitted = whole match.
- Pattern execution is bounded by `RegexExtractOptions.MatchTimeout` (default 1 s) — guards against catastrophic backtracking from a careless pattern.

### `math_eval` / `.MathEval()`
- **Input:** `{"expression":"2 * (3 + 4)"}`
- **Output:** result re-serialised as JSON (number, string, bool, or `null`).
- **Grammar:** whatever `redb.Route.Expressions.ExpressionResolver` accepts — arithmetic (`+ - * /`), comparisons, ternary (`?:`), null-coalescing (`??`), boolean operators (`AND`/`OR`/`NOT`), property/header/body refs, `jpath(...)`. Compilation is cached, so repeated calls with the same text are effectively free.

### `http_fetch` / `.HttpFetch()`
- **Input:** `{"url":"https://..."}`
- **Output:** response body as UTF-8 text. Bytes past `HttpFetchOptions.MaxBytes` are truncated; `llm.http_fetch.truncated` header is set in that case.
- **Guards:** `HostAllowlist` (case-insensitive exact match — leave empty only in trusted contexts), `MaxBytes` (default 1 MiB), `Timeout` (default 15 s).

### `web_search` / `.TavilyWebSearch()`
- **Input:** `{"query":"...","max_results":int?}`
- **Output:** `{"answer":"...","results":[{"title","url","content"}]}` — `answer` is omitted if Tavily did not return one.
- Requires a Tavily API key (`TavilyWebSearchOptions.ApiKey`); set `IncludeAnswer=false` if you only want raw results.

## Authoring your own tool

The DSL-extension shape (preferred):

```csharp
public static class MyToolDsl
{
    public static IRouteDefinition MyTool(this IRouteDefinition self, MyToolOptions? options = null)
    {
        options ??= new MyToolOptions();
        return self.Process(async (exchange, ct) =>
        {
            // Read JSON input from exchange.In.Body, write result to exchange.Out.Body.
            // Use LlmToolJson.ParseObject(...) to validate the input shape.
        });
    }
}
```

Wire it on a tool route exactly like the built-ins:

```csharp
From("direct:llm.my_tool")
    .AsLlmTool("my_tool").Description("...").Input(MySchema).Then()
    .MyTool(opts);
```

For external/process tools where you really need a self-contained `RouteBuilder` (shell-out, RPC), keep the wrapper-class idiom — see `HttpFetchTool` / `TavilyWebSearchTool` in this package and the shell tool in `demos/Llm.HttpShell` for examples.

Package depends on `redb.Route.Llm.Abstractions` only — does **not** require the `redb.Route.Llm` engine at compile time. The engine is needed at runtime to actually dispatch the tools.

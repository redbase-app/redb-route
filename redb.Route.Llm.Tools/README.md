# redb.Route.Llm.Tools

Optional batteries package for `redb.Route.Llm`. Ships reference tool implementations that the agent engine can dispatch to via `direct:` endpoints.

## Pre-built tools

| Tool                    | Endpoint URI (default)        | Side-effect | Cost      | Purpose                                                                        |
|-------------------------|-------------------------------|-------------|-----------|--------------------------------------------------------------------------------|
| `HttpFetchTool`         | `direct:llm.http_fetch`       | External    | Moderate  | HTTP GET a URL, return UTF-8 body. Host allowlist, max-bytes, timeout.         |
| `JsonPathTool`          | `direct:llm.json_path`        | ReadOnly    | Cheap     | JSONPath query (Newtonsoft dialect — recursive `..`, wildcards, filters, slices). |
| `XPathTool`             | `direct:llm.xpath`            | ReadOnly    | Cheap     | XPath 1.0 query backed by `redb.Route.Expressions.XPathExpression`.            |
| `RegexExtractTool`      | `direct:llm.regex_extract`    | ReadOnly    | Cheap     | Extract a substring (or array of substrings) from text by a .NET regex.        |
| `MathEvalTool`          | `direct:llm.math_eval`        | ReadOnly    | Cheap     | Value expression eval via redb.Route's `ExpressionResolver` (cached lambdas).  |
| `TavilyWebSearchTool`   | `direct:llm.web_search`       | External    | Expensive | Web search via the Tavily API; returns `{answer, results[]}`.                  |

Each tool is mounted as a `RouteBuilder` and registered as an `ILlmToolDescriptor` via the `.AsLlmTool(...)` DSL aspect. The query/eval tools (`JsonPathTool`, `XPathTool`, `MathEvalTool`) are thin wrappers over redb.Route framework primitives — same engines the rest of the framework uses for `jpath()`/`xpath()`/expression evaluation in routes. Use as-is, or copy the pattern to author your own.

## Wiring

```csharp
services.AddRedbRoute(route =>
{
    route.Services.AddRedbRouteLlm();
    route.AddRouteBuilder(new HttpFetchTool(new HttpFetchOptions
    {
        HostAllowlist = ["api.example.com"],
        MaxBytes = 1_000_000,
        Timeout = TimeSpan.FromSeconds(15)
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

Reference the registered tool name in any LLM step:

```csharp
.To(LlmDsl.Factory("openai")
    .Tools("http_fetch", "json_path", "xpath", "regex_extract", "math_eval", "web_search")
    .MaxIterations(6)
    .AsUri())
```

## Tool I/O contracts

### `json_path`
- **Input:** `{"json":"<document>","path":"$.foo[0].bar"}`
- **Output:** matched value re-serialised as JSON, or `null` if nothing matches.
- **Path syntax:** full Newtonsoft JsonPath dialect — recursive descent (`..`), wildcards (`[*]`), filters (`[?(@.x > 1)]`), slicing (`[1:5]`), plus the simple property/index forms. Same engine that backs `redb.Route.Expressions.JsonPathExpression`.

### `xpath`
- **Input:** `{"xml":"<document>","xpath":"//book[1]/title"}`
- **Output:** matched value as a string, or `null` if nothing matches. Use XPath functions like `string(...)` / `count(...)` to coerce node-sets to scalars.
- Backed by `redb.Route.Expressions.XPathExpression` (W3C XPath 1.0 via `System.Xml.XPath`).

### `regex_extract`
- **Input:** `{"text":"...","pattern":"...","group":"name|number","all":bool}`
- **Output:** matched string, or array of strings (`all:true`), or `null`.
- `group` selects a capture group; `"0"` or omitted = whole match.
- Pattern execution is bounded by `RegexExtractOptions.MatchTimeout` (default 1 s) — guards against catastrophic backtracking from a careless pattern.

### `math_eval`
- **Input:** `{"expression":"2 * (3 + 4)"}`
- **Output:** result re-serialised as JSON (number, string, bool, or `null`).
- **Grammar:** whatever `redb.Route.Expressions.ExpressionResolver` accepts — arithmetic (`+ - * /`), comparisons, ternary (`?:`), null-coalescing (`??`), boolean operators (`AND`/`OR`/`NOT`), property/header/body refs, `jpath(...)`. Compilation is cached, so repeated calls with the same text are effectively free.

### `web_search`
- **Input:** `{"query":"...","max_results":int?}`
- **Output:** `{"answer":"...","results":[{"title","url","content"}]}` — `answer` is omitted if Tavily did not return one.
- Requires a Tavily API key (`TavilyWebSearchOptions.ApiKey`); set `IncludeAnswer=false` if you only want raw results.

## Authoring your own tool

The shape every tool in this package follows:

```csharp
public sealed class MyTool : RouteBuilder
{
    private readonly MyToolOptions _options;
    public MyTool(MyToolOptions options) => _options = options;

    protected override void Configure()
    {
        From(_options.EndpointUri)
            .AsLlmTool(_options.ToolName)
                .Description("What the model sees.")
                .Input(InputSchema)
                .SideEffect(ToolSideEffect.ReadOnly)
                .Cost(ToolCostClass.Cheap)
            .Then()
            .Process(new MyProcessor(_options));
    }
}
```

Read the input from `exchange.In.Body` (it arrives as a JSON string), write the result to `exchange.Out.Body`. Add observability headers under the `llm.<tool>.*` namespace.

Package depends on `redb.Route.Llm.Abstractions` only — does **not** require the `redb.Route.Llm` engine at compile time. The engine is needed at runtime to actually dispatch the tools.

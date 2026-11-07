using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Demo.Routes;

/// <summary>
/// Showcase of the rich, deeply-nested fluent DSL — the same patterns exercised by
/// <c>DeepNestedDslTests</c> in the test suite, but assembled into runnable demo routes.
///
/// What this file demonstrates:
///   • Choice with sibling <c>When()</c> / <c>Otherwise()</c> reachable through
///     <c>IRouteDefinition</c> after a sub-scope was just closed (e.g. <c>.EndSplit().When(...)</c>).
///   • Rich logging scope (<c>.Log(LogLevel.X)</c>) with multiple static and dynamic
///     messages, captured headers, properties and route id rendering.
///   • TryCatch around a step that may throw, with a rich log entry inside the
///     catch handler that prints the actual exception type via <c>e.Exception</c>.
///   • Mixed scope closers: typed <c>EndChoice()</c> / <c>EndSplit()</c> / <c>EndLog()</c>
///     and the universal <c>End()</c> that walks the parent chain to the nearest scope.
///   • Cascading <c>EndChoice()</c> from deep inside a Split inside a When — closes
///     every intermediate scope in one shot and lands back at the route root.
///
/// All comments are intentionally English-only since this file is part of the public demo.
/// </summary>
internal sealed class DeepDslShowcaseRoutes : RouteBuilder
{
    private readonly ILogger? _log;

    /// <summary>Creates the showcase builder. The optional logger is used by inline lambdas.</summary>
    public DeepDslShowcaseRoutes(ILogger? log) => _log = log;

    /// <summary>Hook called by <see cref="RouteBuilder"/> to register every showcase route.</summary>
    protected override void Configure()
    {
        ConfigureChoiceWithSplitAndRichLog();
        ConfigureTryCatchWithRichLogInCatch();
        ConfigureCascadingEndChoiceFromDeepInside();
        ConfigureUniversalEndChain();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Showcase 1 — Choice → When → RichLog → Split → per-item Process + dynamic
    //               Debug log → EndLog → EndSplit, then a sibling When and an
    //               Otherwise reached via the IRouteDefinition extension methods.
    //
    //  Triggered manually via Direct: send a body of either:
    //     - IEnumerable<string>  → list branch (split + upper-case)
    //     - non-empty string     → single string branch
    //     - anything else        → fallback branch
    // ─────────────────────────────────────────────────────────────────────────
    private void ConfigureChoiceWithSplitAndRichLog()
    {
        // A simple in-memory sink so the demo can assert what flowed through.
        var processed = new ConcurrentBag<string>();

        From("direct://demo-choice-richlog")
            .RouteId("demo-choice-richlog")
            .SetHeader("step", "start")
            .Choice()
                // Branch 1: body is a collection of strings (and not a single string).
                .When(e => e.In.Body is IEnumerable<string> && e.In.Body is not string)
                    .SetHeader("branch", "list")

                    // Rich log: multiple pieces composed into one structured message.
                    // .Header("...")   → captures a header value into the output.
                    // .Property("...") → captures an exchange property.
                    // .ShowRouteId(true) → prefixes the log line with [rId:<route>].
                    .Log(LogLevel.Information)
                        .Message("opening list branch")
                        .Header("branch")
                        .Property("trace")
                        .ShowRouteId(true)
                    .EndLog()

                    // Split fans the items out one-by-one through the inner pipeline.
                    .Split(e => ((IEnumerable<string>)e.In.Body!).Cast<object?>())
                        .Process(e =>
                        {
                            // Per-item synchronous transform — uppercase the body string.
                            var s = (string)e.In.Body!;
                            processed.Add(s.ToUpperInvariant());
                        })

                        // ── NOTICE THE DIFFERENCE ──
                        // Both .Log(...) calls below produce equivalent output. The string
                        // template is compiled internally into the same kind of lambda via
                        // ExpressionResolver, but it skips formatting entirely when the log
                        // level is disabled — zero allocation on the hot path.

                        // (A) Lambda — arbitrary C# at runtime.
                        .Log(e => $"[lambda] item={e.In.Body} branch={e.In.Headers["branch"]}")

                        // (B) String template — ${body}, ${header.x}, ${property.y},
                        //     ${exception.type|message}, ${routeId} all resolved by the engine.
                        .Log("[tmpl]   item=${body} branch=${header.branch} [${routeId}]")

                        // (C) Rich-Log scope — structured, multi-line, with headers and
                        //     properties pulled out as separate fields. .Message() itself
                        //     accepts BOTH a string template AND a lambda — both are live.
                        .Log(LogLevel.Information)
                            .Message("[rich-tmpl]   item=${body}")                       // string template
                            .Message(e => $"[rich-lambda] upper={((string)e.In.Body!).ToUpperInvariant()}") // lambda
                            .Header("branch")                                            // pulls header.branch
                            .Property("item-index")                                      // pulls property.item-index (if set)
                            .ShowRouteId(true)
                        .EndLog()
                    .EndSplit()  // returns IRouteDefinition, but we are still logically inside the When

                    // Sibling-aware: .Log(...) here lives on the When body, not on the Split.
                    .Log("list branch done [${routeId}]")

                // Branch 2: a non-empty string. Notice that .When() works after .EndSplit()
                // even though the static type is IRouteDefinition — the extension method
                // walks up the parent chain and finds the enclosing ChoiceDefinition.
                .When(e => e.In.Body is string s && s.Length > 0)
                    .SetHeader("branch", "string")
                    .Process(e => processed.Add($"STR:{e.In.Body}"))

                // Fallback branch — same .Otherwise() extension trick.
                .Otherwise()
                    .SetHeader("branch", "fallback")
                    .Process(e => processed.Add("FALLBACK"))
            .EndChoice()

            // Final summary log lives on the route root, not on any branch.
            .Log(LogLevel.Information)
                .Message("route complete")
                .ShowRouteId(true)
            .EndLog();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Showcase 2 — TryCatch nested inside a When, with a rich log inside the
    //               catch handler that reports the real exception type.
    //
    //  Triggered manually via Direct: any string body throws inside the try.
    // ─────────────────────────────────────────────────────────────────────────
    private void ConfigureTryCatchWithRichLogInCatch()
    {
        From("direct://demo-trycatch-richlog")
            .RouteId("demo-trycatch-richlog")
            .Choice()
                .When(e => e.In.Body is string)
                    .TryCatch()
                        // The protected body — always throws for the demo.
                        .Process(e => throw new InvalidOperationException("boom"))
                    .DoCatch<InvalidOperationException>()
                        // ── NOTICE THE DIFFERENCE ──
                        // Both lines log the caught exception. The string-template form is
                        // compiled to a similar lambda internally, but stays readable in DSL.

                        // (A) Lambda — explicit access to exchange.Exception.
                        .Log(e => $"[lambda] caught: {e.Exception?.GetType().Name}")

                        // (B) String template — ${exception.type|message} resolve exchange.Exception.
                        .Log("[tmpl]   caught: ${exception.type} — ${exception.message} [${routeId}]")

                        // (C) Rich-Log scope — both .Message(string) and .Message(lambda)
                        //     execute; ${exception.*} placeholders work inside the template.
                        .Log(LogLevel.Warning)
                            .Message("[rich-tmpl]   ${exception.type}: ${exception.message}")     // template
                            .Message(e => $"[rich-lambda] stack-top={e.Exception?.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}") // lambda
                            .ShowRouteId(true)
                        .EndLog()
                        .Process(e => e.In.Headers["caught"] = true)
                    .EndTryCatch()
            .EndChoice();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Showcase 3 — Cascading EndChoice() from deep inside a Split inside a When.
    //
    //  The new End* extensions on IRouteDefinition walk the parent chain looking
    //  for a scope of the requested type and return its parent. Calling
    //  .EndChoice() from inside a Log inside a Split inside a When skips straight
    //  back to the route root in one shot — there is no per-scope finalization
    //  to run, only parent-chain navigation, so this is semantically identical
    //  to chaining .EndLog().EndSplit().EndChoice() but is more concise when
    //  intermediate scopes do not need any extra steps.
    // ─────────────────────────────────────────────────────────────────────────
    private void ConfigureCascadingEndChoiceFromDeepInside()
    {
        From("direct://demo-cascade-endchoice")
            .RouteId("demo-cascade-endchoice")
            .Choice()
                .When(e => true)
                    .Split(e => new object?[] { 1, 2, 3 })
                        .Process(e => { /* per-item work */ })
                        .Log("item=${body}")
                    // .EndChoice() walks the parent chain past Split and When
                    // and lands at the route root — equivalent to the explicit
                    // .EndSplit().EndChoice() chain.
                    .EndChoice()

            // Back at the route root. Subsequent steps land here.
            .SetHeader("post-cascade", "ok")
            .Log("cascade demo done");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Showcase 4 — Universal .End() chain. Every scope-class implements
    //               IRouteScope.End(), and there is also an extension End() on
    //               IRouteDefinition that walks up to the nearest scope. Both
    //               compose to produce a clean closing chain.
    // ─────────────────────────────────────────────────────────────────────────
    private void ConfigureUniversalEndChain()
    {
        From("direct://demo-universal-end")
            .RouteId("demo-universal-end")
            .Choice()
                .When(e => true)
                    .Split(e => new object?[] { "a", "b" })
                        .Log(LogLevel.Information)
                            .Message("inside")
                        .End()   // closes the RichLog scope, returns the Split body
                    .End()        // closes the Split scope,   returns the When body
                .EndChoice()      // closes the Choice scope,  returns the route root

            .SetHeader("after-close", "ok");
    }
}

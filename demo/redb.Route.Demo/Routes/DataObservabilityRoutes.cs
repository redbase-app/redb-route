using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Processors;
using redb.Route.Serialization;
using static redb.Route.Demo.Routes.DemoEndpoints;
using static redb.Route.Demo.Routes.DemoHelpers;

namespace redb.Route.Demo.Routes;

/// <summary>
/// Data & observability showcase: Validate, Marshal/ConvertBody, Traced/Metered,
/// Expression DSL (JPath/XPath/Header/Body/Property/Constant/Expr/Exchange),
/// and the Predicate DSL (comparison, string, null, logic, choice, jpath).
/// </summary>
internal sealed class DataObservabilityRoutes : RouteBuilder
{
    private readonly ILogger? _log;
    public DataObservabilityRoutes(ILogger? log) => _log = log;

    protected override void Configure()
    {
        ConfigureValidationRoute();
        ConfigureMarshalRoute();
        ConfigureObservabilityRoute();
        ConfigureExpressionShowcase();
        ConfigurePredicateShowcase();
    }

    /// <summary>
    /// Validation — JSON Schema + predicate checks.
    /// Invalid messages get rejected before entering the pipeline.
    /// </summary>
    private void ConfigureValidationRoute()
    {
        From("direct://demo-validation")
            .RouteId("demo-validation")
            .Log("[VALID] ▶ Validating message...")

            // JSON Schema (throws on failure)
            .ValidateJsonSchema(MessageSchema)
            .Log("[VALID] ✓ JSON schema OK")

            // Predicate (custom business rule)
            .Validate(
                e => e.In.Body?.ToString()?.Length < 10000,
                errorMessage: "Body too large (max 10KB)")
            .Log("[VALID] ✓ Size check OK")

            .Log("[VALID] ◀ All validations passed");
    }

    /// <summary>
    /// Marshal / ConvertBody — serialization pipeline.
    /// Object → JSON bytes → string.
    /// </summary>
    private void ConfigureMarshalRoute()
    {
        From("direct://demo-marshal")
            .RouteId("demo-marshal")
            .Log("[MARSHAL] ▶ Starting serialization demo...")

            // Create a typed object
            .SetBody(e => new { name = "demo", value = 42, ts = DateTime.UtcNow })
            .Log("[MARSHAL]   Created anonymous object")

            // Marshal → JSON bytes
            .Marshal(typeof(JsonMessageSerializer))
            .Log("[MARSHAL]   After Marshal (JSON): contentType=${contentType}")

            // ConvertBody → string
            .ConvertBody<string>()
            .Log("[MARSHAL]   After ConvertBody<string>: ${body}")

            .Log("[MARSHAL] ◀ Serialization round-trip complete");
    }

    /// <summary>
    /// Traced + Metered blocks — observability without code changes.
    /// Traced creates spans, Metered records counters/histograms.
    /// </summary>
    private void ConfigureObservabilityRoute()
    {
        From("direct://demo-observability")
            .RouteId("demo-observability")
            .Log("[OBS] ▶ Starting observable pipeline...")

            // Traced block — all steps inside get one span
            .Traced("demo-traced-block")
                .Log("[OBS]   traced: step 1 — validate")
                .Validate(e => e.In.Body != null, "Body required")
                .Log("[OBS]   traced: step 2 — transform")
                .Process(e => e.In.Body = e.In.Body?.ToString()?.ToUpperInvariant())
            .EndTraced()

            // Metered block — records execution time + counter
            .Metered("demo-metered-block")
                .Log("[OBS]   metered: step 1 — enrich")
                .SetHeader("obs.enriched", "true")
                .Log("[OBS]   metered: step 2 — delay 10ms")
                .Delay(TimeSpan.FromMilliseconds(10))
            .EndMetered()

            // Inline traced — single step with action
            .Traced("final-stamp", e => e.In.Headers["obs.traced"] = "done")

            // Inline metered — single step with action
            .Metered("final-count", e => e.In.Headers["obs.metered"] = "done")

            .Log("[OBS] ◀ Observable pipeline complete");
    }

    /// <summary>
    /// Expression showcase — all the ways to read/write exchange data.
    /// JPath, XPath, Expr(), Body(), Header(), Property(), Constant(), Exchange().
    /// </summary>
    private void ConfigureExpressionShowcase()
    {
        From("direct://demo-expressions")
            .RouteId("demo-expressions")

            // ── JSON body for JPath ──
            .SetBody(e => "{\"user\":{\"name\":\"Alice\",\"age\":30},\"items\":[\"a\",\"b\",\"c\"]}")
            .Log("[EXPR] ▶ JSON body: ${body}")

            // JPath — extract from JSON body
            .SetHeader("user.name", JPath("$.user.name"))
            .SetHeader("user.age", JPath<int>("$.user.age"))
            .Log("[EXPR]   JPath: name=${header.user.name}, age=${header.user.age}")

            // Property + Constant expressions
            .SetProperty("origin", Constant("demo-route"))
            .SetProperty("computed", Exchange(e => $"from-{GetHeader(e, "user.name")}"))
            .Log("[EXPR]   Property: origin=${property.origin}, computed=${property.computed}")

            // Expr() — template expression as IExpression
            .SetHeader("greeting", Expr("Hello ${header.user.name}, age ${header.user.age}!"))
            .Log("[EXPR]   Expr: ${header.greeting}")

            // Header() expression used in Filter predicate.
            // Closed immediately with EndFilter() so the XML/XPath showcase below
            // always runs regardless of the user.name value.
            .Filter(Header("user.name").contains("Alice"))
                .Log("[EXPR] ✓ User is Alice (Filter + contains)")
            .EndFilter()

            // Body() expression snapshot
            .SetProperty("bodySnapshot", Body())
            .Log("[EXPR]   Body snapshot saved to property")

            // ── XML body for XPath ──
            .SetBody(e => "<order><item id='1'>Widget</item><item id='2'>Gadget</item></order>")
            .Log("[EXPR]   XML body: ${body}")

            // XPath — extract from XML body
            .SetHeader("firstItem", XPath("/order/item[1]"))
            .Log("[EXPR]   XPath: first item = ${header.firstItem}")

            // RemoveHeader / RemoveProperty cleanup
            .RemoveHeader("tempData")
            .RemoveProperty("bodySnapshot")

            .Log("[EXPR] ◀ Expression showcase complete");
    }

    /// <summary>
    /// Predicate showcase — the entire Expression predicate DSL.
    /// Each predicate is a fluent method on an Expression: <c>Header("x").isEqualTo("y")</c>.
    /// Reads like plain English — that's the whole point of the DSL.
    /// </summary>
    private void ConfigurePredicateShowcase()
    {
        // ── Route 1: Comparison predicates ──────────────────────────────
        //
        //  Each Filter is closed with EndFilter() so the next Filter is a
        //  sibling, not a nested child. Reads top-to-bottom as a flat list
        //  of independent predicate demonstrations.
        //
        From("direct://demo-predicates-compare")
            .RouteId("demo-predicates-compare")
            .Log("[PRED] ▶ Comparison predicates showcase")

            // set up test data
            .SetHeader("score", 85)
            .SetHeader("grade", "B+")
            .SetHeader("level", "senior")

            // isEqualTo — exact match
            .Filter(Header("grade").isEqualTo("B+"))
                .Log("[PRED]   ✓ Header('grade').isEqualTo('B+') → passed")
            .EndFilter()

            // isNotEqualTo — not this value
            .Filter(Header("level").isNotEqualTo("junior"))
                .Log("[PRED]   ✓ Header('level').isNotEqualTo('junior') → passed")
            .EndFilter()

            // isGreaterThan — strictly greater
            .Filter(Header("score").isGreaterThan(50))
                .Log("[PRED]   ✓ Header('score').isGreaterThan(50) → passed (score=85)")
            .EndFilter()

            // isLessThan — strictly less
            .Filter(Header("score").isLessThan(100))
                .Log("[PRED]   ✓ Header('score').isLessThan(100) → passed (score=85)")
            .EndFilter()

            // isGreaterThanOrEqualTo — inclusive lower bound
            .Filter(Header("score").isGreaterThanOrEqualTo(85))
                .Log("[PRED]   ✓ Header('score').isGreaterThanOrEqualTo(85) → passed")
            .EndFilter()

            // isLessThanOrEqualTo — inclusive upper bound
            .Filter(Header("score").isLessThanOrEqualTo(100))
                .Log("[PRED]   ✓ Header('score').isLessThanOrEqualTo(100) → passed")
            .EndFilter()

            // isBetween — range check (inclusive)
            .Filter(Header("score").isBetween(70, 90))
                .Log("[PRED]   ✓ Header('score').isBetween(70, 90) → passed (score=85)")
            .EndFilter()

            .Log("[PRED] ◀ Comparison predicates done");


        // ── Route 2: String predicates ──────────────────────────────────
        From("direct://demo-predicates-string")
            .RouteId("demo-predicates-string")
            .Log("[PRED] ▶ String predicates showcase")

            .SetHeader("email", "alice@example.com")
            .SetHeader("filename", "report-2024.pdf")
            .SetHeader("tag", "urgent-task-x42")

            .Filter(Header("email").contains("@example"))
                .Log("[PRED]   ✓ Header('email').contains('@example') → passed")
            .EndFilter()

            .Filter(Header("email").startsWith("alice"))
                .Log("[PRED]   ✓ Header('email').startsWith('alice') → passed")
            .EndFilter()

            .Filter(Header("filename").endsWith(".pdf"))
                .Log("[PRED]   ✓ Header('filename').endsWith('.pdf') → passed")
            .EndFilter()

            .Filter(Header("tag").regex(@"^urgent-.*-x\d+$"))
                .Log("[PRED]   ✓ Header('tag').regex('^urgent-.*-x\\d+$') → passed")
            .EndFilter()

            .Filter(Header("filename").In("report-2024.pdf", "summary.pdf", "data.csv"))
                .Log("[PRED]   ✓ Header('filename').In('report-2024.pdf', 'summary.pdf', 'data.csv') → passed")
            .EndFilter()

            .Log("[PRED] ◀ String predicates done");


        // ── Route 3: Null checks ────────────────────────────────────────
        From("direct://demo-predicates-null")
            .RouteId("demo-predicates-null")
            .Log("[PRED] ▶ Null-check predicates showcase")

            .SetHeader("existing", "value")
            .RemoveHeader("missing")

            .Filter(Header("existing").isNotNull())
                .Log("[PRED]   ✓ Header('existing').isNotNull() → passed")
            .EndFilter()

            .Filter(Header("missing").isNull())
                .Log("[PRED]   ✓ Header('missing').isNull() → passed")
            .EndFilter()

            .Log("[PRED] ◀ Null-check predicates done");


        // ── Route 4: Logical composition — and / or / not ───────────────
        //
        //  and() / or() / not() are methods on Expression, not IPredicate.
        //  Left side = Expression (evaluates to bool), right side = IPredicate.
        //
        From("direct://demo-predicates-logic")
            .RouteId("demo-predicates-logic")
            .Log("[PRED] ▶ Logical composition predicates showcase")

            .SetHeader("role", "admin")
            .SetHeader("active", true)
            .SetHeader("disabled", false)
            .SetHeader("trust", 9)

            // and — Expression is truthy AND predicate matches
            .Filter(Header("active").and(Header("role").isEqualTo("admin")))
                .Log("[PRED]   ✓ Header('active').and(Header('role').isEqualTo('admin')) → passed")
            .EndFilter()

            // or — Expression is truthy OR predicate matches
            .Filter(Header("disabled").or(Header("role").isEqualTo("admin")))
                .Log("[PRED]   ✓ Header('disabled').or(Header('role').isEqualTo('admin')) → passed")
            .EndFilter()

            // not — negates Expression
            .Filter(Header("disabled").not())
                .Log("[PRED]   ✓ Header('disabled').not() → passed (disabled=false)")
            .EndFilter()

            // complex: active AND trust >= 5
            .Filter(Header("active").and(Header("trust").isGreaterThanOrEqualTo(5)))
                .Log("[PRED]   ✓ Header('active').and(Header('trust').isGreaterThanOrEqualTo(5)) → passed")
            .EndFilter()

            .Log("[PRED] ◀ Logical composition done");


        // ── Route 5: String expressions — Filter(string) & When(string) ─
        //
        //  LogicalPredicate compiles "${header.x}" expressions at runtime.
        //  This is the purely declarative way: no lambdas, just strings.
        //
        From("direct://demo-predicates-string-expr")
            .RouteId("demo-predicates-string-expr")
            .Log("[PRED] ▶ String expression predicates (LogicalPredicate)")

            .SetHeader("status", "active")
            .SetHeader("count", "42")

            // Filter(string) — string expression evaluated as boolean
            .Filter("${header.status}")
                .Log("[PRED]   ✓ Filter('$${header.status}') → 'active' is truthy")
            .EndFilter()

            // Choice + When(string) — declarative branching (nested-lambda DSL)
            .Choice(choice => choice
                .When(new StringExpression("${header.status}"), w => w
                    .SetHeader("pred.branch", "status-truthy"))
                .Otherwise(o => o
                    .SetHeader("pred.branch", "status-falsy")))

            .Log("[PRED] ◀ String expression predicates done");


        // ── Route 6: Predicates in a Choice (fluent Expression predicates) ─
        From("direct://demo-predicates-choice")
            .RouteId("demo-predicates-choice")
            .Log("[PRED] ▶ Choice with Expression predicates")

            .SetHeader("amount", 750)

            .Choice(choice => choice
                // When(IPredicate) — expression-based condition
                .When(Header("amount").isGreaterThanOrEqualTo(1000).Matches, w => w
                    .SetHeader("pred.tier", "premium"))
                .When(Header("amount").isBetween(500, 999).Matches, w => w
                    .SetHeader("pred.tier", "standard"))
                .When(Header("amount").isLessThan(500).Matches, w => w
                    .SetHeader("pred.tier", "basic"))
                .Otherwise(o => o
                    .SetHeader("pred.tier", "fallback")))

            .Log("[PRED] ◀ Choice with predicates done");


        // ── Route 7: JPath predicates — conditions on JSON body fields ──
        From("direct://demo-predicates-jpath")
            .RouteId("demo-predicates-jpath")
            .Log("[PRED] ▶ JPath predicate showcase")

            .SetBody(e => "{\"order\":{\"total\":299.99,\"currency\":\"USD\",\"priority\":\"express\"}}")
            .Log("[PRED]   JSON body: ${body}")

            .Filter(JPath("$.order.currency").isEqualTo("USD"))
                .Log("[PRED]   ✓ JPath('$.order.currency').isEqualTo('USD') → passed")
            .EndFilter()

            .Filter(JPath("$.order.priority").In("express", "overnight"))
                .Log("[PRED]   ✓ JPath('$.order.priority').In('express','overnight') → passed")
            .EndFilter()

            .Filter(JPath("$.order.priority").startsWith("exp"))
                .Log("[PRED]   ✓ JPath('$.order.priority').startsWith('exp') → passed")
            .EndFilter()

            .Log("[PRED] ◀ JPath predicates done");
    }
}

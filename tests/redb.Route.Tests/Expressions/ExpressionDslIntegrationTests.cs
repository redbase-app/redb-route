using FluentAssertions;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Expressions;
using redb.Route.Predicates;
using redb.Route.Processors;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Integration tests verifying that Expression/Predicate system is fully wired into DSL,
/// OldRouteCompiler, and RouteContext end-to-end.
/// </summary>
[Collection("ExpressionResolver")]
public class ExpressionDslIntegrationTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // ─── Filter with IPredicate ───

    [Fact]
    public async Task Filter_WithPredicate_MatchingExchange_PassesThrough()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://filter-pred-match")
                .Filter(new HeaderExpression("amount").isGreaterThan(100))
                .Process(e => captured = e.In.Body);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "high-value" });
        exchange.In.Headers["amount"] = 200;

        var producer = _context.GetEndpoint("direct://filter-pred-match").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        captured.Should().Be("high-value");
    }

    [Fact]
    public async Task Filter_WithPredicate_NonMatchingExchange_Stops()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://filter-pred-no")
                .Filter(new HeaderExpression("amount").isGreaterThan(100))
                .Process(e => captured = e.In.Body);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "low-value" });
        exchange.In.Headers["amount"] = 50;

        var producer = _context.GetEndpoint("direct://filter-pred-no").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        captured.Should().BeNull();
    }

    [Fact]
    public async Task Filter_WithStringExpression_MatchesTrue()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://filter-expr-yes")
                .Filter("${header.enabled}")
                .Process(e => captured = e.In.Body);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "yes" });
        exchange.In.Headers["enabled"] = "true";

        var producer = _context.GetEndpoint("direct://filter-expr-yes").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        captured.Should().Be("yes");
    }

    [Fact]
    public async Task Filter_WithStringExpression_FiltersFalse()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://filter-expr-no")
                .Filter("${header.enabled}")
                .Process(e => captured = e.In.Body);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "no" });
        exchange.In.Headers["enabled"] = "false";

        var producer = _context.GetEndpoint("direct://filter-expr-no").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        captured.Should().BeNull();
    }

    // ─── SetBody with IExpression ───

    [Fact]
    public async Task SetBody_WithExpression_SetsBodyFromHeader()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://setbody-expr")
                .SetBody(new HeaderExpression("source"))
                .Process(e => captured = e.In.Body);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "original" });
        exchange.In.Headers["source"] = "from-header";

        var producer = _context.GetEndpoint("direct://setbody-expr").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        captured.Should().Be("from-header");
    }

    [Fact]
    public async Task SetBody_WithStringExpression_ResolvesTemplate()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://setbody-str")
                .SetBodyExpression("${header.greeting} ${header.name}")
                .Process(e => captured = e.In.Body);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "ignored" });
        exchange.In.Headers["greeting"] = "Hello";
        exchange.In.Headers["name"] = "World";

        var producer = _context.GetEndpoint("direct://setbody-str").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        captured.Should().Be("Hello World");
    }

    // ─── SetHeader with IExpression ───

    [Fact]
    public async Task SetHeader_WithExpression_SetsHeaderFromBody()
    {
        object? capturedHeader = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://setheader-expr")
                .SetHeader("derived", new BodyExpression())
                .Process(e => capturedHeader = e.In.Headers["derived"]);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "body-value" });

        var producer = _context.GetEndpoint("direct://setheader-expr").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        capturedHeader.Should().Be("body-value");
    }

    [Fact]
    public async Task SetHeader_WithStringExpression_ResolvesTemplate()
    {
        object? capturedHeader = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://setheader-str")
                .SetHeaderExpression("fullName", "${header.first} ${header.last}")
                .Process(e => capturedHeader = e.In.Headers["fullName"]);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "ignored" });
        exchange.In.Headers["first"] = "John";
        exchange.In.Headers["last"] = "Doe";

        var producer = _context.GetEndpoint("direct://setheader-str").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        capturedHeader.Should().Be("John Doe");
    }

    // ─── Transform with IExpression ───

    [Fact]
    public async Task Transform_WithExpression_TransformsBody()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://transform-expr")
                .Transform(new DelegateExpression<string>(e => $"Transformed: {e.In.Body}"))
                .Process(e => captured = e.In.Body);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "data" });

        var producer = _context.GetEndpoint("direct://transform-expr").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        captured.Should().Be("Transformed: data");
    }

    [Fact]
    public async Task Transform_WithStringExpression_ResolvesTemplate()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://transform-str")
                .TransformExpression("${header.prefix}-${body}")
                .Process(e => captured = e.In.Body);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "payload" });
        exchange.In.Headers["prefix"] = "MSG";

        var producer = _context.GetEndpoint("direct://transform-str").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        captured.Should().Be("MSG-payload");
    }

    // ─── Choice with IPredicate ───

    [Fact]
    public async Task Choice_WithPredicateWhen_RoutesToCorrectBranch()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://choice-pred")
                .Choice()
                    .When(new HeaderExpression("priority").isGreaterThan(5))
                        .Process(e => captured = "HIGH")
                    .When(new HeaderExpression("priority").isEqualTo(5))
                        .Process(e => captured = "MEDIUM")
                    .Otherwise()
                        .Process(e => captured = "LOW")
                .End();
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://choice-pred").CreateProducer();
        await producer.Start();

        // High priority
        var ex1 = new Exchange(new Message { Body = "order" });
        ex1.In.Headers["priority"] = 8;
        await producer.Process(ex1);
        captured.Should().Be("HIGH");

        // Medium priority
        var ex2 = new Exchange(new Message { Body = "order" });
        ex2.In.Headers["priority"] = 5;
        await producer.Process(ex2);
        captured.Should().Be("MEDIUM");

        // Low priority
        var ex3 = new Exchange(new Message { Body = "order" });
        ex3.In.Headers["priority"] = 2;
        await producer.Process(ex3);
        captured.Should().Be("LOW");
    }

    [Fact]
    public async Task Choice_WithExpressionWhen_RoutesToCorrectBranch()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://choice-expr")
                .Choice()
                    .When("${header.type}")
                        .Process(e => captured = "TYPED")
                    .Otherwise()
                        .Process(e => captured = "DEFAULT")
                .End();
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://choice-expr").CreateProducer();
        await producer.Start();

        // type header truthy
        var ex1 = new Exchange(new Message { Body = "order" });
        ex1.In.Headers["type"] = "true";
        await producer.Process(ex1);
        captured.Should().Be("TYPED");

        // type header falsy
        var ex2 = new Exchange(new Message { Body = "unknown" });
        ex2.In.Headers["type"] = "false";
        await producer.Process(ex2);
        captured.Should().Be("DEFAULT");
    }

    // ─── Log with ${} templates ───

    [Fact]
    public async Task Log_WithTemplatePlaceholders_ResolvesAndDoesNotThrow()
    {
        var processed = false;
        _context.AddRoutes(r =>
        {
            r.From("direct://logtemplate")
                .Log("Processing order ${header.orderId} body: ${body}")
                .Process(e => processed = true);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "test-body" });
        exchange.In.Headers["orderId"] = "ORD-123";

        var producer = _context.GetEndpoint("direct://logtemplate").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        processed.Should().BeTrue();
    }

    [Fact]
    public async Task Log_WithTemplateAndLogLevel_Compiles()
    {
        _context.AddRoutes(r =>
        {
            r.From("direct://logtemplate-debug")
                .Log("Debug: ${body}", LogLevel.Debug);
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "debug-data" });

        var producer = _context.GetEndpoint("direct://logtemplate-debug").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        // No assertion needed — verifies compilation/execution without exceptions
    }

    // ─── Split with IExpression ───

    [Fact]
    public async Task Split_WithExpression_SplitsBody()
    {
        var items = new List<object?>();
        _context.AddRoutes(r =>
        {
            r.From("direct://split-expr")
                .Split(new BodyExpression())
                    .Process(e => { lock (items) { items.Add(e.In.Body); } })
                .End();
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = new[] { "A", "B", "C" } });

        var producer = _context.GetEndpoint("direct://split-expr").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        items.Should().BeEquivalentTo(new[] { "A", "B", "C" });
    }

    // ─── Combined predicates (And / Or / Not) ───

    [Fact]
    public async Task Filter_WithAndPredicate_RequiresBothConditions()
    {
        object? captured = null;
        // AndPredicate(IExpression, IPredicate): left expr must be truthy AND predicate must match
        var predicate = new AndPredicate(
            new HeaderExpression("enabled"),
            new HeaderExpression("currency").isEqualTo("USD"));

        _context.AddRoutes(r =>
        {
            r.From("direct://filter-and")
                .Filter(predicate)
                .Process(e => captured = e.In.Body);
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://filter-and").CreateProducer();
        await producer.Start();

        // Both match: enabled=true, currency=USD
        var ex1 = new Exchange(new Message { Body = "pass" });
        ex1.In.Headers["enabled"] = true;
        ex1.In.Headers["currency"] = "USD";
        await producer.Process(ex1);
        captured.Should().Be("pass");

        // Expression true but predicate false
        captured = null;
        var ex2 = new Exchange(new Message { Body = "fail" });
        ex2.In.Headers["enabled"] = true;
        ex2.In.Headers["currency"] = "EUR";
        await producer.Process(ex2);
        captured.Should().BeNull();

        // Expression false but predicate true
        captured = null;
        var ex3 = new Exchange(new Message { Body = "fail2" });
        ex3.In.Headers["enabled"] = false;
        ex3.In.Headers["currency"] = "USD";
        await producer.Process(ex3);
        captured.Should().BeNull();
    }

    [Fact]
    public async Task Filter_WithOrPredicate_RequiresEitherCondition()
    {
        object? captured = null;
        // OrPredicate(IExpression, IPredicate): expr truthy OR predicate matches
        var predicate = new OrPredicate(
            new HeaderExpression("vip"),
            new HeaderExpression("status").isEqualTo("NEW"));

        _context.AddRoutes(r =>
        {
            r.From("direct://filter-or")
                .Filter(predicate)
                .Process(e => captured = e.In.Body);
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://filter-or").CreateProducer();
        await producer.Start();

        // Expression true (vip=true)
        var ex1 = new Exchange(new Message { Body = "vip-order" });
        ex1.In.Headers["vip"] = true;
        ex1.In.Headers["status"] = "OLD";
        await producer.Process(ex1);
        captured.Should().Be("vip-order");

        // Predicate matches (status=NEW)
        captured = null;
        var ex2 = new Exchange(new Message { Body = "new-order" });
        ex2.In.Headers["vip"] = false;
        ex2.In.Headers["status"] = "NEW";
        await producer.Process(ex2);
        captured.Should().Be("new-order");

        // Neither matches
        captured = null;
        var ex3 = new Exchange(new Message { Body = "done" });
        ex3.In.Headers["vip"] = false;
        ex3.In.Headers["status"] = "DONE";
        await producer.Process(ex3);
        captured.Should().BeNull();
    }

    [Fact]
    public async Task Filter_WithNotPredicate_InvertsPredicate()
    {
        object? captured = null;
        // NotPredicate(IExpression): negates expression's boolean result
        var predicate = new NotPredicate(
            new HeaderExpression("skip"));

        _context.AddRoutes(r =>
        {
            r.From("direct://filter-not")
                .Filter(predicate)
                .Process(e => captured = e.In.Body);
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://filter-not").CreateProducer();
        await producer.Start();

        // skip=true → NOT true = false → stop
        var ex1 = new Exchange(new Message { Body = "skipped" });
        ex1.In.Headers["skip"] = true;
        await producer.Process(ex1);
        captured.Should().BeNull();

        // skip=false → NOT false = true → passes
        var ex2 = new Exchange(new Message { Body = "kept" });
        ex2.In.Headers["skip"] = false;
        await producer.Process(ex2);
        captured.Should().Be("kept");
    }

    // ─── Mixed DSL: expression + lambda steps ───

    [Fact]
    public async Task MixedPipeline_ExpressionAndLambda_WorkTogether()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://mixed")
                .RouteId("mixed-pipeline")
                .SetHeader("processed", new ConstantExpression(true))
                .Filter(new IsNotNullPredicate(new HeaderExpression("source")))
                .Transform(e => $"[{e.In.Headers["source"]}] {e.In.Body}")
                .SetHeaderExpression("label", "${header.source}-processed")
                .Process(e => captured = $"{e.In.Headers["label"]}: {e.In.Body}");
        });

        await _context.Start();
        var exchange = new Exchange(new Message { Body = "data" });
        exchange.In.Headers["source"] = "API";

        var producer = _context.GetEndpoint("direct://mixed").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);

        captured.Should().Be("API-processed: [API] data");
    }

    // ─── Processor unit tests (no engine needed) ───

    [Fact]
    public async Task ExpressionBodyProcessor_SetsBodyFromExpression()
    {
        var processor = new ExpressionBodyProcessor(new ConstantExpression(42));
        var exchange = new Exchange(new Message { Body = "old" });
        await processor.Process(exchange);
        exchange.In.Body.Should().Be(42);
    }

    [Fact]
    public async Task StringExpressionBodyProcessor_ResolvesTemplate()
    {
        var processor = new StringExpressionBodyProcessor("${header.x}");
        var exchange = new Exchange(new Message());
        exchange.In.Headers["x"] = "resolved";
        await processor.Process(exchange);
        exchange.In.Body.Should().Be("resolved");
    }

    [Fact]
    public async Task ExpressionHeaderProcessor_SetsHeaderFromExpression()
    {
        var processor = new ExpressionHeaderProcessor("derived", new BodyExpression());
        var exchange = new Exchange(new Message { Body = "body-val" });
        await processor.Process(exchange);
        exchange.In.Headers["derived"].Should().Be("body-val");
    }

    [Fact]
    public async Task StringExpressionHeaderProcessor_ResolvesTemplate()
    {
        var processor = new StringExpressionHeaderProcessor("full", "${header.a}-${header.b}");
        var exchange = new Exchange(new Message());
        exchange.In.Headers["a"] = "X";
        exchange.In.Headers["b"] = "Y";
        await processor.Process(exchange);
        exchange.In.Headers["full"].Should().Be("X-Y");
    }

    [Fact]
    public async Task TemplateLogProcessor_DoesNotThrow()
    {
        var processor = new TemplateLogProcessor("Order ${header.id} body=${body}", LogLevel.Information);
        var exchange = new Exchange(new Message { Body = "test" });
        exchange.In.Headers["id"] = "123";

        var act = () => processor.Process(exchange);
        await act.Should().NotThrowAsync();
    }

    // ─── Expression fluent API on IExpression ───

    [Fact]
    public async Task Expression_isEqualTo_WorksInFilter()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://eq-filter")
                .Filter(new HeaderExpression("type").isEqualTo("ORDER"))
                .Process(e => captured = e.In.Body);
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://eq-filter").CreateProducer();
        await producer.Start();

        var ex1 = new Exchange(new Message { Body = "match" });
        ex1.In.Headers["type"] = "ORDER";
        await producer.Process(ex1);
        captured.Should().Be("match");

        captured = null;
        var ex2 = new Exchange(new Message { Body = "no-match" });
        ex2.In.Headers["type"] = "PAYMENT";
        await producer.Process(ex2);
        captured.Should().BeNull();
    }

    [Fact]
    public async Task Expression_contains_WorksInFilter()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://contains-filter")
                .Filter(new BodyExpression().contains("important"))
                .Process(e => captured = e.In.Body);
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://contains-filter").CreateProducer();
        await producer.Start();

        var ex1 = new Exchange(new Message { Body = "this is important data" });
        await producer.Process(ex1);
        captured.Should().Be("this is important data");

        captured = null;
        var ex2 = new Exchange(new Message { Body = "nothing special" });
        await producer.Process(ex2);
        captured.Should().BeNull();
    }

    [Fact]
    public async Task Expression_isNull_WorksInChoice()
    {
        object? captured = null;
        _context.AddRoutes(r =>
        {
            r.From("direct://null-choice")
                .Choice()
                    .When(new HeaderExpression("optional").isNull())
                        .Process(e => captured = "MISSING")
                    .Otherwise()
                        .Process(e => captured = "PRESENT")
                .End();
        });

        await _context.Start();
        var producer = _context.GetEndpoint("direct://null-choice").CreateProducer();
        await producer.Start();

        // Header not set → null → predicate matches
        var ex1 = new Exchange(new Message { Body = "test" });
        await producer.Process(ex1);
        captured.Should().Be("MISSING");

        // Header set → predicate does not match
        var ex2 = new Exchange(new Message { Body = "test" });
        ex2.In.Headers["optional"] = "value";
        await producer.Process(ex2);
        captured.Should().Be("PRESENT");
    }

    [Fact]
    public async Task RouteDefinition_RecordsExpressionSteps()
    {
        var def = new RouteDefinition();
        def.From("direct://test")
            .SetBody(new ConstantExpression(42))
            .SetBodyExpression("${body}")
            .SetHeader("h", new BodyExpression())
            .SetHeaderExpression("h2", "${header.h}")
            .Transform(new DelegateExpression<string>(e => "x"))
            .TransformExpression("${body}")
            .Filter(new IsNotNullPredicate(new BodyExpression())).EndFilter()
            .Filter("${header.flag}").EndFilter()
            .Split(new BodyExpression()).End()
            .Log("Test ${body}")
            .Choice()
                .When(new BodyExpression().isEqualTo("x"))
                    .SetBody("matched")
                .When("${header.cond}")
                    .SetBody("expr-matched")
            .End();

        // Verify outputs (live IProcessorDefinition tree) — the live scope graph after CRTP refactor.
        def.Outputs.Should().HaveCountGreaterThan(10);
        def.Outputs.OfType<SetBodyExpressionDefinition>().Should().NotBeEmpty();
        def.Outputs.OfType<SetHeaderExpressionDefinition>().Should().NotBeEmpty();
        def.Outputs.OfType<TransformExpressionDefinition>().Should().NotBeEmpty();
        def.Outputs.OfType<FilterDefinition>().Should().HaveCountGreaterThanOrEqualTo(2);
        def.Outputs.OfType<SplitDefinition>().Should().HaveCount(1);
        def.Outputs.OfType<LogStaticDefinition>().Should().HaveCount(1);
        var choice = def.Outputs.OfType<ChoiceDefinition>().Single();
        choice.Whens.Should().HaveCount(2);
    }
}

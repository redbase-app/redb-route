using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Expressions.Ast;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// <c>messageHistory(kind)</c> — the trail an exchange left through the route as a value in the
/// expression language, so markup (which has no lambdas) can log it and branch on it.
/// </summary>
public class MessageHistoryFunctionTests
{
    private static IExchange ExchangeWithHistory(params (string Node, string Label, double Ms)[] steps)
    {
        var exchange = new Exchange(new Message("payload")) { RouteId = "orders" };
        foreach (var (node, label, ms) in steps)
            MessageHistory.Append(exchange, new MessageHistoryEntry("orders", node, label, ms));
        return exchange;
    }

    private static object? Interpreted(string expression, IExchange exchange)
        => new Parser(new Tokenizer(expression).GetAllTokens()).Parse().Evaluate(exchange);

    private static string Template(string expression, IExchange exchange)
        => ExpressionResolver.ProcessTemplate(expression, exchange);

    // ── The shapes ───────────────────────────────────────────────────

    [Fact]
    public void Table_IsTheDefault_AndListsEveryStep()
    {
        var exchange = ExchangeWithHistory(("s1", "log", 1.5), ("s2", "to(http)", 40));

        var byDefault = Template("${messageHistory()}", exchange);
        var explicitKind = Template("${messageHistory('table')}", exchange);

        byDefault.Should().Be(explicitKind);
        byDefault.Should().Contain("orders").And.Contain("log").And.Contain("to(http)");
    }

    [Fact]
    public void Compact_IsOneLine_InOrder()
    {
        var exchange = ExchangeWithHistory(("s1", "log", 1), ("s2", "choice", 2), ("s3", "to(http)", 3));

        Template("${messageHistory('compact')}", exchange).Should().Be("log > choice > to(http)");
    }

    [Fact]
    public void Json_CarriesNodeAndElapsed()
    {
        var exchange = ExchangeWithHistory(("s1", "log", 2.5));

        var json = Template("${messageHistory('json')}", exchange);

        json.Should().StartWith("[").And.Contain("\"routeId\":\"orders\"")
            .And.Contain("\"label\":\"log\"").And.Contain("\"elapsedMs\":2.5");
    }

    // ── The numbers: the point of the whole function for markup ──────

    [Fact]
    public void Numbers_AnswerCountTotalAndSlowest()
    {
        var exchange = ExchangeWithHistory(("s1", "log", 1), ("s2", "to(http)", 40), ("s3", "save", 9));

        Interpreted("messageHistory('count')", exchange).Should().Be(3);
        Convert.ToDouble(Interpreted("messageHistory('totalMs')", exchange)).Should().BeApproximately(50, 0.001);
        Convert.ToDouble(Interpreted("messageHistory('slowestMs')", exchange)).Should().BeApproximately(40, 0.001);
        Interpreted("messageHistory('slowest')", exchange).Should().Be("to(http)");
        Interpreted("messageHistory('lastNode')", exchange).Should().Be("save");
    }

    [Fact]
    public void SlowestMs_WorksAsAPredicate_InBothEngineBranches()
    {
        var slow = ExchangeWithHistory(("s1", "log", 1), ("s2", "to(http)", 800));
        var quick = ExchangeWithHistory(("s1", "log", 1), ("s2", "to(http)", 5));

        PredicateFactory.FromString("messageHistory('slowestMs') > 500").Matches(slow).Should().BeTrue();
        PredicateFactory.FromString("messageHistory('slowestMs') > 500").Matches(quick).Should().BeFalse();
        Interpreted("messageHistory('slowestMs') > 500", slow).Should().Be(true);
    }

    // ── History off: the one place this function does not fail loud ──

    [Fact]
    public void HistoryOff_GivesEmptyStringAndZero_NotAFailure()
    {
        var exchange = new Exchange(new Message("payload")) { RouteId = "orders" };

        Template("${messageHistory()}", exchange).Should().BeEmpty();
        Template("${messageHistory('compact')}", exchange).Should().BeEmpty();
        Interpreted("messageHistory('count')", exchange).Should().Be(0);
        Convert.ToDouble(Interpreted("messageHistory('slowestMs')", exchange)).Should().Be(0);
        Interpreted("messageHistory('slowest')", exchange).Should().Be(string.Empty);
    }

    // ── An authoring error still fails loud ──────────────────────────

    [Fact]
    public void UnknownKind_Throws_WithTheKnownNames()
    {
        var exchange = ExchangeWithHistory(("s1", "log", 1));

        var act = () => Template("${messageHistory('slowest-step')}", exchange);

        act.Should().Throw<Exception>().WithMessage("*messageHistory()*slowest-step*");
    }

    // ── End to end: a route logs its own trail ───────────────────────

    [Fact]
    public async Task Route_LogsTheTrail_ThroughTheTemplate()
    {
        var logged = new List<string>();
        var context = new RouteContext(options: new RouteEngineOptions { EnableMessageHistory = true });
        context.AddRoutes(r => r
            .From("direct://mh-fn-in")
            .RouteId("mh-fn")
            .Log("trail: ${messageHistory('compact')}", LogLevel.Information)
            .Process(ex => logged.Add(ExpressionResolver.ProcessTemplate("${messageHistory('compact')}", ex))));

        await context.Start();
        try
        {
            var producer = context.GetEndpoint("direct://mh-fn-in").CreateProducer();
            await producer.Start();
            await producer.Process(new Exchange(new Message("x")));

            logged.Should().ContainSingle();
            logged[0].Should().NotBeEmpty("the route ran with message history enabled");
        }
        finally
        {
            await context.DisposeAsync();
        }
    }
}

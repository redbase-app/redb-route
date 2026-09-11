using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using XPathExpr = redb.Route.Expressions.XPathExpression;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// A query language reads the message body by default and whatever the route names otherwise.
/// <para>
/// Without a source, XML that arrived in a header has to be moved into the body before it can be
/// queried — an extra step that also damages the body for the rest of the route.
/// </para>
/// <para>
/// In the language the source is a second argument with the path staying first:
/// <c>xpath('/order/id', header.payload)</c>. Camel writes <c>xpath(input,exp)</c>, source first,
/// which makes the first argument mean different things at one and two arguments; we do not copy
/// that, and the divergence is deliberate.
/// </para>
/// </summary>
[Collection("ExpressionResolver")]
public class ExpressionSourceTests : IDisposable
{
    private const string HeaderXml = "<order><id>H-1</id><vip>yes</vip></order>";
    private const string BodyXml = "<order><id>B-1</id></order>";
    private const string HeaderJson = """{"id":"J-1","vip":true}""";

    public ExpressionSourceTests() => ExpressionResolver.ClearAllCaches();

    public void Dispose()
    {
        ExpressionResolver.ClearAllCaches();
        GC.SuppressFinalize(this);
    }

    private static IExchange Exchange()
    {
        var exchange = new Exchange(new Message(BodyXml));
        exchange.In.Headers["payload"] = HeaderXml;
        exchange.In.Headers["json"] = HeaderJson;
        exchange.Properties["doc"] = HeaderJson;
        return exchange;
    }

    // ── Object form ──

    [Fact]
    public void XPath_reads_the_named_source_instead_of_the_body()
    {
        var exchange = Exchange();

        new XPathExpr("/order/id").Evaluate<string>(exchange).Should().Be("B-1");
        new XPathExpr("/order/id").From(new HeaderExpression("payload"))
            .Evaluate<string>(exchange).Should().Be("H-1");
    }

    [Fact]
    public void JsonPath_reads_the_named_source_instead_of_the_body()
    {
        var exchange = Exchange();

        new JsonPathExpression("$.id").From(new PropertyExpression("doc"))
            .Evaluate<string>(exchange).Should().Be("J-1");
    }

    [Fact]
    public void Reading_a_source_leaves_the_body_alone()
    {
        var exchange = Exchange();

        new XPathExpr("/order/id").From(new HeaderExpression("payload")).Evaluate<string>(exchange);

        exchange.In.Body.Should().Be(BodyXml, "querying a header must not cost the route its body");
    }

    [Fact]
    public void From_returns_a_new_expression_and_leaves_the_original_reading_the_body()
    {
        var onBody = new XPathExpr("/order/id");
        var onHeader = onBody.From(new HeaderExpression("payload"));
        var exchange = Exchange();

        onHeader.Evaluate<string>(exchange).Should().Be("H-1");
        onBody.Evaluate<string>(exchange).Should().Be("B-1");
    }

    [Fact]
    public void The_source_composes_with_the_result_type()
    {
        var exchange = Exchange();

        new XPathExpr("/order/id", XPathResult.String).From(new HeaderExpression("payload"))
            .Evaluate<object>(exchange).Should().Be("H-1");
    }

    [Fact]
    public void A_condition_on_a_source_asks_the_same_question_as_on_the_body()
    {
        var exchange = Exchange();
        var predicate = (IPredicateExpression)new XPathExpr("/order/vip").From(new HeaderExpression("payload"));

        predicate.Matches(exchange).Should().BeTrue("the header's document has a vip element");
        ((IPredicateExpression)new XPathExpr("/order/vip")).Matches(exchange)
            .Should().BeFalse("the body's document does not");
    }

    [Fact]
    public void A_source_that_produced_nothing_says_so_rather_than_blaming_the_body()
    {
        var exchange = Exchange();

        var act = () => new XPathExpr("/order/id").From(new HeaderExpression("absent"))
            .Evaluate<string>(exchange);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*source expression produced no value*");
    }

    // ── Language function, value position ──

    [Fact]
    public void The_language_function_takes_the_source_as_a_second_argument()
    {
        var exchange = Exchange();

        ExpressionResolver.ProcessTemplate("${xpath('/order/id', header.payload)}", exchange)
            .Should().Be("H-1");
    }

    [Fact]
    public void One_argument_still_means_the_body()
    {
        var exchange = Exchange();

        ExpressionResolver.ProcessTemplate("${xpath('/order/id')}", exchange)
            .Should().Be("B-1");
    }

    [Fact]
    public void The_jpath_function_takes_a_source_too()
    {
        var exchange = Exchange();

        ExpressionResolver.ProcessTemplate("${jpath('$.id', header.json)}", exchange)
            .Should().Be("J-1");
    }

    [Fact]
    public void The_source_argument_mixes_with_the_rest_of_the_language()
    {
        var exchange = Exchange();

        ExpressionResolver.ProcessTemplate(
            "${upper(xpath('/order/id', header.payload))}-${xpath('/order/id')}", exchange)
            .Should().Be("H-1-B-1");
    }

    [Fact]
    public void A_comma_inside_the_path_is_part_of_the_path_and_not_a_second_argument()
    {
        var exchange = new Exchange(new Message("<order><line code='1,2'>ok</line></order>"));

        // Telling one argument from two is what decides which engine reads the placeholder, so a
        // comma that belongs to the path must not be mistaken for an argument separator.
        ExpressionResolver.ProcessTemplate("${xpath(/order/line[@code='1,2'])}", exchange)
            .Should().Be("ok");
    }

    [Fact]
    public void A_comma_inside_brackets_is_part_of_a_json_path_too()
    {
        var exchange = new Exchange(new Message("""{"a,b":"ok"}"""));

        ExpressionResolver.ProcessTemplate("${jpath($['a,b'])}", exchange)
            .Should().Be("ok");
    }

    [Fact]
    public void A_missing_source_reads_like_a_missing_body_inside_the_language()
    {
        // Review follow-up. The language is lenient about missing data — one-argument xpath() on a
        // null body yields null — and the first cut of the two-argument form threw instead, so the
        // same language answered the same situation two ways depending on where the data was
        // missing from. Inside ${...} the two must agree; the strict contract lives on the object
        // form, where From(...) naming a source that produced nothing still fails loudly.
        var nullBody = new Exchange(new Message());
        var absentHeader = Exchange();

        var oneArg = ExpressionResolver.ProcessTemplate("[${xpath('/order/id')}]", nullBody);
        var twoArg = ExpressionResolver.ProcessTemplate("[${xpath('/order/id', header.absent)}]", absentHeader);

        twoArg.Should().Be(oneArg);
    }

    [Fact]
    public void The_jpath_function_is_equally_lenient_about_a_missing_source()
    {
        var nullBody = new Exchange(new Message());
        var absentHeader = Exchange();

        var oneArg = ExpressionResolver.ProcessTemplate("[${jpath($.id)}]", nullBody);
        var twoArg = ExpressionResolver.ProcessTemplate("[${jpath('$.id', header.absent)}]", absentHeader);

        twoArg.Should().Be(oneArg);
    }

    [Fact]
    public async Task A_condition_over_a_missing_source_is_no_match_rather_than_an_error()
    {
        var reached = new List<string>();

        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct://src-cond-absent")
            .Filter("xpath('/order/vip', header.absent)")
                .Process(_ => reached.Add("passed"))
            .EndFilter());
        await context.Start();

        var producer = context.GetEndpoint("direct://src-cond-absent").CreateProducer();
        await producer.Start();

        await producer.Process(Exchange());

        reached.Should().BeEmpty("an optional header that is absent means the condition does not hold");
    }

    // ── Language function, condition position ──

    [Fact]
    public async Task A_condition_written_in_the_language_reads_the_source_too()
    {
        var reached = new List<string>();

        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct://src-cond")
            .Filter("xpath('/order/vip', header.payload)")
                .Process(_ => reached.Add("header"))
            .EndFilter());
        await context.Start();

        var producer = context.GetEndpoint("direct://src-cond").CreateProducer();
        await producer.Start();

        var exchange = Exchange();
        await producer.Process(exchange);

        reached.Should().Equal("header");
    }

    [Fact]
    public async Task The_same_condition_without_a_source_reads_the_body_and_finds_nothing()
    {
        var reached = new List<string>();

        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct://src-cond-body")
            .Filter("xpath('/order/vip')")
                .Process(_ => reached.Add("body"))
            .EndFilter());
        await context.Start();

        var producer = context.GetEndpoint("direct://src-cond-body").CreateProducer();
        await producer.Start();

        await producer.Process(Exchange());

        reached.Should().BeEmpty();
    }
}

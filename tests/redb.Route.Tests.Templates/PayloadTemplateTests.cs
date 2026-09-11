using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Templates;
using redb.Route.TestKit;
using static redb.Route.Core.TextSource;

namespace redb.Route.Tests.Templates;

/// <summary>Rendering, escaping and the data model of the payload template step.</summary>
public class PayloadTemplateTests
{
    public sealed class Item { public string Sku { get; set; } = ""; public int Qty { get; set; } }

    public sealed class Order
    {
        public int Id { get; set; }
        public bool Vip { get; set; }
        public List<Item> Items { get; set; } = [];
        public decimal Price { get; set; }
        public DateTime When { get; set; }
        public string Fragment { get; set; } = "";
        public string Explode() => throw new InvalidOperationException("must never be called from a template");
    }

    private static Task<string?> Render(string template, MediaType mediaType, object? body,
        IDictionary<string, object?>? headers = null, Action<TemplateArgs>? args = null, Action<RouteTemplateOptions>? options = null)
        => Render(Inline(template), mediaType, body, headers, args, options);

    private static async Task<string?> Render(TextSource source, MediaType mediaType, object? body,
        IDictionary<string, object?>? headers = null, Action<TemplateArgs>? args = null, Action<RouteTemplateOptions>? options = null)
    {
        await using var ctx = new RouteContext();
        if (options is not null) ctx.UseTemplates(options);
        ctx.AddRoutes(b => b.From("direct://in").SetBodyTemplate(source, mediaType, args));
        await ctx.Start();
        return await ctx.RequestBodyAndHeaders<string>("direct://in", body, headers ?? new Dictionary<string, object?>());
    }

    [Fact]
    public async Task Json_EscapesSubstitutedValues_TemplateLiteralsUntouched()
    {
        var name = "Bob \"the\" Builder\nline2 \\ end";
        var json = await Render("""{"name": "{{ headers.name }}", "n": {{ headers.n }}}""", MediaType.Json, null,
            new Dictionary<string, object?> { ["name"] = name, ["n"] = 5 });

        using var doc = JsonDocument.Parse(json!);
        doc.RootElement.GetProperty("name").GetString().Should().Be(name);
        doc.RootElement.GetProperty("n").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task Xml_EscapesSubstitutedValues()
    {
        var xml = await Render("""<a t="{{ headers.t }}">{{ body }}</a>""", MediaType.Xml, "x < y & z",
            new Dictionary<string, object?> { ["t"] = "it's \"quoted\"" });

        var root = XDocument.Parse(xml!).Root!;
        root.Value.Should().Be("x < y & z");
        root.Attribute("t")!.Value.Should().Be("it's \"quoted\"");
    }

    [Fact]
    public async Task Text_DoesNotEscape()
    {
        (await Render("{{ body }}", MediaType.Text, "<&>\"")).Should().Be("<&>\"");
    }

    // Code review 2026-09-01 (В10): escaping happens once, when a value is written to the result,
    // whatever operators or filters produced it. Hooked into ObjectToString it ran at every conversion.

    [Fact]
    public async Task Json_EscapesOnce_WhateverOperatorOrFilterProducedTheValue()
    {
        var json = await Render(
            """{"a":"{{ headers.name }}","b":"{{ headers.name + '!' }}","c":"{{ headers.first + ' ' + headers.name }}","d":"{{ headers.tags | array.join ',' }}"}""",
            MediaType.Json, null,
            new Dictionary<string, object?> { ["name"] = "O\"Brien", ["first"] = "Sean", ["tags"] = new List<string> { "a\"b", "c" } });

        using var doc = JsonDocument.Parse(json!);
        doc.RootElement.GetProperty("a").GetString().Should().Be("O\"Brien");
        doc.RootElement.GetProperty("b").GetString().Should().Be("O\"Brien!", "a value that went through an operator is escaped once, not twice");
        doc.RootElement.GetProperty("c").GetString().Should().Be("Sean O\"Brien");
        doc.RootElement.GetProperty("d").GetString().Should().Be("a\"b,c");
    }

    [Fact]
    public async Task Xml_EscapesOnce_WhateverOperatorProducedTheValue()
    {
        var xml = await Render("""<r a="{{ headers.city + '!' }}"/>""", MediaType.Xml, null,
            new Dictionary<string, object?> { ["city"] = "A&B" });

        XDocument.Parse(xml!).Root!.Attribute("a")!.Value.Should().Be("A&B!");
    }

    [Fact]
    public async Task Xml_ReplacesCharactersIllegalInXml()
    {
        var xml = await Render("<r>{{ headers.v }}</r>", MediaType.Xml, null,
            new Dictionary<string, object?> { ["v"] = "ACK\u0008END" });

        XDocument.Parse(xml!).Root!.Value.Should().Be("ACK\uFFFDEND", "a control character has no representation in XML 1.0");
    }

    [Fact]
    public async Task Body_ContentType_FollowsMediaType()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://ct").SetBodyTemplate(Inline("{}"), MediaType.Json).To("mock://ct"));
        await ctx.Start();

        await ctx.SendBody("direct://ct", "ignored");

        ctx.Mock("mock://ct").ReceivedExchanges[0].In.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task Loop_And_Condition_OverPocoBody_WithCSharpMemberNames()
    {
        var order = new Order { Vip = true, Items = [new() { Sku = "a", Qty = 1 }, new() { Sku = "b", Qty = 2 }] };

        var text = await Render("{{ for i in body.Items }}{{ i.Sku }}:{{ i.Qty }}{{ if !for.last }},{{ end }}{{ end }}{{ if body.Vip }} VIP{{ end }}",
            MediaType.Text, order);

        text.Should().Be("a:1,b:2 VIP");
    }

    [Fact]
    public async Task JsonStringBody_IsParsed_FileTemplate_WithArgs()
    {
        var json = await Render(File("Templates/order-confirm.json.sbn"), MediaType.Json,
            """{"items":[{"sku":"a","qty":1},{"sku":"b","qty":2}]}""",
            new Dictionary<string, object?> { ["orderId"] = 7, ["customerId"] = "C-42" },
            a => a.Set("customer", "header.customerId"));

        using var doc = JsonDocument.Parse(json!);
        doc.RootElement.GetProperty("id").GetInt32().Should().Be(7);
        doc.RootElement.GetProperty("customer").GetString().Should().Be("C-42");
        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("items")[1].GetProperty("sku").GetString().Should().Be("b");
    }

    [Fact]
    public async Task XmlStringBody_IsNavigable()
    {
        var text = await Render("{{ body.order.id }}|{{ body.order.item[1].sku }}|{{ body.order.note }}|{{ body.order.item | array.size }}",
            MediaType.Text, """<order id="7"><item sku="a"/><item sku="b"/><note>hi</note></order>""");

        text.Should().Be("7|b|hi|2");
    }

    [Fact]
    public async Task Args_ExpressionAndConstant()
    {
        var text = await Render("{{ args.total }}/{{ args.channel }}", MediaType.Text, null,
            new Dictionary<string, object?> { ["amount"] = 2, ["qty"] = 3 },
            a => a.Set("total", "header.amount * header.qty").SetValue("channel", "email"));

        text.Should().Be("6/email");
    }

    [Fact]
    public void Args_MalformedExpression_FailsAtDslTime()
    {
        // A broken function call is a syntax error for the AST; "header.a +* 2" would not be — by the
        // literal-name-first rule it is a header literally named "a +* 2" (dashes and spaces are legal).
        var act = () => new TemplateArgs().Set("x", "max(header.a, ");

        act.Should().Throw<Exception>();
    }

    [Fact]
    public async Task Expr_InsideTemplate_AgreesWithDollarTemplateOutside()
    {
        var headers = new Dictionary<string, object?> { ["amount"] = 2.5m, ["qty"] = 4, ["name"] = "kit" };
        var exchange = Exchange.Create(new Message(null), null);
        foreach (var (k, v) in headers) exchange.In.Headers[k] = v;
        var outside = ExpressionResolver.ProcessTemplate("${header.amount * header.qty}-${header.name}", exchange);

        var inside = await Render("{{ expr(\"header.amount * header.qty\") }}-{{ expr(\"header.name\") }}", MediaType.Text, null, headers);

        inside.Should().Be(outside);
    }

    [Fact]
    public async Task HeaderAndPropertyTargets_LeaveBodyAlone()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://targets")
                .SetHeaderTemplate("X-Sum", Inline("{{ headers.a }}+{{ headers.b }}"), MediaType.Text)
                .SetPropertyTemplate("summary", Inline("{{ body }}!"), MediaType.Text)
                .To("mock://targets"));
        await ctx.Start();

        await ctx.SendBodyAndHeaders("direct://targets", "body", new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 });

        var received = ctx.Mock("mock://targets").ReceivedExchanges[0];
        received.In.Headers["X-Sum"].Should().Be("1+2");
        received.Properties["summary"].Should().Be("body!");
        received.In.Body.Should().Be("body");
    }

    [Fact]
    public async Task NumbersAndDates_RenderInvariant_EvenUnderRuCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            var order = new Order { Price = 2.5m, When = new DateTime(2026, 9, 1, 10, 30, 0, DateTimeKind.Utc) };
            var text = await Render("{{ body.Price }}|{{ body.When }}|{{ headers.d }}", MediaType.Text, order,
                new Dictionary<string, object?> { ["d"] = 1234.5 });

            text.Should().Be("2.5|2026-09-01T10:30:00.0000000Z|1234.5");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task Raw_SkipsEscaping_ForPreBuiltFragments()
    {
        var order = new Order { Fragment = """{"x":1}""" };

        var withRaw = await Render("""{"inner": {{ body.Fragment | raw }}}""", MediaType.Json, order);
        var withoutRaw = await Render("""{"inner": "{{ body.Fragment }}"}""", MediaType.Json, order);

        JsonDocument.Parse(withRaw!).RootElement.GetProperty("inner").GetProperty("x").GetInt32().Should().Be(1);
        JsonDocument.Parse(withoutRaw!).RootElement.GetProperty("inner").GetString().Should().Be("""{"x":1}""");
    }

    [Fact]
    public async Task MethodCall_OnBodyObject_IsNotAllowed()
    {
        var act = () => Render("{{ body.Explode() }}", MediaType.Text, new Order());

        var ex = await act.Should().ThrowAsync<Exception>();
        ex.Which.Message.Should().Contain("Explode");
        ex.Which.Should().NotBeOfType<InvalidOperationException>("the method body must never run");
    }

    // Code review 2026-09-01 (В14): the sandbox holds through the expr() bridge and the arguments too.

    private sealed class Probe
    {
        public static bool Executed;
        public string Name => "probe";
        public string Pwn() { Executed = true; return "pwned"; }
    }

    [Fact]
    public async Task Expr_CannotCallMethods_TheSandboxHoldsThroughTheBridge()
    {
        Probe.Executed = false;
        var act = () => Render("{{ expr('body.Pwn()') }}", MediaType.Text, new Probe());

        var ex = await act.Should().ThrowAsync<Exception>();
        ex.Which.Message.Should().Contain("Pwn");
        Probe.Executed.Should().BeFalse("a template reads members, it never runs code");
    }

    [Fact]
    public async Task Expr_StillReadsMembers()
    {
        (await Render("{{ expr('body.Name') }}", MediaType.Text, new Probe())).Should().Be("probe");
    }

    [Fact]
    public async Task Args_CannotCallMethods()
    {
        Probe.Executed = false;
        var act = () => Render("{{ args.x }}", MediaType.Text, new Probe(), args: a => a.Set("x", "body.Pwn()"));

        await act.Should().ThrowAsync<Exception>();
        Probe.Executed.Should().BeFalse();
    }

    [Fact]
    public async Task Liquid_Syntax_WhenEnabled()
    {
        var text = await Render("{{ headers.name | upcase }}", MediaType.Text, null,
            new Dictionary<string, object?> { ["name"] = "bob" }, options: o => o.Liquid = true);

        text.Should().Be("BOB");
    }

    [Fact]
    public async Task MissingVariable_EmptyByDefault_ErrorWhenStrict()
    {
        (await Render("[{{ headers.nope }}]", MediaType.Text, null)).Should().Be("[]");

        var strict = () => Render("[{{ headers.nope }}]", MediaType.Text, null, options: o => o.StrictVariables = true);
        await strict.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Exception_IsVisible_InsideOnException()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.OnException<InvalidOperationException>().Handled()
                .SetBodyTemplate(Inline("""{"error": "{{ exception.message }}", "type": "{{ exception.type }}"}"""), MediaType.Json)
                .To("mock://errors");
            b.From("direct://boom").ThrowException<InvalidOperationException>("bad \"input\"");
        });
        await ctx.Start();

        await ctx.SendBody("direct://boom", "x");

        var body = (string)ctx.Mock("mock://errors").ReceivedExchanges[0].In.Body!;
        var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("error").GetString().Should().Be("bad \"input\"");
        doc.RootElement.GetProperty("type").GetString().Should().Be(nameof(InvalidOperationException));
    }
}

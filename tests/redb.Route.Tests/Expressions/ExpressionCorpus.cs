using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// The shared corpus of expression forms used by the characterisation net. One list of forms, run
/// through every position of the language, so a difference between positions is visible as a row
/// rather than having to be remembered.
/// <para>
/// Adding a form here is cheap and is the right reflex: the snapshot records what it does today,
/// and any later change to the expression system shows up as a diff on that snapshot.
/// </para>
/// </summary>
internal static class ExpressionCorpus
{
    /// <summary>A person, used to exercise member access behind a header, a property and the body.</summary>
    internal sealed class Person
    {
        public string Name { get; init; } = "ann";
        public int Age { get; init; } = 30;
        public bool Active { get; init; } = true;
        public Person? Manager { get; init; }

        /// <summary>A public field, not a property: member access has to read both alike.</summary>
        public int Level = 3;
    }

    /// <summary>Which exchange a form is evaluated against.</summary>
    internal enum Fixture
    {
        /// <summary>Body is a <see cref="Person"/>; the default.</summary>
        Object,

        /// <summary>Body is a plain string.</summary>
        Text,

        /// <summary>Body is a JSON document.</summary>
        Json,

        /// <summary>Body is an XML document.</summary>
        Xml
    }

    /// <summary>One form of the language, with the fixture it is meaningful against.</summary>
    /// <param name="Form">The expression text exactly as an author would write it.</param>
    /// <param name="Group">Section heading in the snapshot, so the file reads as a document.</param>
    /// <param name="Fixture">Which exchange to evaluate it against.</param>
    /// <param name="Templated">
    /// True when the form already carries its own <c>${...}</c>, as the Apache Camel idiom does by
    /// putting the operator outside the placeholder. Wrapping such a form again would only measure
    /// nonsense, so the template column is skipped for it.
    /// </param>
    internal readonly record struct Entry(
        string Form,
        string Group,
        Fixture Fixture = Fixture.Object,
        bool Templated = false);

    internal static IExchange CreateExchange(Fixture fixture)
    {
        object body = fixture switch
        {
            Fixture.Text => "payload",
            Fixture.Json => "{\"order\":{\"id\":7,\"total\":150.5,\"tags\":[\"a\",\"b\"]}}",
            Fixture.Xml => "<order><id>7</id><total>150.5</total></order>",
            _ => new Person { Name = "ann", Age = 30, Active = true, Manager = new Person { Name = "bob", Age = 51 } }
        };

        var exchange = new Exchange(new Message(body));

        exchange.In.Headers["a"] = 42;
        exchange.In.Headers["b"] = 3;
        exchange.In.Headers["zero"] = 0;
        exchange.In.Headers["neg"] = -7;
        exchange.In.Headers["dbl"] = 2.5;
        exchange.In.Headers["flag"] = true;
        exchange.In.Headers["off"] = false;
        exchange.In.Headers["s"] = "hello";
        exchange.In.Headers["empty"] = "";
        exchange.In.Headers["quoted"] = "a > b";
        exchange.In.Headers["worded"] = "x AND y";
        exchange.In.Headers["ct"] = "application/json";
        exchange.In.Headers["dotted.key"] = "literal-dot";
        exchange.In.Headers["Content-Type"] = "text/xml";
        exchange.In.Headers["X-Request-Id"] = "req-1";
        exchange.In.Headers["a+b"] = "plus-name";
        exchange.In.Headers["user"] = new Person();
        exchange.In.Headers["list"] = new List<int> { 1, 2, 3, 4, 5 };

        exchange.Properties["count"] = 7;
        exchange.Properties["zero"] = 0;
        exchange.Properties["flag"] = false;
        exchange.Properties["name"] = "abcd";
        exchange.Properties["items"] = new List<string> { "p", "q", "r" };
        exchange.Properties["cfg"] = new Dictionary<string, object?> { ["enabled"] = true, ["limit"] = 5 };
        exchange.Properties["user"] = new Person();

        exchange.Exception = new InvalidOperationException("boom");

        return exchange;
    }

    internal static IReadOnlyList<Entry> Entries { get; } = Build();

    private static Entry[] Build() =>
    [
        // ── literals ──────────────────────────────────────────────────────────
        new("42", "literals"),
        new("-7", "literals"),
        new("2.5", "literals"),
        new("true", "literals"),
        new("false", "literals"),
        new("'hello'", "literals"),
        new("\"hello\"", "literals"),
        new("'a > b'", "literals"),
        new("'x AND y'", "literals"),
        new("hello", "literals"),
        new("plain text", "literals"),

        // ── strings that look like operators but are configuration ────────────
        new("a>b", "config-shaped literals"),
        new("x<y", "config-shaped literals"),
        new("a==b", "config-shaped literals"),
        new("dGVzdC1zZWNyZXQ=", "config-shaped literals"),
        new("dGVzdC1zZWNyZXQ==", "config-shaped literals"),
        new("<root/>", "config-shaped literals"),
        new("<xml>", "config-shaped literals"),
        new("key=value", "config-shaped literals"),
        new("application/json", "config-shaped literals"),
        new("text/plain; charset=utf-8", "config-shaped literals"),
        new("some/path/file.txt", "config-shaped literals"),
        new("https://example.com/a?b=c", "config-shaped literals"),
        new("2026-08-28", "config-shaped literals"),
        new("black and white", "config-shaped literals"),
        new("yes or no", "config-shaped literals"),
        new("BLACK AND WHITE", "config-shaped literals"),

        // ── accessors ─────────────────────────────────────────────────────────
        new("header.a", "accessors"),
        new("header.zero", "accessors"),
        new("header.neg", "accessors"),
        new("header.flag", "accessors"),
        new("header.off", "accessors"),
        new("header.s", "accessors"),
        new("header.empty", "accessors"),
        new("header.missing", "accessors"),
        new("header.dotted.key", "accessors"),
        new("header.Content-Type", "accessors"),
        new("header.X-Request-Id", "accessors"),
        new("header.a+b", "accessors"),
        new("property.count", "accessors"),
        new("property.zero", "accessors"),
        new("property.flag", "accessors"),
        new("property.missing", "accessors"),
        new("body", "accessors", Fixture.Text),
        new("body", "accessors"),
        new("exception.Message", "accessors"),
        new("contentType", "accessors"),

        // ── member access behind an accessor ──────────────────────────────────
        new("header.user.Name", "member access"),
        new("header.user.Age", "member access"),
        new("header.user.Active", "member access"),
        new("header.user.Manager", "member access"),
        new("header.list.Count", "member access"),
        new("header.s.Length", "member access"),
        new("property.user.Name", "member access"),
        new("property.cfg.enabled", "member access"),
        new("property.cfg.limit", "member access"),
        new("property.items.Count", "member access"),
        new("body.Name", "member access"),
        new("body.Age", "member access"),
        new("body.Manager.Name", "member access"),
        // a public field, read through every root the same way as a property
        new("header.user.Level", "member access"),
        new("property.user.Level", "member access"),
        new("body.Level", "member access"),
        new("body.Manager.Level", "member access"),

        // ── comparison, spaced ────────────────────────────────────────────────
        new("header.a > 10", "comparison spaced"),
        new("header.a < 10", "comparison spaced"),
        new("header.a >= 42", "comparison spaced"),
        new("header.a <= 42", "comparison spaced"),
        new("header.a == 42", "comparison spaced"),
        new("header.a != 42", "comparison spaced"),
        new("header.s == 'hello'", "comparison spaced"),
        new("header.s != 'hello'", "comparison spaced"),
        new("header.missing == null", "comparison spaced"),
        new("header.a != null", "comparison spaced"),
        new("1 > 0", "comparison spaced"),

        // ── comparison, whitespace variations ─────────────────────────────────
        new("header.a>10", "comparison whitespace"),
        new("header.a>=42", "comparison whitespace"),
        new("header.a<=42", "comparison whitespace"),
        new("header.a==42", "comparison whitespace"),
        new("header.a!=42", "comparison whitespace"),
        new("header.a  >  10", "comparison whitespace"),
        new("header.a\t>\t10", "comparison whitespace"),
        new("header.a\n>\n10", "comparison whitespace"),
        new("  header.a>10  ", "comparison whitespace"),
        new("header.a >10", "comparison whitespace"),
        new("header.a> 10", "comparison whitespace"),
        new("1>0", "comparison whitespace"),

        // ── comparison against member access ──────────────────────────────────
        new("header.user.Age > 18", "comparison member"),
        new("header.user.Age>18", "comparison member"),
        new("header.list.Count > 3", "comparison member"),
        new("property.cfg.limit > 3", "comparison member"),
        new("property.cfg.limit>3", "comparison member"),
        new("property.items.Count > 2", "comparison member"),
        new("header.s.Length > 3", "comparison member"),
        new("body.Age > 18", "comparison member"),
        new("body.Age>18", "comparison member"),

        // ── operators inside quoted literals ──────────────────────────────────
        new("header.quoted == 'a > b'", "quoted operators"),
        new("header.quoted=='a > b'", "quoted operators"),
        new("header.quoted == 'a < b'", "quoted operators"),
        new("header.worded == 'x AND y'", "quoted operators"),
        new("header.worded == 'x OR y'", "quoted operators"),
        new("header.ct == 'application/json'", "quoted operators"),
        new("header.ct=='application/json'", "quoted operators"),

        // ── word logic ────────────────────────────────────────────────────────
        new("header.a > 10 AND header.b < 5", "word logic"),
        new("header.a>10 AND header.b<5", "word logic"),
        new("header.a>10  AND  header.b<5", "word logic"),
        new("header.a>10\tAND\theader.b<5", "word logic"),
        new("header.a>10 and header.b<5", "word logic"),
        new("header.a > 10 OR header.b > 100", "word logic"),
        new("header.a > 10 XOR header.b > 100", "word logic"),
        new("property.count > 5 OR property.flag", "word logic"),
        new("NOT header.flag", "word logic"),
        new("!header.flag", "word logic"),
        new("header.a > 10 AND NOT header.off", "word logic"),
        new("header.a > 10 AND !header.off", "word logic"),
        new("header.a AND header.b", "word logic"),

        // ── parentheses ───────────────────────────────────────────────────────
        new("(header.a > 10)", "parentheses"),
        new("(header.a > 10) AND (header.b < 5)", "parentheses"),
        new("(header.a>10) AND (header.b<5)", "parentheses"),
        new("(header.a > 10 AND header.b < 5)", "parentheses"),
        new("(header.a > 100) OR (header.b < 5)", "parentheses"),
        new("((header.a > 10))", "parentheses"),
        new("(header.a + header.b) * 2", "parentheses"),

        // ── arithmetic ────────────────────────────────────────────────────────
        new("header.a + header.b", "arithmetic"),
        new("header.a+header.b", "arithmetic"),
        new("header.a - 2", "arithmetic"),
        new("header.a-2", "arithmetic"),
        new("header.a * 2", "arithmetic"),
        new("header.a*2", "arithmetic"),
        new("header.a / 2", "arithmetic"),
        new("header.a/2", "arithmetic"),
        new("header.a + header.b * 2", "arithmetic"),
        new("header.dbl * 2", "arithmetic"),
        new("-header.a", "arithmetic"),
        new("header.a + header.b > 40", "arithmetic"),
        new("header.a+header.b>40", "arithmetic"),
        // Route-XML Ф1.5: modulo, in every spacing, with negatives, by zero, and as a condition.
        new("header.a % 5", "arithmetic"),
        new("header.a%5", "arithmetic"),
        new("header.neg % 3", "arithmetic"),
        new("header.dbl % 2", "arithmetic"),
        new("header.a % header.zero", "arithmetic"),
        new("header.a % 2 == 0", "arithmetic"),
        new("header.a%2==0", "arithmetic"),

        // ── functions ─────────────────────────────────────────────────────────
        new("count(property.items)", "functions"),
        new("count(property.items) > 2", "functions"),
        new("count(property.items)>2", "functions"),
        new("length(property.name)", "functions"),
        new("length(property.name) > 3", "functions"),
        new("contains(header.s, 'ell')", "functions"),
        new("upper(header.s)", "functions"),
        new("lower(header.s)", "functions"),
        new("logical(header.a > 10)", "functions"),
        new("logical(header.a>10)", "functions"),
        // The forms where the two compilers disagree. In the template position these still go
        // through the hand-written branch, which answers wrongly; the AST answers correctly, and
        // these lines are what will move when the second compiler is removed.
        new("logical(header.a > 100)", "functions"),
        new("logical((header.a > 10) AND (header.b < 5))", "functions"),
        new("logical(header.a + header.b > 40)", "functions"),
        new("logical(header.worded == 'x AND y')", "functions"),
        new("jpath($.order.id)", "functions", Fixture.Json),
        new("xpath(/order/id)", "functions", Fixture.Xml),
        // Route-XML Ф1.5: datediff over literal dates (deterministic; uuid() is impure and is
        // pinned by LanguageAdditionsF15Tests instead of the snapshot).
        new("datediff('2026-01-03', '2026-01-01', 'days')", "functions"),
        new("datediff('2026-01-01', '2026-01-03', 'hours')", "functions"),
        new("datediff('2026-01-01T06:00:00', '2026-01-01T00:00:00', 'days')", "functions"),
        new("datediff('2026-01-02', '2026-01-01', 'fortnights')", "functions"),

        // ── increments: on headers and on properties, prefix and postfix ─────
        new("header.a++", "increments"),
        new("++header.a", "increments"),
        new("header.a--", "increments"),
        new("--header.a", "increments"),
        new("property.count++", "increments"),
        new("++property.count", "increments"),

        // ── min / max: two scalars or one collection ──────────────────────────
        new("min(header.a, header.b)", "min max"),
        new("max(header.a, header.b)", "min max"),
        new("min(header.list)", "min max"),
        new("max(header.list)", "min max"),
        new("min(property.items)", "min max"),
        new("max(property.missing)", "min max"),

        // ── decimal literals inside function arguments (parsed culture-invariantly) ───
        new("max(2.5, 1)", "decimal literals"),
        new("round(2.567, 2)", "decimal literals"),
        new("abs(-2.5)", "decimal literals"),
        new("2.5 + 1", "decimal literals"),

        // ── ternary and null coalescing ───────────────────────────────────────
        new("header.a > 10 ? 'big' : 'small'", "ternary"),
        new("header.a>10 ? 'big' : 'small'", "ternary"),
        new("header.a > 100 ? 'big' : 'small'", "ternary"),
        new("length(property.name) > 3 ? true : false", "ternary"),
        new("header.missing ?? 5", "ternary"),
        new("header.a ?? 5", "ternary"),
        new("header.missing ?? 'dflt'", "ternary"),

        // ── index access ──────────────────────────────────────────────────────
        new("property.items[0]", "index access"),
        new("header.list[1]", "index access"),
        new("property.items[0] == 'p'", "index access"),

        // ── a whole-string placeholder, the historical way to write a condition ───
        // The value is rendered to text and read back, so a comparison arrives as "True"/"False"
        // and RouteTruthiness parses it. Correct, but worth pinning: before the placeholder learned
        // to evaluate comparisons every one of these was the empty string, which made
        // Filter("${header.a > 10}") always false whatever the comparison said.
        new("${header.flag}", "whole-string placeholder", Templated: true),
        new("${header.off}", "whole-string placeholder", Templated: true),
        new("${header.zero}", "whole-string placeholder", Templated: true),
        new("${header.empty}", "whole-string placeholder", Templated: true),
        new("${header.missing}", "whole-string placeholder", Templated: true),
        new("${header.a > 10}", "whole-string placeholder", Templated: true),
        new("${header.a > 100}", "whole-string placeholder", Templated: true),
        new("${header.a>100}", "whole-string placeholder", Templated: true),
        new("${header.s == 'hello'}", "whole-string placeholder", Templated: true),
        new("${header.s == 'nope'}", "whole-string placeholder", Templated: true),
        new("${header.a > 10 AND header.b < 5}", "whole-string placeholder", Templated: true),

        // ── Apache Camel idioms: the operator lives outside the placeholder ───
        new("${header.a} > 10", "camel idioms", Templated: true),
        new("${header.a} > 100", "camel idioms", Templated: true),
        new("${header.a} == 42", "camel idioms", Templated: true),
        new("${header.s} == 'hello'", "camel idioms", Templated: true),
        new("${header.s} == 'nope'", "camel idioms", Templated: true),
        new("${header.a} > 10 && ${header.b} < 5", "camel idioms", Templated: true),
        new("${header[Content-Type]}", "camel idioms", Templated: true),
        new("${headers.Content-Type}", "camel idioms", Templated: true),
        new("${headers[Content-Type]}", "camel idioms", Templated: true),
        new("${exchangeProperty.count}", "camel idioms", Templated: true),
        new("${property.count}", "camel idioms", Templated: true),
        new("${body}", "camel idioms", Fixture.Text, Templated: true),

        // ── truthiness: the one rule, §1.1 of the catalog ─────────────────────
        new("0", "truthiness"),
        new("1", "truthiness"),
        new("'0'", "truthiness"),
        new("'1'", "truthiness"),
        new("'no'", "truthiness"),
        new("'yes'", "truthiness"),
        new("'off'", "truthiness"),
        // Foreign words for yes/no are not boolean words: the set is English-only by design.
        new("'oui'", "truthiness"),
        new("'nein'", "truthiness"),
        new("'anything else'", "truthiness"),
        new("header.dbl", "truthiness"),
        new("header.zero AND header.a", "truthiness"),
        new("header.zero OR header.a", "truthiness"),
        new("logical(header.zero)", "truthiness"),
        new("logical(header.a)", "truthiness"),
        new("NOT header.zero", "truthiness"),

        // ── malformed ─────────────────────────────────────────────────────────
        new("header.a >", "malformed"),
        new("> 10", "malformed"),
        new("((((", "malformed"),
        new("1 +", "malformed"),
        new("~~~INVALID~~~", "malformed"),
        new("header.", "malformed"),
        new("count(", "malformed"),
        new("'unterminated", "malformed"),
    ];
}

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>A person in the guide's fixture exchange.</summary>
public sealed class GuidePerson
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public bool Active { get; set; }
    public GuidePerson? Manager { get; set; }
}

/// <summary>The bean the guide's bean: examples call.</summary>
public sealed class GuidePricing
{
    public double Gross(int qty, double price) => qty * price;
    public bool IsGold(string? name) => name == "Ann";
    public string Badge(string name, int points) => $"{name}:{points}";
    public void Touch(string _) { }
    public string Rate => "standard";
}

/// <summary>
/// Runs every example of <c>redb.Route/EXPRESSIONS.md</c> that the guide marks for running: each table preceded by
/// <c>&lt;!-- run: value --&gt;</c>, <c>&lt;!-- run: template --&gt;</c> or <c>&lt;!-- run: condition --&gt;</c> is
/// evaluated row by row on the exchange of the guide's section 0, and the result column must be what the engine
/// gives. The guide cannot drift from the code: a changed result fails here.
/// </summary>
public sealed class ExpressionsGuideExamplesTests : IAsyncLifetime
{
    private readonly RouteContext _context = new();

    public Task InitializeAsync()
    {
        _context.AddToRegistry("pricing", new GuidePricing());
        _context.AddToRegistry("pricing-rules", new GuidePricing());
        _context.AddToRegistry("limits.max", 100);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    private static string Guide([CallerFilePath] string file = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", "EXPRESSIONS.md"));

    /// <summary>The exchange of the guide's section 0, fresh for every row (increments change it).</summary>
    private Exchange Fixture(string? body)
    {
        var exchange = new Exchange(new Message(body switch
        {
            "json" => """{"order":{"id":7,"total":150.5,"tags":["a","b"]}}""",
            "xml" => "<order><id>7</id><total>150.5</total></order>",
            _ => "payload",
        })) { Context = _context };
        var headers = exchange.In.Headers;
        headers["a"] = 42;
        headers["b"] = 3;
        headers["neg"] = -7;
        headers["zero"] = 0;
        headers["price"] = 2.5;
        headers["s"] = "Hello World";
        headers["flag"] = true;
        headers["off"] = false;
        headers["Content-Type"] = "text/xml";
        headers["empty"] = "";
        headers["user"] = new GuidePerson { Name = "Ann", Age = 30, Active = true, Manager = new GuidePerson { Name = "Bob", Age = 51 } };
        headers["list"] = new List<int> { 5, 1, 9 };
        headers["when"] = new DateTime(2026, 8, 28, 10, 30, 0, DateTimeKind.Utc);
        headers["O'Brien"] = "obrien";
        headers["redbFile.Name"] = "orders/a.xml";
        headers["redbFile.NameOnly"] = "a";
        exchange.Properties["count"] = 7;
        exchange.Properties["items"] = new List<string> { "p", "q", "r" };
        exchange.Properties["nums"] = new List<int> { 1, 2, 3, 4 };
        exchange.Properties["cfg"] = new Dictionary<string, object?> { ["enabled"] = true, ["limit"] = 5 };
        exchange.Properties["counter"] = 10;
        exchange.Exception = new InvalidOperationException("boom");
        return exchange;
    }

    private static readonly Regex Marker = new(@"^<!--\s*run:\s*(?<mode>value|template|condition)(\s+body=(?<body>json|xml))?\s*-->$", RegexOptions.Compiled);
    private static readonly Regex Code = new(@"`(?<code>[^`]+)`", RegexOptions.Compiled);

    public sealed record Example(string Mode, string? Body, string Expression, string Expected, int Line)
    {
        public override string ToString() => $"line {Line}: {Expression}";
    }

    /// <summary>Every marked row of the guide.</summary>
    public static IEnumerable<object[]> Examples()
    {
        var lines = File.ReadAllLines(Guide());
        for (var i = 0; i < lines.Length; i++)
        {
            if (Marker.Match(lines[i].Trim()) is not { Success: true } marker)
                continue;
            var mode = marker.Groups["mode"].Value;
            var body = marker.Groups["body"].Success ? marker.Groups["body"].Value : null;
            var row = i + 1;
            while (row < lines.Length && !lines[row].TrimStart().StartsWith('|'))
                row++;
            row += 2; // header and separator
            for (; row < lines.Length && lines[row].TrimStart().StartsWith('|'); row++)
            {
                var cells = Cells(lines[row]);
                var expression = Code.Match(cells[0]).Groups["code"].Value;
                var expected = Code.Match(cells[1]) is { Success: true } m ? m.Groups["code"].Value : null;
                if (expected is null)
                    throw new InvalidOperationException($"EXPRESSIONS.md line {row + 1}: a run table row needs a `result`.");
                yield return [new Example(mode, body, expression, expected, row + 1)];
            }
        }
    }

    private static string[] Cells(string line)
    {
        var text = line.Trim().Trim('|');
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '|')
            {
                current.Append('|');
                i++;
            }
            else if (text[i] == '|')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(text[i]);
            }
        }
        cells.Add(current.ToString().Trim());
        return [.. cells];
    }

    [Fact]
    public void TheGuide_HasRunnableExamples()
    {
        Examples().Count().Should().BeGreaterThan(100);
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void EveryMarkedExample_GivesTheDocumentedResult(Example example)
    {
        string actual;
        try
        {
            var exchange = Fixture(example.Body);
            actual = example.Mode switch
            {
                "template" => ExpressionResolver.ProcessTemplate(example.Expression, exchange),
                "condition" => PredicateFactory.FromString(example.Expression).Matches(exchange) ? "True" : "False",
                _ => new StringExpression(example.Expression).Evaluate<object>(exchange) is { } value
                    ? ExpressionResolver.TemplateText(value)
                    : "null",
            };
        }
        catch (Exception ex)
        {
            actual = $"error: {ex.Message}";
        }

        actual.Should().Be(example.Expected, $"EXPRESSIONS.md line {example.Line}: {example.Expression}");
    }
}

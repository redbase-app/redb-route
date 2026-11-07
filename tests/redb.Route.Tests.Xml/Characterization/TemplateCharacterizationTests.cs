using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Predicates;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml.Characterization;

/// <summary>
/// Characterization of every place that finds <c>${...}</c> in a string, recorded before the shared template scanner
/// and the bean function change any of them (stage 2 of the bean parity work, step a). The corpus is every template
/// written in the redb.Route tree: docs, examples, demos, sources and tests. For each one the snapshot holds what the
/// engine does with it today:
/// <list type="bullet">
/// <item>the hole spans of the template compiler, of <see cref="StringExpression"/>'s template test and of
/// <see cref="EndpointOptions"/>' expression test (<see cref="HoleSpans"/>, the one reader that may change when the
/// scanner replaces the three patterns);</item>
/// <item>the single-placeholder reading, the rendered text, the typed value, the condition result, whether an
/// endpoint option binds it as dynamic, and the pack gate's comparison warning.</item>
/// </list>
/// The snapshot file must not change while the scanner and the grammar change. Values that depend on the clock, on
/// random numbers or on the environment are recorded by their kind only.
/// Re-record (only on purpose, and only with the reviewer's agreement): <c>REDB_RECORD_CHARACTERIZATION=1</c> re-records the
/// committed templates; <c>=extract</c> rebuilds the corpus from the sources.
/// </summary>
public sealed class TemplateCharacterizationTests
{
    private const string SnapshotName = "template-characterization.jsonl";

    private static string Here([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;

    /// <summary>The redb.Route tree root: two levels above the test project directory, in the monorepo and the mirror.</summary>
    private static string RouteTreeRoot() => Path.GetFullPath(Path.Combine(Here(), "..", "..", ".."));

    [Fact]
    public void EveryTemplateOfTheRouteTree_BehavesAsRecorded()
    {
        var snapshot = Path.Combine(Here(), SnapshotName);
        // "1" re-records the committed templates (the corpus stays fixed while the engine changes); "extract" builds the
        // corpus again from the Route tree, which also picks up and drops templates as the sources change.
        var record = Environment.GetEnvironmentVariable("REDB_RECORD_CHARACTERIZATION");
        if (record is "1" or "extract")
        {
            List<string> corpus = record == "extract" || !File.Exists(snapshot)
                ? ExtractCorpus(RouteTreeRoot())
                : [.. File.ReadAllLines(snapshot).Where(l => l.Length > 0)
                    .Select(l => JsonSerializer.Deserialize<Entry>(l, Json)!.Template)];
            // A one-time, reviewed addition to the corpus: one expression per line of the named file.
            if (Environment.GetEnvironmentVariable("REDB_CHARACTERIZATION_ADD") is { Length: > 0 } additions)
                corpus = [.. new SortedSet<string>(corpus.Concat(File.ReadAllLines(additions).Where(l => l.Length > 0)), StringComparer.Ordinal)];
            File.WriteAllText(snapshot, string.Join("\n", corpus.Select(t => Serialize(Characterize(t)))) + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        File.Exists(snapshot).Should().BeTrue($"the snapshot {SnapshotName} is committed beside this test");
        var recorded = File.ReadAllLines(snapshot).Where(l => l.Length > 0).ToList();
        recorded.Count.Should().BeGreaterThan(500, "the corpus is every template of the Route tree");

        var mismatches = new List<string>();
        foreach (var line in recorded)
        {
            var template = JsonSerializer.Deserialize<Entry>(line, Json)!.Template;
            var now = Serialize(Characterize(template));
            if (now != line)
                mismatches.Add($"recorded: {line}{Environment.NewLine}now:      {now}");
        }

        mismatches.Should().BeEmpty($"{mismatches.Count} of {recorded.Count} templates behave differently:{Environment.NewLine}" +
                                    string.Join(Environment.NewLine, mismatches.Take(20)));
    }

    [Fact]
    public void TheSnapshot_IsSortedAndFreeOfDuplicates()
    {
        var templates = File.ReadAllLines(Path.Combine(Here(), SnapshotName)).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<Entry>(l, Json)!.Template).ToList();

        templates.Should().OnlyHaveUniqueItems();
        templates.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    // ── one template ─────────────────────────────────────────────────────────

    /// <summary>One line of the snapshot.</summary>
    public sealed record Entry(
        string Template,
        int[][] CompilerHoles,
        int[][] StringExpressionHoles,
        int[][] EndpointOptionHoles,
        string? SinglePlaceholder,
        string Text,
        string Value,
        string Condition,
        string OptionBinding,
        bool GateWarnsComparison,
        string? ResolveExpression = null,
        string? Interpreted = null,
        string? Ast = null);

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Serialize(Entry entry) => JsonSerializer.Serialize(entry, Json);

    private static readonly Regex Varying = new(
        @"(?i)\b(now|today|date|time|timestamp|random|uuid|guid|ticks|exchangeId|messageId|stats|messagehistory|elapsed|env)\b|\{\{",
        RegexOptions.Compiled);

    private static Entry Characterize(string template)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        try
        {
            var varies = Varying.IsMatch(template);
            return new Entry(
                template,
                HoleSpans(template, HolePattern.Compiler),
                HoleSpans(template, HolePattern.StringExpression),
                HoleSpans(template, HolePattern.EndpointOption),
                ExpressionResolver.TryGetSinglePlaceholder(template, out var single) ? single : null,
                Outcome(() => ExpressionResolver.ProcessTemplate(template, Fixture()), varies),
                Outcome(() => new StringExpression(template).Evaluate<object>(Fixture()), varies),
                Outcome(() => PredicateFactory.FromString(template).Matches(Fixture()), varies),
                Outcome(() => OptionIsDynamic(template), varies),
                GateWarnsComparison(template),
                // The three positions rows 2+22 reconcile: the public entry a payload template's expr() calls, the
                // interpreted AST (where row 27 lives), and the AST compiler alone.
                Outcome(() => ExpressionResolver.ResolveExpression(template, Fixture()), varies),
                Outcome(() => new redb.Route.Expressions.Ast.Parser(new redb.Route.Expressions.Ast.Tokenizer(template).GetAllTokens())
                    .Parse().Evaluate(Fixture()), varies),
                Outcome(() => ExpressionResolver.GetCompiledValueExpressionWithAst(template)(Fixture()), varies));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    /// <summary>The same message for every evaluation: fixed headers, properties and a structured body.</summary>
    private static Exchange Fixture()
    {
        var body = new Dictionary<string, object?>
        {
            ["Name"] = "ann",
            ["Amount"] = 10,
            ["Price"] = 2.5,
            ["Items"] = new List<object?> { 1, 2, 3 },
            ["Customer"] = new Dictionary<string, object?> { ["Name"] = "bob", ["Vip"] = true },
        };
        var exchange = new Exchange(new Message(body));
        foreach (var (key, value) in new (string, object?)[]
                 {
                     ("qty", 3), ("price", 2.5), ("amount", 5000), ("kind", "order"), ("flag", true), ("name", "ann"),
                     ("orderId", "A-42"), ("orderNo", "A-42"), ("tripId", "T1"), ("count", 2), ("go", "yes"),
                     ("CamelFileName", "in.txt"), ("Content-Type", "application/json"), ("status", 200),
                 })
            exchange.In.Headers[key] = value;
        exchange.Properties["tenant"] = "acme";
        exchange.Properties["retries"] = 1;
        return exchange;
    }

    private static string Outcome(Func<object?> evaluate, bool varies)
    {
        object? value;
        try
        {
            value = evaluate();
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } e } ? e : ex;
            return varies ? $"!{inner.GetType().Name}" : $"!{inner.GetType().Name}: {Stable(inner.Message)}";
        }
        if (value is null)
            return "null";
        var type = value.GetType().Name;
        return varies ? $"{type}" : $"{type}: {Stable(Render(value))}";
    }

    private static string Render(object value) => value switch
    {
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        System.Collections.IDictionary d => "{" + string.Join(", ", d.Keys.Cast<object>().Select(k => $"{k}={Render(d[k]!)}")) + "}",
        System.Collections.IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(i => i is null ? "null" : Render(i))) + "]",
        _ => value.ToString() ?? "",
    };

    private static readonly Regex Ids = new(@"\b[0-9a-f]{32}\b|\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.Compiled);

    private static readonly Regex AssemblyVersions = new(@"Version=\d+(\.\d+){3}", RegexOptions.Compiled);

    /// <summary>Ids and the runtime's assembly versions (net8, net9 and net10 differ) are not part of the behaviour.</summary>
    private static string Stable(string text)
        => AssemblyVersions.Replace(Ids.Replace(text, "<id>"), "Version=*").ReplaceLineEndings("\n");

    private sealed class ProbeOptions : EndpointOptions
    {
        public DynamicValue<string> Value { get; set; }

        public override void Validate() { }
    }

    private static bool OptionIsDynamic(string template)
    {
        var options = new ProbeOptions();
        options.BindFromUri(new Dictionary<string, string> { ["value"] = template });
        return options.Value.IsDynamic;
    }

    private static bool GateWarnsComparison(string template)
        => (bool)typeof(RoutePackage)
            .GetMethod("LooksLikeTemplateComparison", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [template])!;

    // ── the hole reader (the one helper step b may switch to the shared scanner) ──

    private enum HolePattern { Compiler, StringExpression, EndpointOption }

    /// <summary>The <c>[start, length]</c> of every hole each place finds, read through the shared template scanner.</summary>
    private static int[][] HoleSpans(string template, HolePattern place)
    {
        // Every place reads holes through the shared scanner since step b; the place argument stays so the three
        // recorded columns keep their meaning.
        _ = place;
        return [.. TemplateScanner.Scan(template).Holes.Select(h => new[] { h.Start, h.Length })];
    }

    // ── the corpus ───────────────────────────────────────────────────────────

    private static readonly Regex Quoted = new(@"""((?:[^""\\\r\n]|\\.)*\$\{(?:[^""\\\r\n]|\\.)*)""", RegexOptions.Compiled);
    private static readonly Regex Backticked = new(@"`([^`\r\n]*\$\{[^`\r\n]*)`", RegexOptions.Compiled);
    private static readonly Regex DocCode = new(@"<c>([^<\r\n]*\$\{[^<\r\n]*)</c>", RegexOptions.Compiled);
    private static readonly Regex ElementText = new(@">([^<>\r\n]*\$\{[^<>\r\n]*)<", RegexOptions.Compiled);

    /// <summary>
    /// Every distinct template written in the Route tree: quoted strings and attribute values, inline code of the
    /// docs, <c>&lt;c&gt;</c> spans of XML docs and element text of route files, up to 300 characters.
    /// </summary>
    private static List<string> ExtractCorpus(string root)
    {
        var files = new[] { "README.md", "UPGRADING.md", "TRANSACTIONS.md", "CONCURRENCY.md" }
            .Select(f => Path.Combine(root, f)).Where(File.Exists)
            .Concat(new[] { "docs", "src", "tests", "demos", "demo", "tools" }
                .Select(d => Path.Combine(root, d)).Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories)))
            .Where(f => f.EndsWith(".md", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)
                        || f.EndsWith(".xml", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains("node_modules", StringComparison.Ordinal)
                        && !f.EndsWith(SnapshotName, StringComparison.Ordinal));

        var corpus = new SortedSet<string>(EdgeCases, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var isCs = file.EndsWith(".cs", StringComparison.Ordinal);
            foreach (var line in File.ReadLines(file))
            {
                if (!line.Contains("${", StringComparison.Ordinal))
                    continue;
                foreach (Match m in Quoted.Matches(line))
                    Add(corpus, isCs ? UnescapeCs(m.Groups[1].Value) : WebUtility.HtmlDecode(m.Groups[1].Value));
                foreach (Match m in Backticked.Matches(line))
                    Add(corpus, m.Groups[1].Value);
                foreach (Match m in DocCode.Matches(line))
                    Add(corpus, WebUtility.HtmlDecode(m.Groups[1].Value));
                if (!isCs)
                    foreach (Match m in ElementText.Matches(line))
                        Add(corpus, WebUtility.HtmlDecode(m.Groups[1].Value));
            }
        }
        return [.. corpus];
    }

    /// <summary>
    /// The shapes a balanced scanner reads differently from the first-'}' patterns, written down so their current
    /// reading is on record: braces and quotes inside a hole, unbalanced and empty holes, adjacent holes, nesting.
    /// </summary>
    private static readonly string[] EdgeCases =
    [
        "${'}'}", "${\"}\"}", "${header.a == '}'}", "${body} '}'", "${a ${b}", "${a ${b}}", "${${body}}",
        "${}", "$${body}", "${body}}", "{${body}}", "}${body}{", "${ body }", "${header.a}${header.b}",
        "text ${", "text ${body", "${body} and ${", "${header.flag ? 'yes' : 'no'}", "${header.flag?'a':'b'}",
        "${bean:svc.Check(${body})}", "${bean:svc.Check(body)}", "${concat('a', '}', 'b')}",
        "${header.list[0]}", "${body.Items[1]}", "${upper(header.name)}", "${header.qty * header.price}",
        "${header.kind} == 'order'", "${header.kind == 'order'}", "pre ${header.name} mid ${header.kind} post",
        "${header.name}\n${header.kind}", "${header['Content-Type']}", "${property.tenant}",
    ];

    private static void Add(SortedSet<string> corpus, string template)
    {
        template = template.Trim();
        if (template.Length is > 0 and <= 300 && template.Contains("${", StringComparison.Ordinal))
            corpus.Add(template);
    }

    private static string UnescapeCs(string text)
        => text.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
}

using System.Globalization;
using System.Text;
using FluentAssertions;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// The characterisation net over the expression language. Every form of
/// <see cref="ExpressionCorpus"/> is evaluated in all four positions and compared against
/// <see cref="ExpressionLanguageSnapshot.Expected"/>.
/// <para>
/// A failure here does not mean the code is wrong; it means behaviour moved. Read the reported
/// lines, decide whether the move is the intended one, and only then update the snapshot. That
/// discipline is what makes a change to the expression system safe to attempt at all: without it
/// a fix in one position silently rearranges the other three.
/// </para>
/// </summary>
[Collection("ExpressionResolver")]
public class ExpressionLanguageSnapshotTests
{
    [Fact]
    public void TheLanguageBehavesExactlyAsRecorded()
    {
        var actual = Normalise(ExpressionPositions.RenderSnapshot());
        var expected = Normalise(ExpressionLanguageSnapshot.Expected);

        if (actual == expected)
            return;

        // Written out so regenerating the snapshot is a copy, not a retyping exercise.
        var fresh = Path.Combine(Path.GetTempPath(), "expression-language.snapshot.txt");
        File.WriteAllText(fresh, actual);

        Assert.Fail($"{BuildReport(expected, actual)}{Environment.NewLine}Fresh snapshot written to {fresh}.");
    }

    /// <summary>
    /// The corpus has to keep covering every position; an entry that silently stops being
    /// evaluated would leave a hole the snapshot cannot show.
    /// </summary>
    [Fact]
    public void EveryFormIsRecordedInEveryPosition()
    {
        var lines = Normalise(ExpressionLanguageSnapshot.Expected)
            .Split('\n')
            .Where(line => line.Contains(" | value=", StringComparison.Ordinal))
            .ToList();

        lines.Should().HaveCount(ExpressionCorpus.Entries.Count);
        lines.Should().OnlyContain(line =>
            line.Contains(" | value=", StringComparison.Ordinal) &&
            line.Contains(" | condition=", StringComparison.Ordinal) &&
            line.Contains(" | template=", StringComparison.Ordinal));
    }

    /// <summary>
    /// Template interpolation formats numbers with the ambient culture, so the same route emits
    /// <c>2.5</c> or <c>2,5</c> depending on where it runs. Recorded here rather than hidden:
    /// the snapshot itself is generated under the invariant culture so it stays portable, which
    /// would otherwise make this dependency invisible.
    /// </summary>
    [Fact]
    public void TemplateNumberFormatting_IsInvariant_OnEveryMachine()
    {
        // V4 (phase 09): a ${...} template renders numbers and dates culture-invariant; before, ru-RU gave "2,5".
        var exchange = ExpressionCorpus.CreateExchange(ExpressionCorpus.Fixture.Object);
        exchange.In.Headers["price"] = 2.5;
        exchange.In.Headers["when"] = new DateTime(2026, 9, 1, 10, 30, 0, DateTimeKind.Utc);

        Render("en-US").Should().Be("2.5");
        Render("ru-RU").Should().Be("2,5".Replace(',', '.'));
        new StringExpression("${header.when}").Evaluate<string>(exchange).Should().Be("2026-09-01T10:30:00.0000000Z");

        string Render(string culture)
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            try
            {
                return new StringExpression("${header.price}").Evaluate<string>(exchange);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }

    /// <summary>
    /// Source text is culture-invariant: a decimal literal inside a function argument must parse
    /// on every machine. Until 2026-08-29 the parser used the ambient culture, so
    /// <c>max(2.5, 1)</c> compiled on en-US and threw on ru-RU.
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    [InlineData("de-DE")]
    public void DecimalLiterals_ParseUnderAnyCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var exchange = ExpressionCorpus.CreateExchange(ExpressionCorpus.Fixture.Object);
            new StringExpression("max(2.5, 1)").Evaluate<double>(exchange).Should().Be(2.5);
            new StringExpression("round(2.567, 2)").Evaluate<double>(exchange).Should().Be(2.57);
            new StringExpression("2.5 + 1").Evaluate<double>(exchange).Should().Be(3.5);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static string Normalise(string text)
        => text.Replace("\r\n", "\n").TrimEnd('\n');

    /// <summary>
    /// Compares by the form each line describes rather than by line number, so adding a form to the
    /// corpus does not cascade into every line below it and drown the real difference.
    /// </summary>
    private static string BuildReport(string expected, string actual)
    {
        var before = IndexByForm(expected);
        var after = IndexByForm(actual);

        var report = new StringBuilder();
        report.AppendLine("The expression language no longer behaves as recorded.");
        report.AppendLine("Each entry below is a form whose outcome moved. Decide whether the move is intended,");
        report.AppendLine("note it in the changelog, then regenerate ExpressionLanguageSnapshot.Expected.");
        report.AppendLine();

        var moved = 0;
        foreach (var form in before.Keys.Concat(after.Keys).Distinct())
        {
            var was = before.GetValueOrDefault(form, "<form not recorded>");
            var now = after.GetValueOrDefault(form, "<form no longer evaluated>");
            if (was == now)
                continue;

            moved++;
            if (moved > 40)
            {
                report.AppendLine("... further differences omitted.");
                break;
            }

            report.AppendLine($"  form: {form}");
            report.AppendLine($"   was: {was}");
            report.AppendLine($"   now: {now}");
            report.AppendLine();
        }

        if (moved == 0)
            report.AppendLine("No form changed; only the layout of the snapshot differs.");
        else
            report.AppendLine($"{moved} form(s) moved out of {before.Count} recorded.");

        return report.ToString();
    }

    private static Dictionary<string, string> IndexByForm(string snapshot)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in snapshot.Split('\n'))
        {
            var separator = line.IndexOf(" | value=", StringComparison.Ordinal);
            if (separator < 0)
                continue;

            index[line[..separator]] = line[(separator + 3)..];
        }

        return index;
    }
}

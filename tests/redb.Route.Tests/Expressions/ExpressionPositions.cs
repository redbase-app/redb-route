using System.Globalization;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Expressions;
using redb.Route.Predicates;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Evaluates one form of the language through every position it can appear in, and renders the
/// outcome as one deterministic line. This is the engine of the characterisation net: the corpus
/// supplies the forms, this supplies the four answers, and the snapshot pins them.
/// </summary>
internal static class ExpressionPositions
{
    /// <summary>The positions a string can occupy in the language.</summary>
    internal static readonly string[] Names = ["value", "condition", "template"];

    /// <summary>
    /// Renders the whole corpus as a text document, grouped by section, one line per form.
    /// <para>
    /// Runs under <see cref="CultureInfo.InvariantCulture"/> so the snapshot is the same on every
    /// machine. Template interpolation formats numbers with the ambient culture, which would
    /// otherwise make this file depend on the developer's regional settings; that dependency is
    /// itself pinned separately by <c>ExpressionLanguageSnapshotTests</c>.
    /// </para>
    /// </summary>
    internal static string RenderSnapshot()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return RenderSnapshotCore();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static string RenderSnapshotCore()
    {
        var text = new StringBuilder();
        text.AppendLine("# Characterisation of the redb.Route expression language.");
        text.AppendLine("#");
        text.AppendLine("# One line per form of the corpus, four columns, one per position:");
        text.AppendLine("#   value     — StringExpression(form), what SetBody / SetHeader / a connector option does");
        text.AppendLine("#   condition — PredicateFactory.FromString(form), what Filter / When / LoopWhile / Validate does");
        text.AppendLine("#   template  — StringExpression(\"${form}\"), the interpolated form");
        text.AppendLine("#");
        text.AppendLine("# This file records what the language does TODAY, right or wrong. A diff here means");
        text.AppendLine("# behaviour moved; every moved line has to be explained before the snapshot is updated.");

        var group = string.Empty;
        foreach (var entry in ExpressionCorpus.Entries)
        {
            if (entry.Group != group)
            {
                group = entry.Group;
                text.AppendLine();
                text.AppendLine($"## {group}");
            }

            text.AppendLine(RenderLine(entry));
        }

        return text.ToString();
    }

    private static string RenderLine(ExpressionCorpus.Entry entry)
    {
        var form = entry.Form;
        var fixture = entry.Fixture == ExpressionCorpus.Fixture.Object ? string.Empty : $" @{entry.Fixture}";

        return string.Join(
            " | ",
            $"{Escape(form)}{fixture}",
            $"value={Value(form, entry.Fixture)}",
            $"condition={Condition(form, entry.Fixture)}",
            $"template={Template(entry)}");
    }

    private static string Value(string form, ExpressionCorpus.Fixture fixture)
        => Capture(() => new StringExpression(form).Evaluate<object?>(ExpressionCorpus.CreateExchange(fixture)));

    private static string Condition(string form, ExpressionCorpus.Fixture fixture)
        => Capture(() => PredicateFactory.FromString(form).Matches(ExpressionCorpus.CreateExchange(fixture)));

    private static string Template(ExpressionCorpus.Entry entry)
        => entry.Templated
            ? "-"
            : Capture(() => new StringExpression("${" + entry.Form + "}")
                .Evaluate<object?>(ExpressionCorpus.CreateExchange(entry.Fixture)));

    /// <summary>
    /// Runs an evaluation and renders its outcome, a value or the type of whatever it threw.
    /// Exception messages are deliberately not recorded: they carry file paths and framework
    /// wording, and the signal here is what happened, not how it was phrased.
    /// </summary>
    private static string Capture(Func<object?> evaluate)
    {
        try
        {
            return Render(evaluate());
        }
        catch (Exception ex)
        {
            return $"!{ex.GetType().Name}";
        }
    }

    private static string Render(object? value) => value switch
    {
        null => "null",
        string s => $"\"{Escape(s)}\"",
        bool b => b ? "true" : "false",
        int or long or short or byte => $"{Convert.ToInt64(value, CultureInfo.InvariantCulture)}:{value.GetType().Name}",
        double or float or decimal => $"{Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)}:{value.GetType().Name}",
        System.Collections.ICollection collection => $"<{value.GetType().Name} count={collection.Count}>",
        _ => $"<{value.GetType().Name}>"
    };

    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
}

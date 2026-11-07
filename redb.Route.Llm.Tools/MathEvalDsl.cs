using System.Globalization;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Llm.Tools;

/// <summary>
/// DSL extension that wires an expression evaluator into a route step. Reads
/// <c>{"expression":"2 * (3 + 4)"}</c> from <c>exchange.In.Body</c>, evaluates
/// it via <see cref="ExpressionResolver.GetCompiledValueExpression"/> (same
/// compiled-lambda cache the rest of redb.Route uses for value expressions)
/// and writes the result re-serialised as JSON to <c>exchange.Out.Body</c>.
/// <para>
/// Grammar (whatever <c>ExpressionResolver</c> accepts): arithmetic
/// (<c>+ - * / %</c>), comparisons, ternary (<c>?:</c>), null-coalescing
/// (<c>??</c>), boolean operators (<c>AND/OR/NOT</c>), property / header /
/// body references and JsonPath via <c>jpath(...)</c>. Expression compilation
/// is cached, so repeated calls with the same text are effectively free.
/// </para>
/// <example>
/// <code>
/// From("direct:llm.math_eval")
///     .AsLlmTool("math_eval").Description("Evaluate an arithmetic expression.").Then()
///     .MathEval(new MathEvalOptions());
/// </code>
/// </example>
/// </summary>
public static class MathEvalDsl
{
    /// <summary>Adds an expression evaluator step. Uses default options when <paramref name="options"/> is null.</summary>
    public static IRouteDefinition MathEval(this IRouteDefinition self, MathEvalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(self);
        options ??= new MathEvalOptions();

        return self.Process((exchange, _) =>
        {
            var expression = ParseInput(exchange.In.Body);
            if (expression.Length > options.MaxExpressionChars)
                throw new ArgumentException(
                    $"Expression exceeds MaxExpressionChars ({options.MaxExpressionChars}); got {expression.Length}.");

            var compiled = ExpressionResolver.GetCompiledValueExpression(expression);
            var child = exchange.CreateChild(new Message(string.Empty));
            var result = compiled(child);

            exchange.Out ??= exchange.In.Clone();
            exchange.Out.Body = SerialiseResult(result);
            exchange.Out.Headers["llm.math_eval.finite"] = result is null
                || (result is double d && !double.IsNaN(d) && !double.IsInfinity(d))
                || result is not double;
            return Task.CompletedTask;
        });
    }

    private static string SerialiseResult(object? value) => value switch
    {
        null => "null",
        double d when double.IsNaN(d) || double.IsInfinity(d) => "null",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f when float.IsNaN(f) || float.IsInfinity(f) => "null",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        string s => JsonSerializer.Serialize(s),
        IFormattable num when num is byte or sbyte or short or ushort
                                   or int or uint or long or ulong or decimal =>
            num.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value)
    };

    private static string ParseInput(object? body)
    {
        using var doc = LlmToolJson.ParseObject(body, "MathEval");
        return LlmToolJson.RequiredString(doc.RootElement, "expression", "MathEval");
    }
}

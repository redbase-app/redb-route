using System.Globalization;
using System.Text.Json;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Llm.Abstractions.Tools;
using redb.Route.Llm.Extensions;

namespace redb.Route.Llm.Tools;

/// <summary>
/// Evaluates a value expression via the framework's
/// <see cref="ExpressionResolver"/> — same compiled-lambda cache the rest of
/// redb.Route uses. Supports arithmetic (<c>+ - * / %</c>), comparisons,
/// ternary (<c>?:</c>), null-coalescing (<c>??</c>), boolean operators
/// (<c>AND/OR/NOT</c>), property/header/body references and JSONPath via
/// <c>jpath(...)</c>.
/// <para>
/// Input: <c>{"expression":"2 * (3 + 4)"}</c>.
/// Output: result re-serialised as JSON in <c>exchange.Out.Body</c>.
/// </para>
/// <para>
/// For pure-arithmetic use the expression is evaluated against the tool's own
/// exchange (no extra context). The expression compilation is cached by
/// <see cref="ExpressionResolver"/>, so repeated calls with the same text are
/// effectively free.
/// </para>
/// </summary>
public sealed class MathEvalTool : RouteBuilder
{
    private const string InputSchema = """
        {
          "type": "object",
          "properties": {
            "expression": { "type": "string", "description": "Value expression supported by redb.Route's ExpressionResolver, e.g. '2 * (3 + 4)' or 'property.x + 1'." }
          },
          "required": ["expression"],
          "additionalProperties": false
        }
        """;

    private readonly MathEvalOptions _options;

    public MathEvalTool() : this(new MathEvalOptions()) { }

    public MathEvalTool(MathEvalOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    protected override void Configure()
    {
        var processor = new MathEvalProcessor(_options);

        From(_options.EndpointUri)
            .AsLlmTool(_options.ToolName)
                .Description("Evaluates a value expression via redb.Route's ExpressionResolver " +
                             "(arithmetic, comparisons, ternary, jpath/property/header/body refs). " +
                             "Returns the result as JSON.")
                .Input(InputSchema)
                .SideEffect(ToolSideEffect.ReadOnly)
                .Cost(ToolCostClass.Cheap)
            .Then()
            .Process(processor);
    }

    private sealed class MathEvalProcessor : IProcessor
    {
        private readonly MathEvalOptions _options;

        public MathEvalProcessor(MathEvalOptions options) => _options = options;

        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            var expression = ParseInput(exchange.In.Body);

            if (expression.Length > _options.MaxExpressionChars)
                throw new ArgumentException(
                    $"Expression exceeds MaxExpressionChars ({_options.MaxExpressionChars}); got {expression.Length}.");

            var compiled = ExpressionResolver.GetCompiledValueExpression(expression);
            var child = exchange.CreateChild(new Message(string.Empty));
            var result = compiled(child);

            exchange.Out ??= exchange.In.Clone();
            exchange.Out.Body = SerialiseResult(result);
            exchange.Out.Headers["llm.math_eval.finite"] = result is null
                || (result is double d && !double.IsNaN(d) && !double.IsInfinity(d))
                || result is not double;
            return Task.CompletedTask;
        }

        private static string SerialiseResult(object? value)
        {
            switch (value)
            {
                case null: return "null";
                case double d when double.IsNaN(d) || double.IsInfinity(d): return "null";
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case float f when float.IsNaN(f) || float.IsInfinity(f): return "null";
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case bool b: return b ? "true" : "false";
                case string s: return JsonSerializer.Serialize(s);
                case IFormattable num when num is byte or sbyte or short or ushort
                                              or int or uint or long or ulong or decimal:
                    return num.ToString(null, CultureInfo.InvariantCulture);
                default: return JsonSerializer.Serialize(value);
            }
        }

        private static string ParseInput(object? body)
        {
            if (body is null)
                throw new ArgumentException("MathEval input is empty — expected JSON {\"expression\":\"...\"}.");

            var raw = body as string ?? body.ToString() ?? string.Empty;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("MathEval input must be an object.");
                if (!doc.RootElement.TryGetProperty("expression", out var e) || e.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("MathEval input must include 'expression' as a string.");
                return e.GetString() ?? string.Empty;
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("MathEval input is not valid JSON.", ex);
            }
        }
    }
}

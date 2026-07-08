using redb.Route.Configuration;
using redb.Route.Llm.Extensions;

namespace redb.Route.Tests.Llm.TestHelpers;

/// <summary>
/// Test helper that mounts a <c>direct:</c> route with <c>.AsLlmTool(...)</c>.
/// The route's processor captures every input passed by the agent engine and
/// returns a fixed JSON reply, letting tests assert that:
/// <list type="bullet">
///   <item>The model called the tool (input list non-empty).</item>
///   <item>The model passed the right arguments (input contains expected keys).</item>
///   <item>The agent surfaced the tool result back to the model (final reply mentions the payload).</item>
/// </list>
/// <para>
/// The descriptor lands in <see cref="IToolDescriptorRegistry"/> via the DSL,
/// so the agent picks it up by name from <c>?tools=...</c> or
/// <see cref="LlmCallBuilder.UseTools"/>.
/// </para>
/// </summary>
public sealed class EchoToolRoute : RouteBuilder
{
    private readonly string _toolName;
    private readonly string _description;
    private readonly string _inputSchema;
    private readonly Func<string, string> _replyFactory;
    private readonly List<string> _capturedInputs = new();

    /// <summary>Inputs (raw JSON) the agent has passed to this tool, in call order.</summary>
    public IReadOnlyList<string> CapturedInputs => _capturedInputs;

    /// <summary>The endpoint URI the descriptor dispatches to (<c>direct:tool-{name}</c>).</summary>
    public string EndpointUri => $"direct:tool-{_toolName}";

    /// <summary>
    /// Creates a tool route that returns <paramref name="replyJson"/> for every call.
    /// </summary>
    public EchoToolRoute(string toolName, string description, string inputSchema, string replyJson)
        : this(toolName, description, inputSchema, _ => replyJson) { }

    /// <summary>
    /// Creates a tool route whose reply depends on the input JSON.
    /// </summary>
    public EchoToolRoute(string toolName, string description, string inputSchema, Func<string, string> replyFactory)
    {
        _toolName = toolName;
        _description = description;
        _inputSchema = inputSchema;
        _replyFactory = replyFactory;
    }

    /// <inheritdoc />
    protected override void Configure()
    {
        From(EndpointUri)
            .AsLlmTool(_toolName)
                .Description(_description)
                .Input(_inputSchema)
                .SideEffect(ToolSideEffect.ReadOnly)
                .Cost(ToolCostClass.Cheap)
            .Then()
            .Process(e =>
            {
                var input = e.In.Body switch
                {
                    string s => s,
                    null => "{}",
                    var x => x.ToString() ?? "{}"
                };
                lock (_capturedInputs) _capturedInputs.Add(input);

                e.Out ??= e.In.Clone();
                e.Out.Body = _replyFactory(input);
                e.Out.Headers["Content-Type"] = "application/json";
            });
    }
}

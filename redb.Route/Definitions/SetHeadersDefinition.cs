using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Sets several headers in one step (one Message History entry). A value that is an
/// <see cref="IExpression"/> is evaluated per message, a <c>Func&lt;IExchange, object?&gt;</c> is invoked,
/// anything else is a constant.
/// </summary>
public sealed class SetHeadersDefinition : ProcessorDefinition
{
    /// <summary>Creates the node.</summary>
    public SetHeadersDefinition(IReadOnlyList<(string Name, object? Value)> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (headers.Count == 0) throw new ArgumentException("At least one header is required.", nameof(headers));
        foreach (var (name, _) in headers)
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Headers = headers;
    }

    /// <summary>Header names and value sources.</summary>
    public IReadOnlyList<(string Name, object? Value)> Headers { get; }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
        => new DelegateProcessor(exchange =>
        {
            foreach (var (name, value) in Headers)
                exchange.In.Headers[name] = value switch
                {
                    IExpression expression => expression.Evaluate<object>(exchange),
                    Func<IExchange, object?> factory => factory(exchange),
                    _ => value,
                };
        });
}

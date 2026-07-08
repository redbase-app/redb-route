using redb.Route.Abstractions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Camel-style dynamic <c>toD()</c>: sends the exchange to an endpoint whose URI is
/// resolved per message. Accepts a <c>${...}</c> template, an <see cref="IExpression"/>,
/// or a factory delegate.
/// <para>
/// Example: <c>.ToD("kafka://orders-${header.region}")</c>.
/// </para>
/// </summary>
public sealed class ToDynamicDefinition : ProcessorDefinition
{
    private readonly string? _template;
    private readonly IExpression? _expression;
    private readonly Func<IExchange, string>? _uriFactory;

    /// <summary>Creates a dynamic-to definition from a <c>${...}</c> URI template.</summary>
    public ToDynamicDefinition(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        _template = template;
    }

    /// <summary>Creates a dynamic-to definition from an <see cref="IExpression"/> producing the URI.</summary>
    public ToDynamicDefinition(IExpression expression)
    {
        _expression = expression ?? throw new ArgumentNullException(nameof(expression));
    }

    /// <summary>Creates a dynamic-to definition from a factory delegate producing the URI.</summary>
    public ToDynamicDefinition(Func<IExchange, string> uriFactory)
    {
        _uriFactory = uriFactory ?? throw new ArgumentNullException(nameof(uriFactory));
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var resolver =
            _template is not null ? DynamicEndpointResolver.FromTemplate(context, _template) :
            _expression is not null ? DynamicEndpointResolver.FromExpression(context, _expression) :
            new DynamicEndpointResolver(context, _uriFactory!);
        return new ToDynamicProcessor(resolver);
    }
}

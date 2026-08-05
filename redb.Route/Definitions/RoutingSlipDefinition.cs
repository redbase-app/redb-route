using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Expressions;
using redb.Route.Processors;

namespace redb.Route.Definitions;

/// <summary>
/// Routing Slip EIP (Apache Camel parity): routes the exchange through a list of endpoints computed
/// <b>once</b> up front. The slip may be supplied as a factory delegate, an <see cref="IExpression"/>
/// that yields a delimited URI string, or a <c>${...}</c> template string. Leaf node — no
/// <see cref="IProcessorDefinition.Outputs"/>.
/// <para>
/// Unlike <see cref="DynamicRouterDefinition"/> (which recomputes the next hop per step) the slip is
/// evaluated a single time; unlike <see cref="RecipientListDefinition"/> (which fans out a copy to
/// each recipient) the slip pipes the <i>same</i> exchange through the endpoints in sequence.
/// </para>
/// </summary>
public sealed class RoutingSlipDefinition : ProcessorDefinition
{
    private const string DefaultDelimiter = ",";

    private readonly Func<IExchange, IEnumerable<string>> _slipFactory;
    private readonly bool _ignoreInvalidEndpoints;

    /// <summary>Creates a routing slip from a factory that returns the endpoint URIs per exchange.</summary>
    public RoutingSlipDefinition(Func<IExchange, IEnumerable<string>> slipFactory, bool ignoreInvalidEndpoints = false)
    {
        _slipFactory = slipFactory ?? throw new ArgumentNullException(nameof(slipFactory));
        _ignoreInvalidEndpoints = ignoreInvalidEndpoints;
    }

    /// <summary>
    /// Creates a routing slip from an <see cref="IExpression"/> that evaluates to a delimited URI
    /// string (Camel form <c>routingSlip(header("..."))</c>).
    /// </summary>
    public RoutingSlipDefinition(IExpression slip, string uriDelimiter = DefaultDelimiter, bool ignoreInvalidEndpoints = false)
    {
        ArgumentNullException.ThrowIfNull(slip);
        var delimiter = string.IsNullOrEmpty(uriDelimiter) ? DefaultDelimiter : uriDelimiter;
        _slipFactory = ex => Split(slip.Evaluate<string>(ex), delimiter);
        _ignoreInvalidEndpoints = ignoreInvalidEndpoints;
    }

    /// <summary>
    /// Creates a routing slip from a <c>${...}</c> template that resolves to a delimited URI string
    /// (a literal like <c>"direct:a,direct:b"</c> works too).
    /// </summary>
    public RoutingSlipDefinition(string slipTemplate, string uriDelimiter = DefaultDelimiter, bool ignoreInvalidEndpoints = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slipTemplate);
        var delimiter = string.IsNullOrEmpty(uriDelimiter) ? DefaultDelimiter : uriDelimiter;
        _slipFactory = ex => Split(ExpressionResolver.ProcessTemplate(slipTemplate, ex), delimiter);
        _ignoreInvalidEndpoints = ignoreInvalidEndpoints;
    }

    /// <inheritdoc />
    public override IProcessor CreateProcessor(IRouteContext context)
    {
        var logger = context.GetService<ILoggerFactory>()?.CreateLogger<RoutingSlipProcessor>();
        return new RoutingSlipProcessor(context, _slipFactory, _ignoreInvalidEndpoints, logger);
    }

    private static IEnumerable<string> Split(string? slip, string delimiter) =>
        string.IsNullOrEmpty(slip)
            ? Array.Empty<string>()
            : slip.Split(delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.ErrorHandling;

namespace redb.Route.Xml.CoreElements;

/// <summary>
/// <c>&lt;deadLetterChannel uri="direct://errors"/&gt;</c> in <c>context.xml</c>: the context's dead letter channel
/// (<see cref="DeadLetterErrorHandler"/>), Apache Camel's <c>errorHandlerRef</c> on <c>&lt;camelContext&gt;</c>. Every route
/// of the context sends to it a failure no <c>OnException</c> took.
/// </summary>
internal sealed class DeadLetterChannelContextContribution : IXmlContextContribution
{
    /// <summary>The one instance, in the core registry.</summary>
    internal static DeadLetterChannelContextContribution Instance { get; } = new();

    // Not constructible from outside: a host that discovers package contributions by scanning assemblies for
    // IXmlElementContribution types with a parameterless constructor (redb.Tsak) must not register a core element twice.
    private DeadLetterChannelContextContribution() { }

    /// <inheritdoc />
    public string Name => "deadLetterChannel";

    /// <inheritdoc />
    public XmlElementKind Kind => XmlElementKind.ContextLevel;

    /// <inheritdoc />
    public ElementSpec Spec => CoreContributions.Specs[Name];

    /// <inheritdoc />
    public IRouteDefinition Apply(XElement element, IRouteDefinition current, XmlParseContext context)
    {
        context.AddError(element, "<deadLetterChannel> is the context's error handler — put it inside <context>, not in a route.");
        return current;
    }

    /// <inheritdoc />
    public void ApplyContext(XElement element, Core.RouteContext context, XmlParseContext parseContext)
    {
        if (parseContext.RequiredAttr(element, "uri") is not { } uri)
            return;
        if (context.ErrorHandler is not null)
        {
            parseContext.AddError(element,
                "the context already has an error handler; <deadLetterChannel> would replace it — declare one of them.");
            return;
        }
        context.ErrorHandler = new DeadLetterErrorHandler(context, uri,
            context.GetService<ILoggerFactory>()?.CreateLogger<DeadLetterErrorHandler>());
    }
}

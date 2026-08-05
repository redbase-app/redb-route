#pragma warning disable CS0619
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Transactions;
using redb.Route.Validation;
using redb.Route.Xslt;

namespace redb.Route.Definitions;

/// <summary>
/// Explicit <see cref="IRouteDefinition"/> implementation for <see cref="RouteDefinitionBase{TSelf}"/>.
/// Each method delegates to the typed <c>TSelf</c> overload defined in the main partial.
/// <para>
/// This indirection is required because C# does not accept a generic type parameter
/// (constrained to be a subtype of the interface) as a covariant return type for an
/// interface method (CS0738). Explicit-impl boilerplate is therefore localised here so
/// concrete scope classes never need to repeat it.
/// </para>
/// </summary>
public abstract partial class RouteDefinitionBase<TSelf>
{
    // ── Identity ──
    IRouteDefinition IRouteDefinition.RouteId(string routeId) => RouteId(routeId);
    IRouteDefinition IRouteDefinition.AutoStart(bool value) => AutoStart(value);
    IRouteDefinition IRouteDefinition.ProcessingTimeout(TimeSpan timeout) => ProcessingTimeout(timeout);

    // ── Source ──
    IRouteDefinition IRouteDefinition.From(string uri) => From(uri);

    // ── Destination ──
    IRouteDefinition IRouteDefinition.To(string uri) => To(uri);
    IRouteDefinition IRouteDefinition.ToD(string uriTemplate) => ToD(uriTemplate);
    IRouteDefinition IRouteDefinition.ToD(IExpression uriExpression) => ToD(uriExpression);
    IRouteDefinition IRouteDefinition.ToD(Func<IExchange, string> uriFactory) => ToD(uriFactory);

    // ── Processing delegates ──
    IRouteDefinition IRouteDefinition.Process(Action<IExchange> action) => Process(action);
    IRouteDefinition IRouteDefinition.Process(Func<IExchange, CancellationToken, Task> action) => Process(action);
    IRouteDefinition IRouteDefinition.Process(IProcessor processor) => Process(processor);

    // ── Body ──
    IRouteDefinition IRouteDefinition.SetBody(object? value) => SetBody(value);
    IRouteDefinition IRouteDefinition.SetBody(Func<IExchange, object?> factory) => SetBody(factory);
    IRouteDefinition IRouteDefinition.SetBody(IExpression expression) => SetBody(expression);
    IRouteDefinition IRouteDefinition.SetBodyExpression(string template) => SetBodyExpression(template);
    IRouteDefinition IRouteDefinition.Transform(Func<IExchange, object?> transform) => Transform(transform);
    IRouteDefinition IRouteDefinition.Transform(IExpression expression) => Transform(expression);
    IRouteDefinition IRouteDefinition.RemoveBody() => RemoveBody();

    // ── Headers ──
    IRouteDefinition IRouteDefinition.SetHeader(string key, object? value) => SetHeader(key, value);
    IRouteDefinition IRouteDefinition.SetHeader(string key, Func<IExchange, object?> factory) => SetHeader(key, factory);
    IRouteDefinition IRouteDefinition.SetHeader(string name, IExpression expression) => SetHeader(name, expression);
    IRouteDefinition IRouteDefinition.SetHeaderExpression(string name, string template) => SetHeaderExpression(name, template);
    IRouteDefinition IRouteDefinition.RemoveHeader(string key) => RemoveHeader(key);

    // ── Properties ──
    IRouteDefinition IRouteDefinition.SetProperty(string key, object? value) => SetProperty(key, value);
    IRouteDefinition IRouteDefinition.SetProperty(string key, Func<IExchange, object?> factory) => SetProperty(key, factory);
    IRouteDefinition IRouteDefinition.SetProperty(string key, IExpression expression) => SetProperty(key, expression);
    IRouteDefinition IRouteDefinition.SetPropertyExpression(string key, string template) => SetPropertyExpression(key, template);
    IRouteDefinition IRouteDefinition.RemoveProperty(string key) => RemoveProperty(key);

    // ── Logging ──
    IRouteDefinition IRouteDefinition.Log(string message, LogLevel level) => Log(message, level);
    IRouteDefinition IRouteDefinition.Log(Func<IExchange, string> messageFactory, LogLevel level) => Log(messageFactory, level);

    // ── Stop / Throw ──
    IRouteDefinition IRouteDefinition.Stop() => Stop();
    IRouteDefinition IRouteDefinition.ThrowException() => ThrowException();
    IRouteDefinition IRouteDefinition.ThrowException(string message) => ThrowException(message);
    IRouteDefinition IRouteDefinition.ThrowException(Exception exception) => ThrowException(exception);
    IRouteDefinition IRouteDefinition.ThrowException(Type exceptionType, string message) => ThrowException(exceptionType, message);
    IRouteDefinition IRouteDefinition.ThrowException<TException>(string? message) => ThrowException<TException>(message);

    // ── Delay / Sampling ──
    IRouteDefinition IRouteDefinition.Delay(TimeSpan duration) => Delay(duration);
    IRouteDefinition IRouteDefinition.Delay(Func<IExchange, TimeSpan> factory) => Delay(factory);
    IRouteDefinition IRouteDefinition.Sample(long messageFrequency) => Sample(messageFrequency);
    IRouteDefinition IRouteDefinition.Sample(TimeSpan period) => Sample(period);

    // ── Stream caching ──
    IRouteDefinition IRouteDefinition.StreamCaching(long? spoolThreshold) => StreamCaching(spoolThreshold);

    // ── Validation ──
    IRouteDefinition IRouteDefinition.Validate(IMessageValidator validator, bool throwOnFailure) => Validate(validator, throwOnFailure);
    IRouteDefinition IRouteDefinition.Validate(Func<IExchange, bool> predicate, string errorMessage, bool throwOnFailure) => Validate(predicate, errorMessage, throwOnFailure);
    IRouteDefinition IRouteDefinition.ValidateJsonSchema(string schemaJson, bool throwOnFailure) => ValidateJsonSchema(schemaJson, throwOnFailure);
    IRouteDefinition IRouteDefinition.ValidateJsonSchema(Json.Schema.JsonSchema schema, bool throwOnFailure) => ValidateJsonSchema(schema, throwOnFailure);
    IRouteDefinition IRouteDefinition.ValidateXsd(string xsdContent, bool throwOnFailure) => ValidateXsd(xsdContent, throwOnFailure);
    IRouteDefinition IRouteDefinition.ValidateXsd(string? targetNamespace, string xsdContent, bool throwOnFailure) => ValidateXsd(targetNamespace, xsdContent, throwOnFailure);
    IRouteDefinition IRouteDefinition.ValidateXsd(System.Xml.Schema.XmlSchemaSet schemaSet, bool throwOnFailure) => ValidateXsd(schemaSet, throwOnFailure);
    IRouteDefinition IRouteDefinition.Xslt(string stylesheetPath, XsltOutput output, bool failOnNullBody, bool allowTemplateFromHeader) => Xslt(stylesheetPath, output, failOnNullBody, allowTemplateFromHeader);
    IRouteDefinition IRouteDefinition.XsltContent(string stylesheetXml, XsltOutput output, bool failOnNullBody, bool allowTemplateFromHeader) => XsltContent(stylesheetXml, output, failOnNullBody, allowTemplateFromHeader);

    // ── Serialization ──
    IRouteDefinition IRouteDefinition.Marshal(Type serializerType) => Marshal(serializerType);
    IRouteDefinition IRouteDefinition.Marshal<TSerializer>() => Marshal<TSerializer>();
    IRouteDefinition IRouteDefinition.Unmarshal(Type serializerType, Type targetType) => Unmarshal(serializerType, targetType);
    IRouteDefinition IRouteDefinition.Unmarshal<TSerializer, TTarget>() => Unmarshal<TSerializer, TTarget>();
    IRouteDefinition IRouteDefinition.Unmarshal<T>() => Unmarshal<T>();
    IRouteDefinition IRouteDefinition.ConvertBody<T>() => ConvertBody<T>();
    IRouteDefinition IRouteDefinition.ConvertBody(Type targetType) => ConvertBody(targetType);

    // ── Transactions ──
    IRouteDefinition IRouteDefinition.BeginTransaction() => BeginTransaction();
    IRouteDefinition IRouteDefinition.BeginTransaction(TransactionPolicy policy) => BeginTransaction(policy);
    IRouteDefinition IRouteDefinition.CommitTransaction() => CommitTransaction();
    IRouteDefinition IRouteDefinition.RollbackTransaction() => RollbackTransaction();

    // ── Replay checkpoints ──
    // The non-lambda Replayable returns ReplayableDefinition (matches the interface exactly, no
    // explicit impl needed); the lambda overload returns TSelf and needs this bridge.
    IRouteDefinition IRouteDefinition.Replayable(string name, Action<ReplayableDefinition> body, bool exposed)
        => Replayable(name, body, exposed);

    // ── Telemetry (inline overloads) ──
    IRouteDefinition IRouteDefinition.Traced(string operationName, Action<IExchange> action) => Traced(operationName, action);
    IRouteDefinition IRouteDefinition.Traced(string operationName, Func<IExchange, CancellationToken, Task> action) => Traced(operationName, action);
    IRouteDefinition IRouteDefinition.Traced(string operationName, IProcessor processor) => Traced(operationName, processor);
    IRouteDefinition IRouteDefinition.Metered(string stepName, Action<IExchange> action) => Metered(stepName, action);
    IRouteDefinition IRouteDefinition.Metered(string stepName, Func<IExchange, CancellationToken, Task> action) => Metered(stepName, action);
    IRouteDefinition IRouteDefinition.Metered(string stepName, IProcessor processor) => Metered(stepName, processor);

    // ── Enrichment ──
    IRouteDefinition IRouteDefinition.WireTap(string uri) => WireTap(uri);
    IRouteDefinition IRouteDefinition.WireTap(string uri, Action<IExchange> onPrepare) => WireTap(uri, onPrepare);
    IRouteDefinition IRouteDefinition.WireTap(string uri, Func<IExchange, object?> newBodyFactory) => WireTap(uri, newBodyFactory);
    IRouteDefinition IRouteDefinition.WireTap(string uri, Action<IExchange> onPrepare, Func<IExchange, object?> newBodyFactory) => WireTap(uri, onPrepare, newBodyFactory);
    IRouteDefinition IRouteDefinition.WireTap(Func<IExchange, string> uriFactory) => WireTap(uriFactory);
    IRouteDefinition IRouteDefinition.WireTap(Func<IExchange, string> uriFactory, Action<IExchange> onPrepare) => WireTap(uriFactory, onPrepare);
    IRouteDefinition IRouteDefinition.WireTap(Func<IExchange, string> uriFactory, Func<IExchange, object?> newBodyFactory) => WireTap(uriFactory, newBodyFactory);
    IRouteDefinition IRouteDefinition.WireTap(Func<IExchange, string> uriFactory, Action<IExchange> onPrepare, Func<IExchange, object?> newBodyFactory) => WireTap(uriFactory, onPrepare, newBodyFactory);
    IRouteDefinition IRouteDefinition.Enrich(string resourceUri, Func<IExchange, IExchange, IExchange> mergeStrategy) => Enrich(resourceUri, mergeStrategy);
    IRouteDefinition IRouteDefinition.Enrich(Func<IExchange, string> uriFactory, Func<IExchange, IExchange, IExchange> mergeStrategy) => Enrich(uriFactory, mergeStrategy);
    IRouteDefinition IRouteDefinition.PollEnrich(string resourceUri, Func<IExchange, IExchange?, IExchange> mergeStrategy, TimeSpan? timeout) => PollEnrich(resourceUri, mergeStrategy, timeout);
    IRouteDefinition IRouteDefinition.PollEnrich(Func<IExchange, string> uriFactory, Func<IExchange, IExchange?, IExchange> mergeStrategy, TimeSpan? timeout) => PollEnrich(uriFactory, mergeStrategy, timeout);
    IRouteDefinition IRouteDefinition.RecipientList(Func<IExchange, IEnumerable<string>> recipientListFactory, bool parallelProcessing, bool stopOnException, Func<IExchange, IExchange, IExchange>? aggregationStrategy) => RecipientList(recipientListFactory, parallelProcessing, stopOnException, aggregationStrategy);
    IRouteDefinition IRouteDefinition.DynamicRouter(Func<IExchange, string?> routingFunction) => DynamicRouter(routingFunction);
    IRouteDefinition IRouteDefinition.RoutingSlip(Func<IExchange, IEnumerable<string>> slipFactory, bool ignoreInvalidEndpoints) => RoutingSlip(slipFactory, ignoreInvalidEndpoints);
    IRouteDefinition IRouteDefinition.RoutingSlip(IExpression slip, string uriDelimiter, bool ignoreInvalidEndpoints) => RoutingSlip(slip, uriDelimiter, ignoreInvalidEndpoints);
    IRouteDefinition IRouteDefinition.RoutingSlip(string slipTemplate, string uriDelimiter, bool ignoreInvalidEndpoints) => RoutingSlip(slipTemplate, uriDelimiter, ignoreInvalidEndpoints);

    // ── Bean / Service Activator ──
    IRouteDefinition IRouteDefinition.Bean<TService>(Func<TService, IExchange, CancellationToken, Task> method) => Bean(method);
    IRouteDefinition IRouteDefinition.Bean<TService>(Func<TService, IExchange, Task> method) => Bean(method);
    IRouteDefinition IRouteDefinition.Bean<TService>(Action<TService, IExchange> method) => Bean(method);

    // ── Scatter-Gather ──
    IRouteDefinition IRouteDefinition.ScatterGather(Func<IExchange, IExchange, IExchange> aggregationStrategy, params string[] recipients) => ScatterGather(aggregationStrategy, recipients);
    IRouteDefinition IRouteDefinition.ScatterGather(Action<IScatterGatherDefinition> configure) => ScatterGather(configure);

    // ── Saga ──
    IRouteDefinition IRouteDefinition.Saga(Action<ISagaDefinition> configure) => Saga(configure);

    // ── LoadBalance ──
    IRouteDefinition IRouteDefinition.LoadBalance(ILoadBalancerStrategy strategy, params string[] uris) => LoadBalance(strategy, uris);
    IRouteDefinition IRouteDefinition.LoadBalance(Action<ILoadBalancerDefinition> configure) => LoadBalance(configure);

    // ── Normalizer ──
    IRouteDefinition IRouteDefinition.Normalize(Action<INormalizerDefinition> configure) => Normalize(configure);

    // ── Exception handling ──
    IRouteDefinition IRouteDefinition.ExceptionHandled() => ExceptionHandled();
    IRouteDefinition IRouteDefinition.RollbackAll() => RollbackAll();

    // ── Route-level policy ──
    IRouteDefinition IRouteDefinition.Cluster(bool value) => Cluster(value);
    IRouteDefinition IRouteDefinition.MessageHistory(bool value) => MessageHistory(value);
    IRouteDefinition IRouteDefinition.RoutePolicy(IRoutePolicy policy) => RoutePolicy(policy);
}

using System.Globalization;
using System.Xml.Linq;
using redb.Route.Abstractions;
using redb.Route.Aggregation;
using redb.Route.ControlBus;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Expressions;
using redb.Route.Extensions;
using redb.Route.Serialization;
using redb.Route.Transactions;
using redb.Route.Xslt;

namespace redb.Route.Xml.CoreElements;

internal static partial class CoreContributions
{
    /// <summary>The rest of the Ф0 §4 catalog — same registry, same facade-over-one-verb rule.</summary>
    private static IXmlElementContribution[] Catalog() =>
    [
        // ── leaves ──────────────────────────────────────────────────────────
        Step("sort", (e, cur, ctx) =>
        {
            var expr = ctx.RequiredAttr(e, "expr");
            if (expr is null) return cur;
            return cur.Sort(expr, ctx.Attr(e, "by"), ctx.Convert<bool>(e, "descending") ?? false);
        }),
        Step("sample", (e, cur, ctx) =>
        {
            var pick = ctx.ExactlyOneOf(e, "messageFrequency", "period");
            if (pick is null) return cur;
            if (pick.Value.Name == "messageFrequency")
            {
                if (long.TryParse(pick.Value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frequency))
                    return cur.Sample(frequency);
                ctx.AddError(e, $"'{pick.Value.Value}' is not a message frequency (a whole number).");
                return cur;
            }
            if (Duration(pick.Value.Value) is { } period)
                return cur.Sample(period);
            ctx.AddError(e, $"'{pick.Value.Value}' is not a TimeSpan (use hh:mm:ss or hh:mm:ss.fff).");
            return cur;
        }),
        Step("streamCaching", (e, cur, ctx) => cur.StreamCaching(ctx.Convert<long>(e, "spoolThreshold"))),
        Step("messageHistory", (e, cur, ctx) => cur.MessageHistory(ctx.Convert<bool>(e, "value") ?? true)),
        Step("validateJsonSchema", (e, cur, ctx) =>
        {
            var schema = FileOrContent(e, ctx, "JSON schema");
            if (schema is null) return cur;
            return cur.ValidateJsonSchema(schema, ctx.Convert<bool>(e, "throwOnFailure") ?? true);
        }),
        Step("validateXsd", (e, cur, ctx) =>
        {
            var xsd = FileOrContent(e, ctx, "XSD schema");
            if (xsd is null) return cur;
            return cur.ValidateXsd(ctx.Attr(e, "targetNamespace"), xsd, ctx.Convert<bool>(e, "throwOnFailure") ?? true);
        }),
        Step("xslt", (e, cur, ctx) =>
        {
            // The stylesheet path resolves at runtime through the route resource resolver,
            // so the file form passes the reference through untouched (Ф1.4).
            var file = ctx.Attr(e, "file");
            var content = ElementText(e);
            if ((file is null) == (content is null))
            {
                ctx.AddError(e, "<xslt> takes either the file attribute or inline stylesheet content, not both.");
                return cur;
            }
            var output = XsltOutput.String;
            if (ctx.Attr(e, "output") is { } outputName &&
                !Enum.TryParse(outputName, ignoreCase: true, out output))
            {
                ctx.AddError(e, $"'{outputName}' is not an XSLT output kind ({string.Join(", ", Enum.GetNames<XsltOutput>())}).");
                return cur;
            }
            var failOnNullBody = ctx.Convert<bool>(e, "failOnNullBody") ?? true;
            var fromHeader = ctx.Convert<bool>(e, "allowTemplateFromHeader") ?? false;
            return file is not null
                ? cur.Xslt(file, output, failOnNullBody, fromHeader)
                : cur.XsltContent(content!, output, failOnNullBody, fromHeader);
        }),
        Step("marshal", (e, cur, ctx) =>
        {
            var serializer = SerializerFromElement(e, ctx, requireTarget: false, out _);
            return serializer switch
            {
                string contentType => cur.Marshal(contentType),
                IMessageSerializer instance => cur.Marshal(instance),
                _ => cur,
            };
        }),
        Step("unmarshal", (e, cur, ctx) =>
        {
            var serializer = SerializerFromElement(e, ctx, requireTarget: true, out var target);
            if (target is null) return cur;
            return serializer switch
            {
                string contentType => cur.Unmarshal(contentType, target),
                IMessageSerializer instance => cur.Unmarshal(instance, target),
                // No format and no type: dispatch by the message ContentType — the generic
                // Unmarshal<T>() closed over the declared target (cold path, Ф2 §3.5).
                ContentTypeDispatch => (IRouteDefinition)typeof(IRouteDefinition)
                    .GetMethods()
                    .First(m => m.Name == nameof(IRouteDefinition.Unmarshal)
                                && m.IsGenericMethodDefinition
                                && m.GetGenericArguments().Length == 1
                                && m.GetParameters().Length == 0)
                    .MakeGenericMethod(target)
                    .Invoke(cur, null)!,
                _ => cur,
            };
        }),
        Step("controlBus", (e, cur, ctx) =>
        {
            var actionName = ctx.RequiredAttr(e, "action");
            var routeId = ctx.RequiredAttr(e, "routeId");
            if (actionName is null || routeId is null) return cur;
            if (!Enum.TryParse<ControlBusAction>(actionName, ignoreCase: true, out var action))
            {
                ctx.AddError(e, $"'{actionName}' is not a control-bus action ({string.Join(", ", Enum.GetNames<ControlBusAction>())}).");
                return cur;
            }
            return cur.ControlBus(action, routeId, ctx.Convert<bool>(e, "async") ?? false);
        }),
        Step("enrich", (e, cur, ctx) =>
        {
            var uri = StructuredEndpoint.Resolve(e, ctx, out var position);
            if (uri is null) return cur;
            ctx.NoteEndpointUri(position, uri);
            var strategy = MergeStrategy(e, ctx) ?? AggregationStrategies.UseLatest();
            return cur.Enrich(uri, strategy);
        }),
        Step("pollEnrich", (e, cur, ctx) =>
        {
            var uri = StructuredEndpoint.Resolve(e, ctx, out var position);
            if (uri is null) return cur;
            ctx.NoteEndpointUri(position, uri);
            var timeout = ctx.Convert<TimeSpan>(e, "timeout");
            var strategy = MergeStrategy(e, ctx);
            if (strategy is null)
                return cur.PollEnrich(uri, timeout);
            // The poll may return nothing; a named strategy only merges what actually arrived.
            return cur.PollEnrich(uri, (original, polled) => polled is null ? original : strategy(original, polled), timeout);
        }),
        Step("recipientList", (e, cur, ctx) =>
        {
            var expr = ctx.RequiredAttr(e, "expr");
            if (expr is null) return cur;
            return cur.RecipientList(expr,
                ctx.Attr(e, "delimiter") ?? ",",
                ctx.Convert<bool>(e, "parallel") ?? false,
                ctx.Convert<bool>(e, "stopOnException") ?? false,
                MergeStrategy(e, ctx));
        }),
        Step("dynamicRouter", (e, cur, ctx) =>
        {
            var expr = ctx.RequiredAttr(e, "expr");
            return expr is null ? cur : cur.DynamicRouter(expr);
        }),
        Step("routingSlip", (e, cur, ctx) =>
        {
            var expr = ctx.RequiredAttr(e, "expr");
            if (expr is null) return cur;
            return cur.RoutingSlip(expr,
                ctx.Attr(e, "delimiter") ?? ",",
                ctx.Convert<bool>(e, "ignoreInvalidEndpoints") ?? false);
        }),
        Step("claimCheck", (e, cur, ctx) =>
        {
            var operationName = ctx.RequiredAttr(e, "operation");
            if (operationName is null) return cur;
            if (!Enum.TryParse<ClaimCheckOperation>(operationName, ignoreCase: true, out var operation))
            {
                ctx.AddError(e, $"'{operationName}' is not a claim-check operation ({string.Join(", ", Enum.GetNames<ClaimCheckOperation>())}).");
                return cur;
            }
            return cur.ClaimCheck(operation,
                ctx.Attr(e, "key"),
                ctx.Convert<TimeSpan>(e, "ttl"),
                RegistryName(ctx.Attr(e, "repository")));
        }),
        Step("beginTransaction", (e, cur, ctx) =>
            Policy(e, ctx) is { } policy ? cur.BeginTransaction(policy) : cur.BeginTransaction()),
        Step("commitTransaction", (e, cur, ctx) => cur.CommitTransaction()),
        Step("rollbackTransaction", (e, cur, ctx) => cur.RollbackTransaction()),
        Step("rollbackAll", (e, cur, ctx) => cur.RollbackAll()),
        Step("exceptionHandled", (e, cur, ctx) => cur.ExceptionHandled()),
        Step("routePolicy", (e, cur, ctx) =>
        {
            var reference = ctx.RequiredAttr(e, "ref");
            return reference is null ? cur : cur.RoutePolicy(RegistryName(reference)!);
        }),
        Step("setHeaders", (e, cur, ctx) =>
        {
            var headers = new List<(string Name, object? Value)>();
            foreach (var child in e.Elements())
            {
                if (child.Name.LocalName != "header")
                {
                    ctx.AddError(child, $"<setHeaders> accepts only <header> children, found <{child.Name.LocalName}>.");
                    continue;
                }
                var name = ctx.RequiredAttr(child, "name");
                var pick = ctx.ExactlyOneOf(child, "value", "expr");
                if (name is null || pick is null) continue;
                headers.Add((name, pick.Value.Name == "value"
                    ? pick.Value.Value
                    : new StringExpression(pick.Value.Value)));
            }
            if (headers.Count == 0)
            {
                ctx.AddError(e, "<setHeaders> needs at least one <header name=… value=…/> or <header name=… expr=…/>.");
                return cur;
            }
            return cur.SetHeaders([.. headers]);
        }),

        // ── scopes ──────────────────────────────────────────────────────────
        Scope("multicast", (e, cur, ctx) =>
        {
            var scope = cur.Multicast();
            if (ctx.Convert<bool>(e, "parallel") == true) scope.ParallelProcessing();
            if (ctx.Convert<int>(e, "maxParallelism") is { } dop) scope.MaxParallelism(dop);
            if (ctx.Convert<bool>(e, "stopOnException") == true) scope.StopOnException();
            ctx.ParseSteps(e, scope);
            return scope.EndMulticast();
        }),
        Scope("aggregate", (e, cur, ctx) =>
        {
            var correlation = ctx.RequiredAttr(e, "correlation");
            // Owner decision 2026-09-02: no silent default strategy — absence is a schema error.
            var strategyName = ctx.RequiredAttr(e, "strategy");
            if (correlation is null || strategyName is null) return cur;
            var strategy = NamedStrategy(strategyName, e, ctx);
            if (strategy is null) return cur;
            var scope = cur.Aggregate(correlation, strategy,
                ctx.Attr(e, "completion"),
                ctx.Convert<int>(e, "completionSize"),
                ctx.Convert<TimeSpan>(e, "completionTimeout"));
            ctx.ParseSteps(e, scope);
            return scope.EndAggregate();
        }),
        Scope("tryCatch", (e, cur, ctx) =>
        {
            var scope = cur.TryCatch();
            foreach (var child in e.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "try":
                        ctx.ParseSteps(child, scope);
                        break;
                    case "catch":
                    {
                        var list = ctx.RequiredAttr(child, "exceptions");
                        if (list is null) continue;
                        foreach (var typeName in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var type = ResolveType(typeName, child, ctx);
                            if (type is null) continue;
                            if (!typeof(Exception).IsAssignableFrom(type))
                            {
                                ctx.AddError(child, $"'{typeName}' is not an Exception type.");
                                continue;
                            }
                            var handler = scope.Catch(type);
                            ctx.ApplyBranchIdentity(child, handler);
                            ctx.ParseSteps(child, handler);
                            handler.EndCatch();
                        }
                        break;
                    }
                    case "finally":
                    {
                        var final = scope.Finally();
                        ctx.ApplyBranchIdentity(child, final);
                        ctx.ParseSteps(child, final);
                        final.EndFinally();
                        break;
                    }
                    default:
                        ctx.AddError(child, $"<tryCatch> accepts only <try>, <catch> and <finally>, found <{child.Name.LocalName}>.");
                        break;
                }
            }
            return scope.EndTryCatch();
        }),
        Scope("loop", (e, cur, ctx) =>
        {
            var copy = ctx.Convert<bool>(e, "copy") ?? false;
            var shareScope = ctx.Convert<bool>(e, "shareScope") ?? true;
            var sources = new[] { "count", "expr", "while" }.Where(a => ctx.Attr(e, a) is not null).ToList();
            if (sources.Count != 1)
            {
                ctx.AddError(e, "<loop> takes exactly one of count, expr or while.");
                return cur;
            }
            LoopDefinition scope;
            switch (sources[0])
            {
                case "count":
                    if (ctx.Convert<int>(e, "count") is not { } count) return cur;
                    scope = cur.Loop(count, copy, shareScope);
                    break;
                case "expr":
                    scope = cur.Loop(ctx.Attr(e, "expr")!, copy, shareScope);
                    break;
                default:
                    scope = cur.LoopWhile(ctx.Attr(e, "while")!, copy, shareScope);
                    break;
            }
            ctx.ParseSteps(e, scope);
            return scope.EndLoop();
        }),
        Scope("throttle", (e, cur, ctx) =>
        {
            var max = ctx.RequiredAttr(e, "maxPerPeriod");
            if (max is null) return cur;
            var period = ctx.Convert<TimeSpan>(e, "period");
            var reject = ctx.Convert<bool>(e, "rejectOnOverflow") == true;
            var maxIsConstant = int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxConstant);
            if (ctx.Attr(e, "key") is { } keyExpr)
            {
                var key = new StringExpression(keyExpr);
                Func<IExchange, string> keyExtractor = ex => key.Evaluate<object>(ex)?.ToString() ?? string.Empty;
                KeyedThrottleDefinition keyed;
                if (maxIsConstant)
                {
                    keyed = cur.Throttle(keyExtractor, maxConstant, period);
                }
                else
                {
                    var maxExpression = new StringExpression(max);
                    keyed = cur.Throttle(keyExtractor, ex => maxExpression.Evaluate<int>(ex), period);
                }
                if (reject) keyed.RejectOnOverflow();
                ctx.ParseSteps(e, keyed);
                return keyed.EndKeyedThrottle();
            }
            var scope = maxIsConstant ? cur.Throttle(maxConstant) : cur.Throttle(max, period);
            if (maxIsConstant && period is { } p) scope.Period(p);
            if (reject) scope.RejectOnOverflow();
            ctx.ParseSteps(e, scope);
            return scope.EndThrottle();
        }),
        Scope("debounce", (e, cur, ctx) =>
        {
            var key = ctx.RequiredAttr(e, "key");
            var quiet = ctx.Convert<TimeSpan>(e, "quietPeriod");
            if (key is null || quiet is null)
            {
                if (key is not null) ctx.AddError(e, "<debounce> requires the quietPeriod attribute (a TimeSpan).");
                return cur;
            }
            var scope = cur.Debounce(key, quiet.Value);
            ctx.ParseSteps(e, scope);
            return scope.EndDebounce();
        }),
        Scope("circuitBreaker", (e, cur, ctx) =>
        {
            var scope = cur.CircuitBreaker();
            if (ctx.Convert<int>(e, "failureThreshold") is { } threshold) scope.FailureThreshold(threshold);
            if (ctx.Convert<TimeSpan>(e, "resetTimeout") is { } reset) scope.ResetTimeout(reset);
            if (ctx.Convert<int>(e, "halfOpenMaxCalls") is { } halfOpen) scope.HalfOpenMaxCalls(halfOpen);
            foreach (var child in e.Elements())
            {
                if (child.Name.LocalName == "fallback")
                {
                    var fallback = scope.OnFallback();
                    ctx.ApplyBranchIdentity(child, fallback);
                    ctx.ParseSteps(child, fallback);
                    fallback.EndFallback();
                }
                else
                {
                    ctx.ParseStep(child, scope);
                }
            }
            return scope.EndCircuitBreaker();
        }),
        Scope("idempotentConsumer", (e, cur, ctx) =>
        {
            var key = ctx.RequiredAttr(e, "key");
            var repository = ctx.RequiredAttr(e, "repository");
            if (key is null || repository is null) return cur;
            var scope = cur.IdempotentConsumer(key, RegistryName(repository)!,
                ctx.Convert<bool>(e, "skipDuplicate") ?? true);
            ctx.ParseSteps(e, scope);
            return scope.EndIdempotentConsumer();
        }),
        Scope("resequence", (e, cur, ctx) =>
        {
            var key = ctx.RequiredAttr(e, "key");
            if (key is null) return cur;
            var scope = cur.Resequence(key,
                ctx.Convert<int>(e, "batchSize") ?? 100,
                ctx.Convert<TimeSpan>(e, "timeout"));
            ctx.ParseSteps(e, scope);
            return scope.EndResequence();
        }),
        Scope("transaction", (e, cur, ctx) =>
        {
            var scope = cur.Transaction(Policy(e, ctx));
            if (ctx.Attr(e, "deadLetterChannel") is { } deadLetter) scope.DeadLetterChannel(deadLetter);
            // Attempts and delay are one knob: half of it would retry after a pause nobody wrote.
            var attempts = ctx.Convert<int>(e, "retryAttempts");
            var retryDelay = ctx.Convert<TimeSpan>(e, "retryDelay");
            if (attempts is not null && retryDelay is not null)
                scope.Retry(attempts.Value, retryDelay.Value);
            else if (attempts is not null)
                ctx.AddError(e, "<transaction retryAttempts=…> needs retryDelay too — how long to wait between attempts.");
            else if (ctx.Attr(e, "retryDelay") is not null)
                ctx.AddError(e, "<transaction retryDelay=…> needs retryAttempts too — how many attempts to make.");
            ctx.ParseSteps(e, scope);
            return scope.EndTransaction();
        }),
        Scope("traced", (e, cur, ctx) =>
        {
            var name = ctx.RequiredAttr(e, "name");
            if (name is null) return cur;
            var scope = cur.Traced(name);
            ctx.ParseSteps(e, scope);
            return scope.EndTraced();
        }),
        Scope("metered", (e, cur, ctx) =>
        {
            var name = ctx.RequiredAttr(e, "name");
            if (name is null) return cur;
            var scope = cur.Metered(name);
            // <tag> splits the metric by a header or by an expression; the other children are steps.
            foreach (var child in e.Elements())
            {
                if (child.Name.LocalName != "tag")
                {
                    ctx.ParseStep(child, scope);
                    continue;
                }
                var tagName = ctx.RequiredAttr(child, "name");
                var source = ctx.ExactlyOneOf(child, "fromHeader", "expr");
                if (tagName is null || source is null) continue;
                if (source.Value.Name == "fromHeader")
                {
                    scope.TagFromHeader(tagName, source.Value.Value);
                }
                else
                {
                    var expression = new StringExpression(source.Value.Value);
                    scope.Tag(tagName, exchange => expression.Evaluate<object>(exchange));
                }
            }
            return scope.EndMetered();
        }),
        Scope("replayable", (e, cur, ctx) =>
        {
            var name = ctx.RequiredAttr(e, "name");
            if (name is null) return cur;
            var scope = cur.Replayable(name, ctx.Convert<bool>(e, "exposed") ?? false);
            ctx.ParseSteps(e, scope);
            return CloseScope(scope, e, ctx);
        }),
        Scope("threads", (e, cur, ctx) =>
        {
            var poolSize = ctx.Convert<int>(e, "poolSize");
            if (poolSize is null)
            {
                ctx.AddError(e, "<threads> requires the poolSize attribute (a whole number).");
                return cur;
            }
            var scope = cur.Threads(poolSize.Value);
            if (ctx.Convert<int>(e, "maxQueueSize") is { } maxQueueSize) scope.MaxQueueSize(maxQueueSize);
            if (ctx.Convert<TimeSpan>(e, "enqueueTimeout") is { } enqueueTimeout) scope.EnqueueTimeout(enqueueTimeout);
            ctx.ParseSteps(e, scope);
            return CloseScope(scope, e, ctx);
        }),
        Scope("ofType", (e, cur, ctx) =>
        {
            var typeName = ctx.RequiredAttr(e, "type");
            if (typeName is null) return cur;
            var type = ResolveType(typeName, e, ctx);
            if (type is null) return cur;
            // A typed section, not a closable scope (Ф1.3): the children belong to the section,
            // the siblings continue on the parent — which is exactly XML nesting (Р22).
            var section = cur.OfType(type);
            ctx.ParseSteps(e, section);
            return cur;
        }),
        Step("saga", (e, cur, ctx) =>
        {
            // The saga DSL is lambda-shaped; the declarative form references IProcessor
            // instances from the context registry — resolved at parse, like predicates.
            var steps = new List<(IProcessor Action, IProcessor? Compensate)>();
            var broken = false;
            foreach (var child in e.Elements())
            {
                if (child.Name.LocalName != "step")
                {
                    ctx.AddError(child, "<saga> accepts only <step processor=… compensate=…/> children.");
                    broken = true;
                    continue;
                }
                var actionRef = ctx.RequiredAttr(child, "processor");
                if (actionRef is null) { broken = true; continue; }
                var action = ctx.RouteContext.GetFromRegistry<IProcessor>(actionRef);
                if (action is null)
                {
                    ctx.AddError(child, $"processor '{actionRef}' is not in the context registry (expected an IProcessor).");
                    broken = true;
                    continue;
                }
                IProcessor? compensate = null;
                if (ctx.Attr(child, "compensate") is { } compensateRef)
                {
                    compensate = ctx.RouteContext.GetFromRegistry<IProcessor>(compensateRef);
                    if (compensate is null)
                    {
                        ctx.AddError(child, $"compensation '{compensateRef}' is not in the context registry (expected an IProcessor).");
                        broken = true;
                        continue;
                    }
                }
                steps.Add((action, compensate));
            }
            if (broken) return cur;
            if (steps.Count == 0)
            {
                ctx.AddError(e, "<saga> needs at least one <step processor=…/>.");
                return cur;
            }
            return cur.Saga(saga =>
            {
                foreach (var (action, compensate) in steps)
                    saga.Step(action, compensate);
            });
        }),

        // These four exist at two levels (Ф0 §4.2): on a route here, and on <routes> for every
        // route of the file — the loader reuses the same Configure* helpers, no second copy.
        Scope("onException", (e, cur, ctx) =>
        {
            var types = ExceptionTypes(e, ctx);
            if (types.Length == 0) return cur;
            var scope = cur.OnException(types);
            ConfigureOnException(scope, e, ctx);
            return scope.EndOnException();
        }),
        Scope("intercept", (e, cur, ctx) =>
        {
            var scope = cur.Intercept();
            ConfigureIntercept(scope, e, ctx);
            return scope.EndIntercept();
        }),
        Scope("interceptFrom", (e, cur, ctx) =>
        {
            var scope = cur.InterceptFrom(ctx.Attr(e, "uri"));
            ConfigureIntercept(scope, e, ctx);
            return scope.EndIntercept();
        }),
        Scope("interceptSendToEndpoint", (e, cur, ctx) =>
        {
            var uri = ctx.RequiredAttr(e, "uri");
            if (uri is null) return cur;
            var scope = cur.InterceptSendToEndpoint(uri);
            ConfigureIntercept(scope, e, ctx);
            return scope.EndIntercept();
        }),
        Scope("onCompletion", (e, cur, ctx) =>
        {
            var scope = cur.OnCompletion();
            ConfigureOnCompletion(scope, e, ctx);
            return scope.EndOnCompletion();
        }),
        Step("scatterGather", (e, cur, ctx) =>
        {
            var uris = new List<string>();
            foreach (var child in e.Elements())
            {
                if (child.Name.LocalName != "recipient")
                {
                    ctx.AddError(child, $"<scatterGather> accepts only <recipient uri=…/> children, found <{child.Name.LocalName}>.");
                    continue;
                }
                if (ctx.RequiredAttr(child, "uri") is { } uri)
                {
                    ctx.NoteEndpointUri(child, uri);
                    uris.Add(uri);
                }
            }
            if (uris.Count == 0)
            {
                ctx.AddError(e, "<scatterGather> needs at least one <recipient uri=…/>.");
                return cur;
            }
            var strategy = MergeStrategy(e, ctx);
            var timeout = ctx.Convert<TimeSpan>(e, "timeout");
            var parallel = ctx.Convert<bool>(e, "parallel");
            var maxDop = ctx.Convert<int>(e, "maxDop");
            var stopOnException = ctx.Convert<bool>(e, "stopOnException");
            return cur.ScatterGather(definition =>
            {
                definition.Recipients([.. uris]);
                if (strategy is not null) definition.AggregationStrategy(strategy);
                if (timeout is { } t) definition.Timeout(t);
                if (parallel is { } par) definition.ParallelProcessing(par);
                if (maxDop is { } dop) definition.MaxDegreeOfParallelism(dop);
                if (stopOnException is { } stop) definition.StopOnException(stop);
            });
        }),
        Step("loadBalance", (e, cur, ctx) =>
        {
            var endpoints = new List<string>();
            var weights = new Dictionary<string, int>();
            foreach (var child in e.Elements())
            {
                if (child.Name.LocalName != "endpoint")
                {
                    ctx.AddError(child, $"<loadBalance> accepts only <endpoint uri=…/> children, found <{child.Name.LocalName}>.");
                    continue;
                }
                if (ctx.RequiredAttr(child, "uri") is not { } uri) continue;
                ctx.NoteEndpointUri(child, uri);
                endpoints.Add(uri);
                if (ctx.Convert<int>(child, "weight") is { } weight) weights[uri] = weight;
            }
            var strategyName = ctx.RequiredAttr(e, "strategy");
            if (strategyName is null || endpoints.Count == 0)
            {
                if (strategyName is not null)
                    ctx.AddError(e, "<loadBalance> needs at least one <endpoint uri=…/>.");
                return cur;
            }
            var key = ctx.Attr(e, "key");
            return cur.LoadBalance(definition =>
            {
                definition.Endpoints([.. endpoints]);
                switch (strategyName)
                {
                    case "roundRobin": definition.UseRoundRobin(); break;
                    case "random": definition.UseRandom(); break;
                    case "failover": definition.UseFailover(); break;
                    case "sticky":
                        if (key is null)
                        {
                            ctx.AddError(e, "strategy=\"sticky\" requires the key attribute (an expression).");
                            break;
                        }
                        definition.UseSticky(key);
                        break;
                    case "weighted":
                        if (weights.Count != endpoints.Count)
                        {
                            ctx.AddError(e, "strategy=\"weighted\" requires a weight on every <endpoint>.");
                            break;
                        }
                        definition.UseWeighted(weights);
                        break;
                    default:
                        ctx.AddError(e, $"'{strategyName}' is not a load-balancing strategy (roundRobin, random, failover, sticky, weighted).");
                        break;
                }
            });
        }),
        Step("normalize", (e, cur, ctx) =>
        {
            var branches = e.Elements().ToList();
            if (branches.Count == 0)
            {
                ctx.AddError(e, "<normalize> needs at least one <when>, <whenContentType> or <otherwise> child.");
                return cur;
            }
            // Collected first so the configure callback never touches the parse context late.
            var whens = new List<(string Condition, Func<IExchange, object?> Transform)>();
            var byContentType = new List<(string ContentType, Func<IExchange, object?> Transform)>();
            Func<IExchange, object?>? otherwise = null;
            foreach (var child in branches)
            {
                var transform = NormalizeTransform(child, ctx);
                if (transform is null) continue;
                switch (child.Name.LocalName)
                {
                    case "when":
                        if (ctx.RequiredAttr(child, "expr") is { } condition)
                            whens.Add((condition, transform));
                        break;
                    case "whenContentType":
                        if (ctx.RequiredAttr(child, "type") is { } contentType)
                            byContentType.Add((contentType, transform));
                        break;
                    case "otherwise":
                        otherwise = transform;
                        break;
                    default:
                        ctx.AddError(child, $"<normalize> accepts <when>, <whenContentType> and <otherwise>, found <{child.Name.LocalName}>.");
                        break;
                }
            }
            return cur.Normalize(definition =>
            {
                foreach (var (condition, transform) in whens) definition.When(condition, transform);
                foreach (var (contentType, transform) in byContentType) definition.WhenContentType(contentType, transform);
                if (otherwise is not null) definition.Otherwise(otherwise);
            });
        }),
    ];

    // ── helpers ─────────────────────────────────────────────────────────────

    private static DelegateContribution Scope(
        string name, Func<XElement, IRouteDefinition, XmlParseContext, IRouteDefinition> apply)
        => new(name, XmlElementKind.Scope, apply);

    /// <summary>Closes a scope through the uniform <see cref="IRouteScope.End"/> contract.</summary>
    private static IRouteDefinition CloseScope(IRouteDefinition scope, XElement e, XmlParseContext ctx)
    {
        if (scope is IRouteScope closable)
            return closable.End();
        ctx.AddError(e, $"internal: {scope.GetType().Name} is not an IRouteScope and cannot be closed.");
        return scope;
    }

    /// <summary>The exception-type list of an onException/catch attribute; errors are positioned.</summary>
    internal static Type[] ExceptionTypes(XElement e, XmlParseContext ctx)
    {
        var list = ctx.RequiredAttr(e, "exceptions");
        if (list is null) return [];
        var types = new List<Type>();
        foreach (var typeName in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var type = ResolveType(typeName, e, ctx);
            if (type is null) continue;
            if (!typeof(Exception).IsAssignableFrom(type))
            {
                ctx.AddError(e, $"'{typeName}' is not an Exception type.");
                continue;
            }
            types.Add(type);
        }
        return [.. types];
    }

    /// <summary>Attributes and child steps of an onException — shared by the route and container levels.</summary>
    internal static void ConfigureOnException(OnExceptionDefinition scope, XElement e, XmlParseContext ctx)
    {
        if (ctx.Convert<bool>(e, "handled") is { } handled) scope.Handled(handled);
        if (ctx.Convert<bool>(e, "continued") is { } continued) scope.Continued(continued);
        if (ctx.Convert<int>(e, "maximumRedeliveries") is { } redeliveries) scope.MaximumRedeliveries(redeliveries);
        if (ctx.Convert<TimeSpan>(e, "redeliveryDelay") is { } delay) scope.RedeliveryDelay(delay);
        if (ctx.Convert<bool>(e, "exponentialBackOff") is { } backOff) scope.UseExponentialBackOff(backOff);
        if (ctx.Convert<double>(e, "backOffMultiplier") is { } multiplier) scope.BackOffMultiplier(multiplier);
        if (ctx.Convert<bool>(e, "useOriginalBody") == true) scope.UseOriginalBody();
        if (ctx.Convert<bool>(e, "logStackTrace") is { } stackTrace) scope.LogStackTrace(stackTrace);
        if (ctx.Convert<bool>(e, "logExhausted") is { } exhausted) scope.LogExhausted(exhausted);
        if (LogLevelOf(e, "retryAttemptedLogLevel", ctx) is { } attempted) scope.RetryAttemptedLogLevel(attempted);
        if (LogLevelOf(e, "retriesExhaustedLogLevel", ctx) is { } exhaustedLevel) scope.RetriesExhaustedLogLevel(exhaustedLevel);
        // The three moments a handler can step into, each a bean from the registry: on every
        // occurrence, before every retry, once before the handler takes over for good.
        if (ProcessorRef(e, "onExceptionOccurred", ctx) is { } occurred) scope.OnExceptionOccurred(occurred);
        if (ProcessorRef(e, "onRedelivery", ctx) is { } redelivery) scope.OnRedelivery(redelivery);
        if (ProcessorRef(e, "onPrepareFailure", ctx) is { } prepareFailure) scope.OnPrepareFailure(prepareFailure);
        // <when> decides whether this handler takes the failure at all, <retryWhile> whether
        // another attempt follows: conditions of the scope, like an intercept's. Everything
        // else is the handler's own route.
        foreach (var child in e.Elements())
        {
            if (IsConditionChild(child, ctx, out var condition))
            {
                if (condition is not null) scope.OnWhen(condition);
            }
            else if (child.Name.LocalName == "retryWhile")
            {
                if (child.Elements().Any())
                    ctx.AddError(child, "<retryWhile> here is the scope's retry condition and takes no child steps — put the steps next to it.");
                else if (ctx.RequiredAttr(child, "expr") is { } retryWhile)
                    scope.RetryWhile(retryWhile);
            }
            else
            {
                ctx.ParseStep(child, scope);
            }
        }
    }

    /// <summary>
    /// An optional reference to an <see cref="IProcessor"/> bean; a name the registry does not hold is a
    /// positioned error, so a misspelt reference is named at load instead of going silently unused.
    /// </summary>
    private static IProcessor? ProcessorRef(XElement e, string attribute, XmlParseContext ctx)
    {
        if (ctx.Attr(e, attribute) is not { } reference) return null;
        var processor = ctx.RouteContext.GetFromRegistry<IProcessor>(reference);
        if (processor is null)
            ctx.AddError(e, $"{attribute} '{reference}' is not in the context registry (expected an IProcessor).");
        return processor;
    }

    /// <summary>An optional log-level attribute; an unknown name is a positioned error, not a silent default.</summary>
    private static Microsoft.Extensions.Logging.LogLevel? LogLevelOf(XElement e, string attribute, XmlParseContext ctx)
    {
        if (ctx.Attr(e, attribute) is not { } name) return null;
        if (Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(name, ignoreCase: true, out var level))
            return level;
        ctx.AddError(e, $"'{name}' is not a log level (Trace, Debug, Information, Warning, Error, Critical).");
        return null;
    }

    /// <summary>Attributes, condition and child steps of an intercept — shared by both levels.</summary>
    internal static void ConfigureIntercept(InterceptDefinition scope, XElement e, XmlParseContext ctx)
    {
        if (ctx.Attr(e, "skipSendToOriginalEndpoint") is not null)
        {
            // Meaningful only where there IS an original send to skip; elsewhere it is a
            // misplaced intention — say so instead of applying it silently.
            if (e.Name.LocalName == "interceptSendToEndpoint")
            {
                if (ctx.Convert<bool>(e, "skipSendToOriginalEndpoint") == true)
                    scope.SkipSendToOriginalEndpoint();
            }
            else
            {
                ctx.AddError(e, "skipSendToOriginalEndpoint is valid only on <interceptSendToEndpoint>.");
            }
        }
        foreach (var child in e.Elements())
        {
            if (IsConditionChild(child, ctx, out var condition))
            {
                if (condition is not null) scope.When(condition);
            }
            else
            {
                ctx.ParseStep(child, scope);
            }
        }
    }

    /// <summary>Attributes, condition and child steps of an onCompletion — shared by both levels.</summary>
    internal static void ConfigureOnCompletion(OnCompletionDefinition scope, XElement e, XmlParseContext ctx)
    {
        if (ctx.Convert<bool>(e, "onCompleteOnly") == true) scope.OnCompleteOnly();
        if (ctx.Convert<bool>(e, "onFailureOnly") == true) scope.OnFailureOnly();
        if (ctx.Convert<bool>(e, "modeBeforeConsumer") == true) scope.ModeBeforeConsumer();
        foreach (var child in e.Elements())
        {
            if (IsConditionChild(child, ctx, out var condition))
            {
                if (condition is not null) scope.When(condition);
            }
            else
            {
                ctx.ParseStep(child, scope);
            }
        }
    }

    /// <summary>
    /// A childless <c>&lt;when expr=…/&gt;</c> inside intercepts and onCompletion is the scope's
    /// condition, not a step (Ф0 §4.2); with children it would be ambiguous and is refused.
    /// </summary>
    private static bool IsConditionChild(XElement child, XmlParseContext ctx, out string? condition)
    {
        condition = null;
        if (child.Name.LocalName != "when")
            return false;
        if (child.Elements().Any())
        {
            ctx.AddError(child, "<when> here is the scope's condition and takes no child steps — put the steps next to it.");
            return true;
        }
        condition = ctx.RequiredAttr(child, "expr");
        return true;
    }

    /// <summary>Resolves the optional strategy attribute: <c>#name</c> from the registry, otherwise a named strategy.</summary>
    private static Func<IExchange, IExchange, IExchange>? MergeStrategy(XElement e, XmlParseContext ctx)
    {
        var name = ctx.Attr(e, "strategy");
        return name is null ? null : NamedStrategy(name, e, ctx);
    }

    private static Func<IExchange, IExchange, IExchange>? NamedStrategy(string name, XElement e, XmlParseContext ctx)
    {
        if (name.StartsWith('#'))
        {
            var registered = ctx.RouteContext.GetFromRegistry<Func<IExchange, IExchange, IExchange>>(name);
            if (registered is null)
                ctx.AddError(e, $"strategy '{name}' is not in the context registry (expected a Func<IExchange, IExchange, IExchange>).");
            return registered;
        }
        try
        {
            return AggregationStrategies.ByName(name);
        }
        catch (ArgumentException ex)
        {
            ctx.AddError(e, ex.Message);
            return null;
        }
    }

    /// <summary>The transform of a normalize branch: an expression in the value position.</summary>
    private static Func<IExchange, object?>? NormalizeTransform(XElement child, XmlParseContext ctx)
    {
        var expr = ctx.RequiredAttr(child, "transform");
        if (expr is null) return null;
        var expression = new StringExpression(expr);
        return exchange => expression.Evaluate<object>(exchange);
    }

    private static TransactionPolicy? Policy(XElement e, XmlParseContext ctx)
    {
        var name = ctx.Attr(e, "policy");
        if (name is null) return null;
        var policy = name.ToLowerInvariant() switch
        {
            "default" => TransactionPolicy.Default,
            "requiresnew" => TransactionPolicy.RequiresNew,
            "suppress" => TransactionPolicy.Suppress,
            "mandatory" => TransactionPolicy.Mandatory,
            _ => null,
        };
        if (policy is null)
            ctx.AddError(e, $"'{name}' is not a transaction policy (default, requiresNew, suppress, mandatory).");
        return policy;
    }

    private static string? RegistryName(string? reference)
        => reference is { Length: > 0 } && reference.StartsWith('#') ? reference[1..] : reference;

    /// <summary>Text of the element itself (CDATA included), ignoring child elements.</summary>
    private static string? ElementText(XElement e)
    {
        var text = string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value));
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>Р5.2: the file attribute or inline content, not both; files load through the route resource resolver.</summary>
    private static string? FileOrContent(XElement e, XmlParseContext ctx, string what)
    {
        var file = ctx.Attr(e, "file");
        var content = ElementText(e);
        if ((file is null) == (content is null))
        {
            ctx.AddError(e, $"<{e.Name.LocalName}> takes either the file attribute or inline content, not both.");
            return null;
        }
        if (content is not null)
            return content;
        try
        {
            return File.ReadAllText(ResourceResolution.Resolve(ctx.RouteContext, file!, what));
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            ctx.AddError(e, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The marshal/unmarshal source: a registered content type (<c>format=</c>, returned as
    /// string) or a serializer type (<c>type=</c>, returned as a fresh instance with the extra
    /// attributes bound to its properties). Extra attributes are refused with <c>format=</c> —
    /// registry serializers are shared instances and must not be mutated per step.
    /// </summary>
    private static object? SerializerFromElement(XElement e, XmlParseContext ctx, bool requireTarget, out Type? target)
    {
        target = null;
        if (requireTarget)
        {
            var targetName = ctx.RequiredAttr(e, "target");
            if (targetName is null) return null;
            target = ResolveType(targetName, e, ctx);
            if (target is null) return null;
        }

        var known = requireTarget
            ? new[] { "format", "type", "target", "id", "description" }
            : ["format", "type", "id", "description"];
        var extras = e.Attributes().Where(a => !known.Contains(a.Name.LocalName)).ToList();
        var format = ctx.Attr(e, "format");
        var typeName = ctx.Attr(e, "type");
        if (format is not null && typeName is not null)
        {
            ctx.AddError(e, $"<{e.Name.LocalName}> takes either format or type, not both.");
            return null;
        }
        if (format is not null)
        {
            if (extras.Count > 0)
                ctx.AddError(e, $"format options ({string.Join(", ", extras.Select(a => a.Name.LocalName))}) " +
                                "need the type= form — a registered format is shared and takes no per-step options.");
            return format;
        }
        if (typeName is null)
        {
            if (!requireTarget)
            {
                ctx.AddError(e, $"<{e.Name.LocalName}> requires format or type.");
                return null;
            }
            return ContentTypeDispatch.Instance; // unmarshal dispatches by the message ContentType
        }

        var type = ResolveType(typeName, e, ctx);
        if (type is null) return null;
        if (!typeof(IMessageSerializer).IsAssignableFrom(type))
        {
            ctx.AddError(e, $"'{typeName}' does not implement IMessageSerializer.");
            return null;
        }
        object instance;
        try
        {
            instance = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"Activator returned null for {type}.");
        }
        catch (Exception ex) when (ex is MissingMethodException or MemberAccessException or InvalidOperationException)
        {
            ctx.AddError(e, $"serializer '{typeName}' has no usable parameterless constructor: {ex.Message}");
            return null;
        }
        foreach (var extra in extras)
        {
            var property = type.GetProperty(char.ToUpperInvariant(extra.Name.LocalName[0]) + extra.Name.LocalName[1..])
                ?? type.GetProperty(extra.Name.LocalName);
            if (property is null || !property.CanWrite)
            {
                ctx.AddError(e, $"serializer '{type.Name}' has no writable property for option '{extra.Name.LocalName}'.");
                continue;
            }
            var converted = OptionValueConverter.Convert(extra.Value, property.PropertyType);
            if (converted is null)
            {
                ctx.AddError(e, $"'{extra.Value}' is not convertible to {property.PropertyType.Name} for option '{extra.Name.LocalName}'.");
                continue;
            }
            property.SetValue(instance, converted);
        }
        return (IMessageSerializer)instance;
    }

    private static TimeSpan? Duration(string value)
        => TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var result) ? result : null;

    /// <summary>Marker: unmarshal with neither format nor type — resolve by the message ContentType.</summary>
    private sealed class ContentTypeDispatch
    {
        public static readonly ContentTypeDispatch Instance = new();
    }
}

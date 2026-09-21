using System.Globalization;
using System.Xml.Linq;

namespace redb.Route.Xml.CoreElements;

internal static partial class CoreContributions
{
    // ── The printers (Ф6): element → the fluent C# its Apply parses into ────
    // The third face of the one list. Generated code is statements against receiver variables
    // (see XmlCodeWriter) — the model the Ф3 equivalence twins proved compilable and
    // tree-identical; a printer mirrors its contribution's DSL calls line for line.

    private static string? A(XElement e, string name) => e.Attribute(name)?.Value;
    private static string S6(XElement e, string name) => XmlCodeWriter.Str(A(e, name) ?? "");
    private static string Reg(string? reference)
        => XmlCodeWriter.Str(reference is { Length: > 0 } && reference.StartsWith('#') ? reference[1..] : reference ?? "");

    private static string StrategyArg(string name)
        => name.StartsWith('#')
            ? $"Context!.GetFromRegistry<Func<IExchange, IExchange, IExchange>>({Reg(name)})!"
            : $"AggregationStrategies.ByName({XmlCodeWriter.Str(name)})";

    private static string PredicateArg(string reference)
        => $"Context!.GetFromRegistry<IPredicate>({Reg(reference)})!";

    private static string AddressUri(XElement e)
    {
        if (A(e, "uri") is { } uri)
            return XmlCodeWriter.Str(uri);
        var child = e.Elements().FirstOrDefault()
            ?? throw new NotSupportedException($"<{e.Name.LocalName}> carries neither uri= nor an endpoint child.");
        return XmlCodeWriter.Str(StructuredEndpoint.NormalizeForPrint(child));
    }

    private static string ElementText6(XElement e)
        => string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value)).Trim();

    private static TimeSpan ParseTs(string value)
        => TimeSpan.Parse(value, CultureInfo.InvariantCulture);

    /// <summary>Trailing-default trimming: print positional args up to the last non-null.</summary>
    private static string[] Trim(params string?[] args)
    {
        var last = Array.FindLastIndex(args, a => a is not null);
        return [.. args.Take(last + 1).Select(a => a ?? "null")];
    }

    private static void PrintRichOrPlainLog(XElement e, XmlCodeWriter w)
    {
        var level = A(e, "level");
        var levelRef = level is null ? null : $"LogLevel.{Enum.Parse<Microsoft.Extensions.Logging.LogLevel>(level, true)}";
        if (!e.Elements().Any())
        {
            var text = XmlCodeWriter.Str(ElementText6(e));
            w.Verb(e, "Log", levelRef is null ? [text] : [text, levelRef]);
            return;
        }
        w.Scope(e, "Log", [levelRef ?? "LogLevel.Information"], "EndLog", body: w2 =>
        {
            if (A(e, "showRouteId") == "true")
                w2.Config("ShowRouteId()");
            foreach (var child in e.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "message": w2.Config($"Message({XmlCodeWriter.Str(child.Value)})"); break;
                    case "header": w2.Config($"Header({S6(child, "name")})"); break;
                    case "property": w2.Config($"Property({S6(child, "name")})"); break;
                }
            }
        });
    }

    internal static readonly IReadOnlyDictionary<string, Action<XElement, XmlCodeWriter>> Printers =
        new Dictionary<string, Action<XElement, XmlCodeWriter>>(StringComparer.Ordinal)
        {
            // ── leaves ──────────────────────────────────────────────────────
            ["to"] = (e, w) => w.Verb(e, "To", AddressUri(e)),
            ["toD"] = (e, w) => w.Verb(e, "ToD", AddressUri(e)),
            ["wireTap"] = (e, w) => w.Verb(e, "WireTap", AddressUri(e)),
            ["enrich"] = (e, w) => w.Verb(e, "Enrich", A(e, "strategy") is { } s
                ? [AddressUri(e), StrategyArg(s)]
                : [AddressUri(e)]),
            ["pollEnrich"] = (e, w) =>
            {
                var timeout = A(e, "timeout") is { } t ? XmlCodeWriter.Ts(ParseTs(t)) : null;
                if (A(e, "strategy") is { } s)
                {
                    var merge = $"(original, polled) => polled is null ? original : {StrategyArg(s)}(original, polled)";
                    w.Verb(e, "PollEnrich", Trim(AddressUri(e), merge, timeout));
                }
                else
                {
                    w.Verb(e, "PollEnrich", Trim(AddressUri(e), timeout));
                }
            },
            ["setHeader"] = (e, w) => w.Verb(e, "SetHeader", S6(e, "name"),
                A(e, "expr") is { } x ? XmlCodeWriter.Expr(x) : S6(e, "value")),
            ["setProperty"] = (e, w) => w.Verb(e, "SetProperty", S6(e, "name"),
                A(e, "expr") is { } x ? XmlCodeWriter.Expr(x) : S6(e, "value")),
            ["setBody"] = (e, w) => w.Verb(e, "SetBody",
                A(e, "expr") is { } x ? XmlCodeWriter.Expr(x) : S6(e, "value")),
            ["transform"] = (e, w) => w.Verb(e, "Transform", XmlCodeWriter.Expr(A(e, "expr") ?? "")),
            ["removeHeader"] = (e, w) => w.Verb(e, "RemoveHeader", S6(e, "name")),
            ["removeProperty"] = (e, w) => w.Verb(e, "RemoveProperty", S6(e, "name")),
            ["removeBody"] = (e, w) => w.Verb(e, "RemoveBody"),
            ["removeHeaders"] = (e, w) => w.Verb(e, "RemoveHeaders",
                [S6(e, "pattern"), .. (A(e, "except") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(XmlCodeWriter.Str)]),
            ["removeProperties"] = (e, w) => w.Verb(e, "RemoveProperties",
                [S6(e, "pattern"), .. (A(e, "except") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(XmlCodeWriter.Str)]),
            ["setHeaders"] = (e, w) => w.Verb(e, "SetHeaders", [.. e.Elements()
                .Where(c => c.Name.LocalName == "header")
                .Select(c => $"({S6(c, "name")}, {(A(c, "expr") is { } x ? XmlCodeWriter.Expr(x) : S6(c, "value"))})")]),
            ["log"] = PrintRichOrPlainLog,
            ["delay"] = (e, w) => w.Verb(e, "Delay",
                A(e, "expr") is { } x ? XmlCodeWriter.Str(x) : XmlCodeWriter.Ts(ParseTs(A(e, "duration") ?? "0:0:0"))),
            ["stop"] = (e, w) => w.Verb(e, "Stop"),
            ["throwException"] = (e, w) => w.Verb(e, "ThrowException",
                XmlCodeWriter.TypeRef(A(e, "type") ?? ""), XmlCodeWriter.Str(A(e, "message") ?? "error")),
            ["convertBody"] = (e, w) => w.Verb(e, "ConvertBody", XmlCodeWriter.TypeRef(A(e, "type") ?? "")),
            ["validate"] = (e, w) => w.Verb(e, "Validate", Trim(
                S6(e, "expr"),
                XmlCodeWriter.Str(A(e, "message") ?? "Validation failed"),
                A(e, "throwOnFailure") is { } t ? XmlCodeWriter.Bool(bool.Parse(t)) : null)),
            ["sort"] = (e, w) => w.Verb(e, "Sort", Trim(
                S6(e, "expr"),
                A(e, "by") is { } by ? XmlCodeWriter.Str(by) : A(e, "descending") is null ? null : "null",
                A(e, "descending") is { } d ? XmlCodeWriter.Bool(bool.Parse(d)) : null)),
            ["sample"] = (e, w) => w.Verb(e, "Sample",
                A(e, "messageFrequency") is { } f ? $"{f}L" : XmlCodeWriter.Ts(ParseTs(A(e, "period") ?? "0:0:1"))),
            ["streamCaching"] = (e, w) => w.Verb(e, "StreamCaching",
                A(e, "spoolThreshold") is { } s ? [$"{s}L"] : []),
            ["messageHistory"] = (e, w) => w.Verb(e, "MessageHistory",
                A(e, "value") is { } mh ? [XmlCodeWriter.Bool(bool.Parse(mh))] : []),
            ["validateJsonSchema"] = (e, w) => w.Verb(e, "ValidateJsonSchema", Trim(
                XmlCodeWriter.Str(A(e, "file") is { } f ? w.ReadResource(f) : ElementText6(e)),
                A(e, "throwOnFailure") is { } t ? XmlCodeWriter.Bool(bool.Parse(t)) : null)),
            ["validateXsd"] = (e, w) => w.Verb(e, "ValidateXsd", Trim(
                A(e, "targetNamespace") is { } ns ? XmlCodeWriter.Str(ns) : "null",
                XmlCodeWriter.Str(A(e, "file") is { } f ? w.ReadResource(f) : ElementText6(e)),
                A(e, "throwOnFailure") is { } t ? XmlCodeWriter.Bool(bool.Parse(t)) : null)),
            ["xslt"] = (e, w) =>
            {
                var output = A(e, "output") is { } o ? $"XsltOutput.{Enum.Parse<Route.Xslt.XsltOutput>(o, true)}" : null;
                var fail = A(e, "failOnNullBody") is { } fb ? XmlCodeWriter.Bool(bool.Parse(fb)) : null;
                var fromHeader = A(e, "allowTemplateFromHeader") is { } h ? XmlCodeWriter.Bool(bool.Parse(h)) : null;
                if (A(e, "file") is { } file)
                    w.Verb(e, "Xslt", Trim(XmlCodeWriter.Str(file), output, fail, fromHeader));
                else
                    w.Verb(e, "XsltContent", Trim(XmlCodeWriter.Str(ElementText6(e)), output, fail, fromHeader));
            },
            ["marshal"] = (e, w) =>
            {
                if (e.Attributes().Any(a => a.Name.LocalName is not ("format" or "type" or "id" or "description")))
                    throw new NotSupportedException("<marshal> with per-step format options is not supported by the code generator yet.");
                w.Verb(e, "Marshal", A(e, "format") is { } f ? XmlCodeWriter.Str(f) : XmlCodeWriter.TypeRef(A(e, "type") ?? ""));
            },
            ["unmarshal"] = (e, w) =>
            {
                var target = XmlCodeWriter.TypeRef(A(e, "target") ?? "");
                if (A(e, "format") is { } f)
                    w.Verb(e, "Unmarshal", XmlCodeWriter.Str(f), target);
                else if (A(e, "type") is { } t)
                    w.Verb(e, "Unmarshal", XmlCodeWriter.TypeRef(t), target);
                else
                    throw new NotSupportedException("<unmarshal> by message ContentType is not supported by the code generator yet.");
            },
            ["controlBus"] = (e, w) => w.Verb(e, "ControlBus",
                $"ControlBusAction.{Enum.Parse<Route.ControlBus.ControlBusAction>(A(e, "action") ?? "", true)}",
                S6(e, "routeId"),
                XmlCodeWriter.Bool(A(e, "async") == "true")),
            ["recipientList"] = (e, w) => w.Verb(e, "RecipientList", Trim(
                S6(e, "expr"),
                A(e, "delimiter") is { } d ? XmlCodeWriter.Str(d) : (A(e, "parallel") ?? A(e, "stopOnException") ?? A(e, "strategy")) is null ? null : "\",\"",
                A(e, "parallel") is { } p ? XmlCodeWriter.Bool(bool.Parse(p)) : (A(e, "stopOnException") ?? A(e, "strategy")) is null ? null : "false",
                A(e, "stopOnException") is { } s ? XmlCodeWriter.Bool(bool.Parse(s)) : A(e, "strategy") is null ? null : "false",
                A(e, "strategy") is { } st ? StrategyArg(st) : null)),
            ["dynamicRouter"] = (e, w) => w.Verb(e, "DynamicRouter", S6(e, "expr")),
            ["routingSlip"] = (e, w) => w.Verb(e, "RoutingSlip", Trim(
                S6(e, "expr"),
                A(e, "delimiter") is { } d ? XmlCodeWriter.Str(d) : A(e, "ignoreInvalidEndpoints") is null ? null : "\",\"",
                A(e, "ignoreInvalidEndpoints") is { } i ? XmlCodeWriter.Bool(bool.Parse(i)) : null)),
            ["claimCheck"] = (e, w) => w.Verb(e, "ClaimCheck", Trim(
                $"ClaimCheckOperation.{Enum.Parse<Route.Abstractions.ClaimCheckOperation>(A(e, "operation") ?? "", true)}",
                A(e, "key") is { } k ? XmlCodeWriter.Str(k) : (A(e, "ttl") ?? A(e, "repository")) is null ? null : "null",
                A(e, "ttl") is { } t ? XmlCodeWriter.Ts(ParseTs(t)) : A(e, "repository") is null ? null : "null",
                A(e, "repository") is { } r ? Reg(r) : null)),
            ["beginTransaction"] = (e, w) => w.Verb(e, "BeginTransaction",
                A(e, "policy") is { } p ? [PolicyRef(p)] : []),
            ["commitTransaction"] = (e, w) => w.Verb(e, "CommitTransaction"),
            ["rollbackTransaction"] = (e, w) => w.Verb(e, "RollbackTransaction"),
            ["rollbackAll"] = (e, w) => w.Verb(e, "RollbackAll"),
            ["exceptionHandled"] = (e, w) => w.Verb(e, "ExceptionHandled"),
            ["routePolicy"] = (e, w) => w.Verb(e, "RoutePolicy", Reg(A(e, "ref"))),

            // ── scopes ──────────────────────────────────────────────────────
            ["filter"] = (e, w) => w.Scope(e, "Filter",
                [A(e, "predicate") is { } p ? PredicateArg(p) : S6(e, "expr")], "EndFilter"),
            ["split"] = (e, w) =>
            {
                var tokenizer = e.Elements().FirstOrDefault(c => c.Name.LocalName
                    is "tokenizeLines" or "tokenizeXml" or "tokenizeJsonArray");
                var source = tokenizer is null
                    ? XmlCodeWriter.Expr(A(e, "expr") ?? "")
                    : tokenizer.Name.LocalName switch
                    {
                        "tokenizeLines" => $"SplitLines({XmlCodeWriter.Str(A(tokenizer, "separator") ?? "\n")}, {XmlCodeWriter.Bool(A(tokenizer, "skipEmpty") == "true")})",
                        "tokenizeXml" => $"SplitXml({S6(tokenizer, "element")}{(A(tokenizer, "inheritNamespaceFrom") is { } inh ? ", " + XmlCodeWriter.Str(inh) : "")})",
                        _ => "SplitJsonArray()",
                    };
                w.Scope(e, "Split", [source], "EndSplit", body: w2 =>
                {
                    if (A(e, "parallel") == "true") w2.Config("ParallelProcessing()");
                    if (A(e, "maxParallelism") is { } dop) w2.Config($"MaxParallelism({dop})");
                    if (A(e, "stopOnException") == "true") w2.Config("StopOnException()");
                    foreach (var child in e.Elements().Where(c => c != tokenizer))
                        w2.PrintStep(child);
                });
            },
            ["multicast"] = (e, w) => w.Scope(e, "Multicast", [], "EndMulticast", body: w2 =>
            {
                if (A(e, "parallel") == "true") w2.Config("ParallelProcessing()");
                if (A(e, "maxParallelism") is { } dop) w2.Config($"MaxParallelism({dop})");
                if (A(e, "stopOnException") == "true") w2.Config("StopOnException()");
                w2.PrintSteps(e);
            }),
            ["aggregate"] = (e, w) => w.Scope(e, "Aggregate", Trim(
                S6(e, "correlation"),
                StrategyArg(A(e, "strategy") ?? ""),
                A(e, "completion") is { } c ? XmlCodeWriter.Str(c) : (A(e, "completionSize") ?? A(e, "completionTimeout")) is null ? null : "null",
                A(e, "completionSize") ?? (A(e, "completionTimeout") is null ? null : "null"),
                A(e, "completionTimeout") is { } t ? XmlCodeWriter.Ts(ParseTs(t)) : null), "EndAggregate"),
            ["loop"] = (e, w) =>
            {
                var copy = A(e, "copy") == "true";
                var share = A(e, "shareScope");
                string[] tail = share is not null
                    ? [XmlCodeWriter.Bool(copy), XmlCodeWriter.Bool(bool.Parse(share))]
                    : copy ? [XmlCodeWriter.Bool(true)] : [];
                if (A(e, "count") is { } count)
                    w.Scope(e, "Loop", [count, .. tail], "EndLoop");
                else if (A(e, "expr") is { } expr)
                    w.Scope(e, "Loop", [XmlCodeWriter.Str(expr), .. tail], "EndLoop");
                else
                    w.Scope(e, "LoopWhile", [XmlCodeWriter.Str(A(e, "while") ?? ""), .. tail], "EndLoop");
            },
            ["throttle"] = (e, w) =>
            {
                var period = A(e, "period") is { } p ? XmlCodeWriter.Ts(ParseTs(p)) : null;
                var max = A(e, "maxPerPeriod") ?? "";
                var isConst = int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
                if (A(e, "key") is { } key)
                {
                    var extractor = $"ex => {XmlCodeWriter.Expr(key)}.Evaluate<object>(ex)?.ToString() ?? string.Empty";
                    var maxArg = isConst ? max : $"ex => {XmlCodeWriter.Expr(max)}.Evaluate<int>(ex)";
                    w.Scope(e, "Throttle", Trim(extractor, maxArg, period), "EndKeyedThrottle", body: w2 =>
                    {
                        if (A(e, "rejectOnOverflow") == "true") w2.Config("RejectOnOverflow()");
                        w2.PrintSteps(e);
                    });
                    return;
                }
                w.Scope(e, "Throttle", isConst ? [max] : Trim(XmlCodeWriter.Str(max), period), "EndThrottle", body: w2 =>
                {
                    if (isConst && period is not null) w2.Config($"Period({period})");
                    if (A(e, "rejectOnOverflow") == "true") w2.Config("RejectOnOverflow()");
                    w2.PrintSteps(e);
                });
            },
            ["debounce"] = (e, w) => w.Scope(e, "Debounce",
                [S6(e, "key"), XmlCodeWriter.Ts(ParseTs(A(e, "quietPeriod") ?? "0:0:1"))], "EndDebounce"),
            ["idempotentConsumer"] = (e, w) => w.Scope(e, "IdempotentConsumer", Trim(
                S6(e, "key"), Reg(A(e, "repository")),
                A(e, "skipDuplicate") is { } s ? XmlCodeWriter.Bool(bool.Parse(s)) : null), "EndIdempotentConsumer"),
            ["resequence"] = (e, w) => w.Scope(e, "Resequence", Trim(
                S6(e, "key"),
                A(e, "batchSize") ?? (A(e, "timeout") is null ? null : "100"),
                A(e, "timeout") is { } t ? XmlCodeWriter.Ts(ParseTs(t)) : null), "EndResequence"),
            ["transaction"] = (e, w) => w.Scope(e, "Transaction",
                A(e, "policy") is { } p ? [PolicyRef(p)] : [], "EndTransaction", body: w2 =>
                {
                    if (A(e, "deadLetterChannel") is not null) w2.Config($"DeadLetterChannel({S6(e, "deadLetterChannel")})");
                    if (A(e, "retryAttempts") is { } attempts && A(e, "retryDelay") is { } retryDelay)
                        w2.Config($"Retry({attempts}, {XmlCodeWriter.Ts(ParseTs(retryDelay))})");
                    w2.PrintSteps(e);
                }),
            ["traced"] = (e, w) => w.Scope(e, "Traced", [S6(e, "name")], "EndTraced"),
            ["metered"] = (e, w) => w.Scope(e, "Metered", [S6(e, "name")], "EndMetered", body: w2 =>
            {
                foreach (var child in e.Elements())
                {
                    if (child.Name.LocalName != "tag")
                    {
                        w2.PrintStep(child);
                        continue;
                    }
                    if (A(child, "fromHeader") is not null)
                        w2.Config($"TagFromHeader({S6(child, "name")}, {S6(child, "fromHeader")})");
                    else
                        w2.Config($"Tag({S6(child, "name")}, ex => {XmlCodeWriter.Expr(A(child, "expr") ?? "")}.Evaluate<object>(ex))");
                }
            }),
            ["replayable"] = (e, w) => w.Scope(e, "Replayable", Trim(
                S6(e, "name"), A(e, "exposed") is { } x ? XmlCodeWriter.Bool(bool.Parse(x)) : null), "EndReplayable"),
            ["threads"] = (e, w) => w.Scope(e, "Threads", [A(e, "poolSize") ?? "1"], "EndThreads", body: w2 =>
            {
                if (A(e, "maxQueueSize") is { } queue) w2.Config($"MaxQueueSize({queue})");
                if (A(e, "enqueueTimeout") is { } enqueue) w2.Config($"EnqueueTimeout({XmlCodeWriter.Ts(ParseTs(enqueue))})");
                w2.PrintSteps(e);
            }),
            // A typed section: the children belong to it, the siblings stay on the parent
            // receiver — the statement model expresses that by simply not closing.
            ["ofType"] = (e, w) => w.Scope(e, "OfType", [XmlCodeWriter.TypeRef(A(e, "type") ?? "")], null),
            ["onException"] = (e, w) =>
            {
                var types = (A(e, "exceptions") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                w.Scope(e, "OnException", [.. types.Select(XmlCodeWriter.TypeRef)], "EndOnException",
                    body: w2 => PrintOnException(e, w2));
            },
            ["intercept"] = (e, w) => PrintIntercept(e, w, "Intercept", []),
            ["interceptFrom"] = (e, w) => PrintIntercept(e, w, "InterceptFrom",
                A(e, "uri") is { } u ? [XmlCodeWriter.Str(u)] : []),
            ["interceptSendToEndpoint"] = (e, w) => PrintIntercept(e, w, "InterceptSendToEndpoint",
                [S6(e, "uri")]),
            ["onCompletion"] = (e, w) => w.Scope(e, "OnCompletion", [], "EndOnCompletion", body: w2 =>
            {
                if (A(e, "onCompleteOnly") == "true") w2.Config("OnCompleteOnly()");
                if (A(e, "onFailureOnly") == "true") w2.Config("OnFailureOnly()");
                if (A(e, "modeBeforeConsumer") == "true") w2.Config("ModeBeforeConsumer()");
                PrintWhenConditionThenSteps(e, w2);
            }),
            ["tryCatch"] = (e, w) => w.Scope(e, "TryCatch", [], "EndTryCatch", body: w2 =>
            {
                foreach (var child in e.Elements())
                {
                    switch (child.Name.LocalName)
                    {
                        case "try":
                            w2.PrintSteps(child);
                            break;
                        case "catch":
                            foreach (var typeName in (A(child, "exceptions") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            {
                                var handler = w2.OpenBranch(child, "Catch", XmlCodeWriter.TypeRef(typeName));
                                w2.PrintSteps(child);
                                w2.CloseBranch(handler, "EndCatch");
                            }
                            break;
                        case "finally":
                            var final = w2.OpenBranch(child, "Finally");
                            w2.PrintSteps(child);
                            w2.CloseBranch(final, "EndFinally");
                            break;
                    }
                }
            }),
            ["circuitBreaker"] = (e, w) => w.Scope(e, "CircuitBreaker", [], "EndCircuitBreaker", body: w2 =>
            {
                if (A(e, "failureThreshold") is { } ft) w2.Config($"FailureThreshold({ft})");
                if (A(e, "resetTimeout") is { } rt) w2.Config($"ResetTimeout({XmlCodeWriter.Ts(ParseTs(rt))})");
                if (A(e, "halfOpenMaxCalls") is { } ho) w2.Config($"HalfOpenMaxCalls({ho})");
                foreach (var child in e.Elements())
                {
                    if (child.Name.LocalName == "fallback")
                    {
                        var fallback = w2.OpenBranch(child, "OnFallback");
                        w2.PrintSteps(child);
                        w2.CloseBranch(fallback, "EndFallback");
                    }
                    else
                    {
                        w2.PrintStep(child);
                    }
                }
            }),
            ["choice"] = (e, w) => w.Scope(e, "Choice", [], "EndChoice", body: w2 =>
            {
                foreach (var branch in e.Elements())
                {
                    switch (branch.Name.LocalName)
                    {
                        case "when":
                        {
                            var when = w2.OpenBranch(branch, "When",
                                A(branch, "predicate") is { } p ? PredicateArg(p) : S6(branch, "expr"));
                            w2.PrintSteps(branch);
                            w2.CloseBranch(when);
                            break;
                        }
                        case "otherwise":
                        {
                            var otherwise = w2.OpenBranch(branch, "Otherwise");
                            w2.PrintSteps(branch);
                            w2.CloseBranch(otherwise);
                            break;
                        }
                    }
                }
            }),
            ["scatterGather"] = (e, w) =>
            {
                var recipients = string.Join(", ", e.Elements()
                    .Where(c => c.Name.LocalName == "recipient").Select(c => S6(c, "uri")));
                w.MapTo(e);
                w.Line($"{w.Receiver}.ScatterGather(scatter =>");
                w.Line("{");
                w.Indent();
                w.Line($"scatter.Recipients({recipients});");
                if (A(e, "strategy") is { } st) w.Line($"scatter.AggregationStrategy({StrategyArg(st)});");
                if (A(e, "timeout") is { } t) w.Line($"scatter.Timeout({XmlCodeWriter.Ts(ParseTs(t))});");
                if (A(e, "parallel") is { } p) w.Line($"scatter.ParallelProcessing({XmlCodeWriter.Bool(bool.Parse(p))});");
                if (A(e, "maxDop") is { } d) w.Line($"scatter.MaxDegreeOfParallelism({d});");
                if (A(e, "stopOnException") is { } s) w.Line($"scatter.StopOnException({XmlCodeWriter.Bool(bool.Parse(s))});");
                w.Outdent();
                w.Line("});");
            },
            ["loadBalance"] = (e, w) =>
            {
                var endpoints = e.Elements().Where(c => c.Name.LocalName == "endpoint").ToList();
                var uris = string.Join(", ", endpoints.Select(c => S6(c, "uri")));
                var strategyCall = A(e, "strategy") switch
                {
                    "roundRobin" => "balance.UseRoundRobin();",
                    "random" => "balance.UseRandom();",
                    "failover" => "balance.UseFailover();",
                    "sticky" => $"balance.UseSticky({S6(e, "key")});",
                    _ => "balance.UseWeighted(new Dictionary<string, int> { " +
                         string.Join(", ", endpoints.Select(c => $"[{S6(c, "uri")}] = {A(c, "weight")}")) + " });",
                };
                w.MapTo(e);
                w.Line($"{w.Receiver}.LoadBalance(balance =>");
                w.Line("{");
                w.Indent();
                w.Line($"balance.Endpoints({uris});");
                w.Line(strategyCall);
                w.Outdent();
                w.Line("});");
            },
            ["saga"] = (e, w) =>
            {
                w.MapTo(e);
                w.Line($"{w.Receiver}.Saga(saga =>");
                w.Line("{");
                w.Indent();
                foreach (var child in e.Elements().Where(c => c.Name.LocalName == "step"))
                {
                    var action = $"Context!.GetFromRegistry<IProcessor>({Reg(A(child, "processor"))})!";
                    var compensate = A(child, "compensate") is { } c
                        ? $", Context!.GetFromRegistry<IProcessor>({Reg(c)})!"
                        : "";
                    w.Line($"saga.Step({action}{compensate});");
                }
                w.Outdent();
                w.Line("});");
            },
            ["normalize"] = (e, w) =>
            {
                w.MapTo(e);
                w.Line($"{w.Receiver}.Normalize(normalizer =>");
                w.Line("{");
                w.Indent();
                foreach (var child in e.Elements())
                {
                    var transform = $"ex => {XmlCodeWriter.Expr(A(child, "transform") ?? "")}.Evaluate<object>(ex)";
                    switch (child.Name.LocalName)
                    {
                        case "when":
                            w.Line($"normalizer.When({S6(child, "expr")}, {transform});");
                            break;
                        case "whenContentType":
                            w.Line($"normalizer.WhenContentType({S6(child, "type")}, {transform});");
                            break;
                        case "otherwise":
                            w.Line($"normalizer.Otherwise({transform});");
                            break;
                    }
                }
                w.Outdent();
                w.Line("});");
            },
        };

    private static string PolicyRef(string name) => name.ToLowerInvariant() switch
    {
        "requiresnew" => "TransactionPolicy.RequiresNew",
        "suppress" => "TransactionPolicy.Suppress",
        "mandatory" => "TransactionPolicy.Mandatory",
        _ => "TransactionPolicy.Default",
    };

    internal static void PrintOnExceptionConfig(XElement e, XmlCodeWriter w)
    {
        if (A(e, "handled") is { } h) w.Config($"Handled({XmlCodeWriter.Bool(bool.Parse(h))})");
        if (A(e, "continued") is { } c) w.Config($"Continued({XmlCodeWriter.Bool(bool.Parse(c))})");
        if (A(e, "maximumRedeliveries") is { } m) w.Config($"MaximumRedeliveries({m})");
        if (A(e, "redeliveryDelay") is { } d) w.Config($"RedeliveryDelay({XmlCodeWriter.Ts(ParseTs(d))})");
        if (A(e, "exponentialBackOff") is { } b) w.Config($"UseExponentialBackOff({XmlCodeWriter.Bool(bool.Parse(b))})");
        if (A(e, "backOffMultiplier") is { } bm) w.Config($"BackOffMultiplier({bm})");
        if (A(e, "useOriginalBody") == "true") w.Config("UseOriginalBody()");
        if (A(e, "logStackTrace") is { } st) w.Config($"LogStackTrace({XmlCodeWriter.Bool(bool.Parse(st))})");
        if (A(e, "logExhausted") is { } le) w.Config($"LogExhausted({XmlCodeWriter.Bool(bool.Parse(le))})");
        if (A(e, "retryAttemptedLogLevel") is { } ra) w.Config($"RetryAttemptedLogLevel({LevelRef(ra)})");
        if (A(e, "retriesExhaustedLogLevel") is { } re) w.Config($"RetriesExhaustedLogLevel({LevelRef(re)})");
        foreach (var (attribute, verb) in OnExceptionProcessorRefs)
            if (A(e, attribute) is { } reference)
                w.Config($"{verb}(Context!.GetFromRegistry<IProcessor>({Reg(reference)})!)");
    }

    /// <summary>The handler's bean-reference attributes and the DSL verb each one calls.</summary>
    private static readonly (string Attribute, string Verb)[] OnExceptionProcessorRefs =
    [
        ("onExceptionOccurred", "OnExceptionOccurred"),
        ("onRedelivery", "OnRedelivery"),
        ("onPrepareFailure", "OnPrepareFailure"),
    ];

    private static string LevelRef(string level)
        => $"LogLevel.{Enum.Parse<Microsoft.Extensions.Logging.LogLevel>(level, true)}";

    /// <summary>
    /// The whole body of an onException: its settings, then its conditions and steps. The
    /// conditions are children that are NOT steps, so the walk cannot be a plain PrintSteps.
    /// </summary>
    internal static void PrintOnException(XElement e, XmlCodeWriter w)
    {
        PrintOnExceptionConfig(e, w);
        foreach (var child in e.Elements())
        {
            if (child.Name.LocalName == "when" && !child.Elements().Any())
                w.Config($"OnWhen({S6(child, "expr")})");
            else if (child.Name.LocalName == "retryWhile" && !child.Elements().Any())
                w.Config($"RetryWhile({S6(child, "expr")})");
            else
                w.PrintStep(child);
        }
    }

    private static void PrintIntercept(XElement e, XmlCodeWriter w, string verb, string[] args)
        => w.Scope(e, verb, args, "EndIntercept", body: w2 =>
        {
            if (A(e, "skipSendToOriginalEndpoint") == "true") w2.Config("SkipSendToOriginalEndpoint()");
            PrintWhenConditionThenSteps(e, w2);
        });

    internal static void PrintWhenConditionThenSteps(XElement e, XmlCodeWriter w)
    {
        foreach (var child in e.Elements())
        {
            if (child.Name.LocalName == "when" && !child.Elements().Any())
                w.Config($"When({S6(child, "expr")})");
            else
                w.PrintStep(child);
        }
    }
}

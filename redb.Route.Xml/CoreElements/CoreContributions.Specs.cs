using redb.Route.Xslt;

namespace redb.Route.Xml.CoreElements;

internal static partial class CoreContributions
{
    // ── The public shapes of the core elements (Ф4 §2) ──────────────────────
    // One table, three consumers: parser hints, the XSD generator, the catalog. A test pins
    // every registered core contribution to an entry here, so the shapes cannot drift from the
    // parse code silently.

    private static AttributeSpec S(string name, bool required = false) => new(name, AttributeType.String, required);
    private static AttributeSpec E(string name, bool required = false) => new(name, AttributeType.Expression, required);
    private static AttributeSpec U(string name, bool required = false) => new(name, AttributeType.Uri, required);
    private static AttributeSpec R(string name, bool required = false) => new(name, AttributeType.Reference, required);
    private static AttributeSpec T(string name, bool required = false) => new(name, AttributeType.TypeName, required);
    private static AttributeSpec B(string name) => new(name, AttributeType.Bool);
    private static AttributeSpec I(string name, bool required = false) => new(name, AttributeType.Int, required);
    private static AttributeSpec L(string name) => new(name, AttributeType.Long);
    private static AttributeSpec F(string name) => new(name, AttributeType.Double);
    private static AttributeSpec D(string name, bool required = false) => new(name, AttributeType.Duration, required);
    private static AttributeSpec En(string name, IReadOnlyList<string> values, bool required = false)
        => new(name, AttributeType.Enum, required, values);

    private static readonly string[] LogLevels =
        ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];
    private static readonly string[] TransactionPolicies =
        ["default", "requiresNew", "suppress", "mandatory"];
    private static readonly string[] LoadBalanceStrategies =
        ["roundRobin", "random", "failover", "sticky", "weighted"];

    private static ElementSpec AddressLeaf(string name, params AttributeSpec[] extra)
        => new(name, XmlElementKind.Step, [U("uri"), .. extra], [], TakesEndpoint: true);

    private static readonly ElementSpec WhenCondition =
        ElementSpec.Child("when", allowsSteps: false, E("expr", required: true));

    /// <summary>The spec for a core element name; opaque for a name the table misses (test-guarded).</summary>
    internal static ElementSpec SpecFor(string name, XmlElementKind kind)
        => Specs.TryGetValue(name, out var spec)
            ? spec
            : new ElementSpec(name, kind, [], [], AllowsSteps: kind is XmlElementKind.Scope);

    internal static readonly IReadOnlyDictionary<string, ElementSpec> Specs =
        new Dictionary<string, ElementSpec>(StringComparer.Ordinal)
        {
            // ── leaves ──────────────────────────────────────────────────────
            ["to"] = AddressLeaf("to"),
            ["toD"] = AddressLeaf("toD"),
            ["wireTap"] = AddressLeaf("wireTap"),
            ["enrich"] = AddressLeaf("enrich", S("strategy")),
            ["pollEnrich"] = AddressLeaf("pollEnrich", S("strategy"), D("timeout")),
            ["setHeader"] = ElementSpec.Leaf("setHeader", S("name", required: true), S("value"), E("expr")),
            ["setProperty"] = ElementSpec.Leaf("setProperty", S("name", required: true), S("value"), E("expr")),
            ["setBody"] = ElementSpec.Leaf("setBody", S("value"), E("expr")),
            ["transform"] = ElementSpec.Leaf("transform", E("expr", required: true)),
            ["removeHeader"] = ElementSpec.Leaf("removeHeader", S("name", required: true)),
            ["removeProperty"] = ElementSpec.Leaf("removeProperty", S("name", required: true)),
            ["removeBody"] = ElementSpec.Leaf("removeBody"),
            ["removeHeaders"] = ElementSpec.Leaf("removeHeaders", S("pattern", required: true), S("except")),
            ["removeProperties"] = ElementSpec.Leaf("removeProperties", S("pattern", required: true), S("except")),
            ["setHeaders"] = new("setHeaders", XmlElementKind.Step, [],
                [ElementSpec.Child("header", false, S("name", required: true), S("value"), E("expr"))]),
            ["log"] = new("log", XmlElementKind.Step,
                [En("level", LogLevels), B("showRouteId")],
                [
                    new ElementSpec("message", XmlElementKind.ConfigChild, [], [], AllowsTextContent: true),
                    ElementSpec.Child("header", false, S("name", required: true)),
                    ElementSpec.Child("property", false, S("name", required: true)),
                ],
                AllowsTextContent: true),
            ["delay"] = ElementSpec.Leaf("delay", D("duration"), E("expr")),
            ["stop"] = ElementSpec.Leaf("stop"),
            ["throwException"] = ElementSpec.Leaf("throwException", T("type", required: true), S("message")),
            ["convertBody"] = ElementSpec.Leaf("convertBody", T("type", required: true)),
            ["validate"] = ElementSpec.Leaf("validate", E("expr", required: true), S("message"), B("throwOnFailure")),
            ["sort"] = ElementSpec.Leaf("sort", E("expr", required: true), E("by"), B("descending")),
            ["sample"] = ElementSpec.Leaf("sample", L("messageFrequency"), D("period")),
            ["streamCaching"] = ElementSpec.Leaf("streamCaching", L("spoolThreshold")),
            ["validateJsonSchema"] = new("validateJsonSchema", XmlElementKind.Step,
                [S("file"), B("throwOnFailure")], [], AllowsTextContent: true),
            ["validateXsd"] = new("validateXsd", XmlElementKind.Step,
                [S("file"), S("targetNamespace"), B("throwOnFailure")], [], AllowsTextContent: true),
            ["xslt"] = new("xslt", XmlElementKind.Step,
                [S("file"), En("output", Enum.GetNames<XsltOutput>()), B("failOnNullBody"), B("allowTemplateFromHeader")],
                [], AllowsTextContent: true),
            ["marshal"] = ElementSpec.Leaf("marshal", S("format"), T("type")),
            ["unmarshal"] = ElementSpec.Leaf("unmarshal", S("format"), T("type"), T("target", required: true)),
            ["controlBus"] = ElementSpec.Leaf("controlBus",
                En("action", Enum.GetNames<ControlBus.ControlBusAction>(), required: true),
                S("routeId", required: true), B("async")),
            ["recipientList"] = ElementSpec.Leaf("recipientList",
                E("expr", required: true), S("delimiter"), B("parallel"), B("stopOnException"), S("strategy")),
            ["dynamicRouter"] = ElementSpec.Leaf("dynamicRouter", E("expr", required: true)),
            ["routingSlip"] = ElementSpec.Leaf("routingSlip",
                E("expr", required: true), S("delimiter"), B("ignoreInvalidEndpoints")),
            ["claimCheck"] = ElementSpec.Leaf("claimCheck",
                En("operation", Enum.GetNames<redb.Route.Abstractions.ClaimCheckOperation>(), required: true),
                E("key"), D("ttl"), R("repository")),
            ["beginTransaction"] = ElementSpec.Leaf("beginTransaction", En("policy", TransactionPolicies)),
            ["commitTransaction"] = ElementSpec.Leaf("commitTransaction"),
            ["rollbackTransaction"] = ElementSpec.Leaf("rollbackTransaction"),
            ["rollbackAll"] = ElementSpec.Leaf("rollbackAll"),
            ["exceptionHandled"] = ElementSpec.Leaf("exceptionHandled"),
            ["routePolicy"] = ElementSpec.Leaf("routePolicy", R("ref", required: true)),

            // ── scopes ──────────────────────────────────────────────────────
            ["filter"] = ElementSpec.Scope("filter", E("expr"), R("predicate")),
            ["split"] = new("split", XmlElementKind.Scope,
                [E("expr"), B("parallel"), I("maxParallelism"), B("stopOnException")],
                [
                    ElementSpec.Child("tokenizeLines", false, S("separator"), B("skipEmpty")),
                    ElementSpec.Child("tokenizeXml", false, S("element", required: true), S("inheritNamespaceFrom")),
                    ElementSpec.Child("tokenizeJsonArray", false),
                ],
                AllowsSteps: true),
            ["multicast"] = ElementSpec.Scope("multicast", B("parallel"), I("maxParallelism"), B("stopOnException")),
            ["aggregate"] = ElementSpec.Scope("aggregate",
                E("correlation", required: true), S("strategy", required: true),
                E("completion"), I("completionSize"), D("completionTimeout")),
            ["loop"] = ElementSpec.Scope("loop", I("count"), E("expr"), E("while"), B("copy"), B("shareScope")),
            ["throttle"] = ElementSpec.Scope("throttle",
                E("maxPerPeriod", required: true), D("period"), E("key"), B("rejectOnOverflow")),
            ["debounce"] = ElementSpec.Scope("debounce", E("key", required: true), D("quietPeriod", required: true)),
            ["idempotentConsumer"] = ElementSpec.Scope("idempotentConsumer",
                E("key", required: true), R("repository", required: true), B("skipDuplicate")),
            ["resequence"] = ElementSpec.Scope("resequence", E("key", required: true), I("batchSize"), D("timeout")),
            ["transaction"] = ElementSpec.Scope("transaction", En("policy", TransactionPolicies)),
            ["traced"] = ElementSpec.Scope("traced", S("name", required: true)),
            ["metered"] = ElementSpec.Scope("metered", S("name", required: true)),
            ["replayable"] = ElementSpec.Scope("replayable", S("name", required: true), B("exposed")),
            ["threads"] = ElementSpec.Scope("threads", I("poolSize", required: true)),
            ["ofType"] = ElementSpec.Scope("ofType", T("type", required: true)),
            ["circuitBreaker"] = new("circuitBreaker", XmlElementKind.Scope,
                [I("failureThreshold"), D("resetTimeout"), I("halfOpenMaxCalls")],
                [ElementSpec.Child("fallback", allowsSteps: true)],
                AllowsSteps: true),
            ["tryCatch"] = new("tryCatch", XmlElementKind.Scope, [],
                [
                    ElementSpec.Child("try", allowsSteps: true),
                    ElementSpec.Child("catch", allowsSteps: true, T("exceptions", required: true)),
                    ElementSpec.Child("finally", allowsSteps: true),
                ]),
            ["onException"] = ElementSpec.Scope("onException",
                T("exceptions", required: true), B("handled"), I("maximumRedeliveries"),
                D("redeliveryDelay"), B("exponentialBackOff"), F("backOffMultiplier")),
            ["intercept"] = new("intercept", XmlElementKind.Scope, [], [WhenCondition], AllowsSteps: true),
            ["interceptFrom"] = new("interceptFrom", XmlElementKind.Scope, [U("uri")], [WhenCondition], AllowsSteps: true),
            ["interceptSendToEndpoint"] = new("interceptSendToEndpoint", XmlElementKind.Scope,
                [U("uri", required: true), B("skipSendToOriginalEndpoint")], [WhenCondition], AllowsSteps: true),
            ["onCompletion"] = new("onCompletion", XmlElementKind.Scope,
                [B("onCompleteOnly"), B("onFailureOnly"), B("modeBeforeConsumer")], [WhenCondition], AllowsSteps: true),
            ["scatterGather"] = new("scatterGather", XmlElementKind.Step,
                [D("timeout"), B("parallel"), I("maxDop"), B("stopOnException"), S("strategy")],
                [ElementSpec.Child("recipient", false, U("uri", required: true))]),
            ["loadBalance"] = new("loadBalance", XmlElementKind.Step,
                [En("strategy", LoadBalanceStrategies, required: true), E("key")],
                [ElementSpec.Child("endpoint", false, U("uri", required: true), I("weight"))]),
            ["normalize"] = new("normalize", XmlElementKind.Step, [],
                [
                    ElementSpec.Child("when", false, E("expr", required: true), E("transform", required: true)),
                    ElementSpec.Child("whenContentType", false, S("type", required: true), E("transform", required: true)),
                    ElementSpec.Child("otherwise", false, E("transform", required: true)),
                ]),

            ["saga"] = new("saga", XmlElementKind.Step, [],
                [ElementSpec.Child("step", false,
                    R("processor", required: true), R("compensate"))]),

            // ── branching ───────────────────────────────────────────────────
            ["choice"] = new("choice", XmlElementKind.Branching, [],
                [
                    ElementSpec.Child("when", allowsSteps: true, E("expr"), R("predicate")),
                    ElementSpec.Child("otherwise", allowsSteps: true),
                ]),
        };
}

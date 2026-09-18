/**
 * The §6 category of every format element — what the eye reads before the label: shape and
 * color come from the category, not the icon. The assignment is mechanical over the
 * generated element list (media/redb-route-elements.json); a unit test pins completeness,
 * so a new registry element without a category fails the build instead of rendering blank.
 */

export type Category =
    | "source"        // from
    | "sendExternal"  // to on a transport — filled, accent
    | "sendInternal"  // to on direct/seda/vm — outlined, same accent
    | "transform"     // the lightest, neutral
    | "flow"          // accent color, larger than the rest
    | "errors"        // warning color
    | "userCode"      // bean:
    | "unknown";      // the opaque node (Р10)

const FLOW = [
    "choice", "filter", "split", "multicast", "loop", "tryCatch", "throttle", "aggregate",
    "resequence", "threads", "debounce", "idempotentConsumer", "ofType", "replayable",
    "loadBalance", "recipientList", "dynamicRouter", "routingSlip", "scatterGather",
    "sample", "delay", "stop",
];

const ERRORS = [
    "onException", "onCompletion", "circuitBreaker", "validate", "validateJsonSchema",
    "validateXsd", "throwException", "exceptionHandled", "rollbackAll", "rollbackTransaction",
    "transaction", "beginTransaction", "commitTransaction", "saga",
];

const TRANSFORM = [
    "setBody", "setHeader", "setHeaders", "setProperty", "removeBody", "removeHeader",
    "removeHeaders", "removeProperty", "removeProperties", "transform", "convertBody",
    "marshal", "unmarshal", "normalize", "xslt", "sort", "claimCheck",
    // neutral observability and infrastructure — the lightest weight on purpose
    "log", "metered", "traced", "streamCaching", "messageHistory", "controlBus", "routePolicy",
    "intercept", "interceptFrom", "interceptSendToEndpoint",
    // package elements (present when the palette was refreshed with --bin)
    "cache", "transformJson", "payload", "redbGet", "redbSave", "redbDelete", "redbQuery",
];

const BY_NAME = new Map<string, Category>([
    ...FLOW.map(n => [n, "flow"] as const),
    ...ERRORS.map(n => [n, "errors"] as const),
    ...TRANSFORM.map(n => [n, "transform"] as const),
]);

/** Schemes that stay inside the process — outlined, not filled (§3.1). */
const INTERNAL_SCHEMES = new Set(["direct", "seda", "vm", "direct-vm"]);

export function categoryOf(elementName: string): Category | null {
    return BY_NAME.get(elementName) ?? null;
}

/** The category of an endpoint-carrying node, decided by its URI. */
export function endpointCategory(uri: string): Category {
    const scheme = uri.split(":", 1)[0].toLowerCase();
    if (scheme === "bean") return "userCode";
    return INTERNAL_SCHEMES.has(scheme) ? "sendInternal" : "sendExternal";
}

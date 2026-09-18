/**
 * XML tree → the render tree of 09-UX §3: a horizontal main line, branches only where the
 * flow really branches, scopes as brackets over their stretch, the unknown as an opaque node
 * that survives byte-for-byte. Pure projection — nothing here mutates the document.
 */

import { XmlElement, attr, childElements, textContent, Span } from "./xmlmodel";
import { Category, categoryOf, endpointCategory } from "./category";

export interface ElementInfo {
    name: string;
    kind: "Step" | "Scope" | "Branching" | "TopLevel" | "ContextLevel";
    attributes: { name: string; type: string; required?: boolean; enumValues?: string[] }[];
    children?: ElementInfo[];
    allowsText?: boolean;
    takesEndpoint?: boolean;
}

/** The generated element list (media/redb-route-elements.json), indexed by name. */
export type ElementIndex = Map<string, ElementInfo>;

export function buildIndex(elements: ElementInfo[]): ElementIndex {
    return new Map(elements.map(e => [e.name, e]));
}

// ── the render tree ──────────────────────────────────────────────────

export type StepView = LeafView | ScopeView | BranchingView | UnknownView;

export interface LeafView {
    kind: "leaf";
    path: number[];
    type: string;
    category: Category;
    /** ≤ ~14 chars, secrets redacted. */
    label: string;
    /** Full text for the tooltip — secrets redacted the same way. */
    tooltip: string;
    id: string | null;
    span: Span;
}

export interface ScopeView {
    kind: "scope";
    path: number[];
    type: string;
    category: Category;
    /** The bracket's header: condition or main parameter. */
    label: string;
    tooltip: string;
    /** §3.5: split repeats its one body per element. */
    repeats: boolean;
    parallel: boolean;
    id: string | null;
    steps: StepView[];
    span: Span;
}

export interface BranchView {
    path: number[];
    /** `when ${…}` / `otherwise` / `catch X` / `finally` / plain index for multicast. */
    label: string;
    warn: boolean;
    steps: StepView[];
    span: Span;
}

export interface BranchingView {
    kind: "branching";
    path: number[];
    type: string;
    category: Category;
    id: string | null;
    branches: BranchView[];
    span: Span;
}

export interface UnknownView {
    kind: "unknown";
    path: number[];
    /** The raw tag name as written. */
    type: string;
    category: "unknown";
    label: string;
    tooltip: string;
    span: Span;
}

export interface RouteView {
    path: number[];
    id: string | null;
    description: string | null;
    from: LeafView | null;
    steps: StepView[];
    /** onException/onCompletion/intercept* of this route — the strip above (§3.9). */
    strips: ScopeView[];
    span: Span;
}

export interface BeanDeclaration {
    name: string;
    type: string;
    span: Span;
}

export interface FileGraph {
    routes: RouteView[];
    /** Container-level handlers — the strip above the whole file. */
    strips: ScopeView[];
    /** `<bean name= type=>` declarations — a muted registry row, not steps. */
    beans: BeanDeclaration[];
    /** Top-level nodes the graph does not interpret (rest, unknown). */
    others: UnknownView[];
}

// ── labels (§5) ──────────────────────────────────────────────────────

const SECRET_QUERY = /((?:password|pwd|secret|token|apikey|accesskey|sharedaccesskey|clientsecret)[^=&\s]*=)[^&"'\s]+/gi;
const SECRET_USERINFO = /(\/\/[^/:@\s]+:)[^@/\s]+@/g;

/** URI with every secret masked — labels and tooltips both go through this. */
export function redactUri(uri: string): string {
    return uri.replace(SECRET_USERINFO, "$1***@").replace(SECRET_QUERY, "$1***");
}

export function trimLabel(label: string, max = 22): string {
    return label.length <= max ? label : label.slice(0, max - 1) + "…";
}

/** scheme + last path segment of the RESOLVED uri, marked when it came from config. */
export function endpointLabelResolved(uri: string, resolver: UriResolver): string {
    const { resolved, fromConfig } = resolver(uri);
    const label = endpointLabel(resolved);
    return fromConfig ? label + " ⚙" : label;
}

/** raw → resolved in the tooltip when config took part; secrets redacted in both. */
export function endpointTooltip(uri: string, resolver: UriResolver): string {
    const { resolved, fromConfig } = resolver(uri);
    return fromConfig
        ? redactUri(uri) + "  →  " + redactUri(resolved)
        : redactUri(uri);
}

/** scheme + last path segment: `kafka orders-vip`, `direct orders` (§5). */
export function endpointLabel(uri: string): string {
    const redacted = redactUri(uri);
    const scheme = redacted.split(":", 1)[0];
    const rest = redacted.slice(scheme.length + 1).replace(/^\/\//, "");
    const path = rest.split("?", 1)[0];
    const last = path.split("/").filter(s => s.length > 0).pop() ?? "";
    return last.length > 0 ? `${scheme} ${last}` : scheme;
}

/**
 * The node's main parameter. A name-carrying step reads as `name = value` (§3.2:
 * `setHeader priority = high`); otherwise the first Expression attribute, then inline text,
 * then the first attribute.
 */
function mainParameter(element: XmlElement, info: ElementInfo | undefined): string | null {
    const name = attr(element, "name")?.value ?? attr(element, "key")?.value ?? null;
    let expression: string | null = null;
    if (info) {
        for (const spec of info.attributes) {
            if (spec.type !== "Expression") continue;
            const found = attr(element, spec.name);
            if (found) { expression = found.value; break; }
        }
    }
    if (name !== null)
        return expression !== null ? `${name} = ${expression}` : name;
    if (expression !== null) return expression;
    const inline = textContent(element).trim();
    if (inline.length > 0) return inline;
    const first = element.attributes.find(a => a.name !== "id" && a.name !== "description");
    return first ? first.value : null;
}

// ── building the views ───────────────────────────────────────────────

const STRIP_ELEMENTS = new Set(["onException", "onCompletion", "intercept", "interceptFrom", "interceptSendToEndpoint"]);
const ENDPOINT_LEAFS = new Set(["to", "toD", "wireTap", "enrich", "pollEnrich"]);

export type UriResolver = (uri: string) => { resolved: string; fromConfig: boolean };
const IDENTITY_RESOLVER: UriResolver = uri => ({ resolved: uri, fromConfig: false });

export function buildFileGraph(root: XmlElement, index: ElementIndex, resolve?: UriResolver): FileGraph {
    const resolver = resolve ?? IDENTITY_RESOLVER;
    const routes: RouteView[] = [];
    const strips: ScopeView[] = [];
    const beans: BeanDeclaration[] = [];
    const others: UnknownView[] = [];

    childElements(root).forEach((child, i) => {
        if (child.local === "route") routes.push(buildRoute(child, index, [i], resolver));
        else if (STRIP_ELEMENTS.has(child.local)) strips.push(buildScope(child, index, [i], resolver));
        else if (child.local === "bean" && child.prefix === null)
            beans.push({
                name: attr(child, "name")?.value ?? "(anonymous)",
                type: attr(child, "type")?.value ?? "",
                span: child.span,
            });
        else others.push(unknownView(child, [i]));
    });
    return { routes, strips, beans, others };
}

export function buildRoute(route: XmlElement, index: ElementIndex, path: number[], resolver: UriResolver = IDENTITY_RESOLVER): RouteView {
    let from: LeafView | null = null;
    const steps: StepView[] = [];
    const strips: ScopeView[] = [];

    childElements(route).forEach((child, i) => {
        const childPath = [...path, i];
        if (child.local === "from") {
            from = {
                kind: "leaf",
                path: childPath,
                type: "from",
                category: "source",
                label: trimLabel(endpointLabelResolved(attr(child, "uri")?.value ?? "", resolver)),
                tooltip: endpointTooltip(attr(child, "uri")?.value ?? "", resolver),
                id: attr(child, "id")?.value ?? null,
                span: child.span,
            };
            return;
        }
        if (STRIP_ELEMENTS.has(child.local)) {
            strips.push(buildScope(child, index, childPath, resolver));
            return;
        }
        steps.push(buildStep(child, index, childPath, resolver));
    });

    return {
        path,
        id: attr(route, "id")?.value ?? null,
        description: attr(route, "description")?.value ?? null,
        from,
        steps,
        strips,
        span: route.span,
    };
}

export function buildStep(element: XmlElement, index: ElementIndex, path: number[], resolver: UriResolver = IDENTITY_RESOLVER): StepView {
    const info = index.get(element.local);
    if (!info || element.prefix !== null)
        return unknownView(element, path);

    if (element.local === "choice") return buildChoice(element, index, path, resolver);
    if (element.local === "tryCatch") return buildTryCatch(element, index, path, resolver);
    if (element.local === "multicast") return buildMulticast(element, index, path, resolver);
    if (info.kind === "Scope") return buildScope(element, index, path, resolver);
    return buildLeaf(element, info, path, resolver);
}

function buildLeaf(element: XmlElement, info: ElementInfo, path: number[], resolver: UriResolver = IDENTITY_RESOLVER): LeafView {
    const description = attr(element, "description")?.value ?? null;
    let category = categoryOf(element.local) ?? "transform";
    let label: string;
    let tooltip: string;

    const uri = attr(element, "uri")?.value;
    if (ENDPOINT_LEAFS.has(element.local) && uri) {
        category = endpointCategory(resolver(uri).resolved);
        label = description ?? endpointLabelResolved(uri, resolver);
        tooltip = endpointTooltip(uri, resolver);
    } else if (element.local === "log" && childElements(element).length > 0) {
        // Rich log (§3.8): one node, the content summarized.
        const kids = childElements(element);
        const messages = kids.filter(k => k.local === "message").length;
        const headers = kids.filter(k => k.local === "header").length;
        const properties = kids.filter(k => k.local === "property").length;
        label = description ?? "log";
        tooltip = `log: ${messages} message(s), ${headers} header(s), ${properties} propert(ies)`;
    } else {
        const parameter = mainParameter(element, info);
        label = description ?? parameter ?? element.local;
        tooltip = parameter === null
            ? element.local
            : `${element.local}  ${redactUri(parameter)}`;
    }

    return {
        kind: "leaf",
        path,
        type: element.local,
        category,
        label: trimLabel(redactUri(label)),
        tooltip,
        id: attr(element, "id")?.value ?? null,
        span: element.span,
    };
}

function buildScope(element: XmlElement, index: ElementIndex, path: number[], resolver: UriResolver = IDENTITY_RESOLVER): ScopeView {
    const info = index.get(element.local);
    const parameter = mainParameter(element, info);
    const description = attr(element, "description")?.value ?? null;
    // Branch children of the scope's own spec (e.g. tryCatch handled elsewhere) are steps here.
    const steps = childElements(element).map((child, i) => buildStep(child, index, [...path, i], resolver));

    return {
        kind: "scope",
        path,
        type: element.local,
        category: categoryOf(element.local) ?? "flow",
        label: trimLabel(redactUri(description ?? parameter ?? element.local), 40),
        tooltip: parameter ? `${element.local}  ${redactUri(parameter)}` : element.local,
        repeats: element.local === "split",
        parallel: (attr(element, "parallel")?.value ?? attr(element, "parallelProcessing")?.value) === "true",
        id: attr(element, "id")?.value ?? null,
        steps,
        span: element.span,
    };
}

function buildChoice(element: XmlElement, index: ElementIndex, path: number[], resolver: UriResolver = IDENTITY_RESOLVER): BranchingView {
    const branches: BranchView[] = [];
    childElements(element).forEach((child, i) => {
        if (child.local === "when") {
            const condition = attr(child, "expr")?.value ?? attr(child, "predicate")?.value ?? "";
            branches.push(branch(`when ${redactUri(condition)}`, false, child, index, [...path, i], resolver));
        } else if (child.local === "otherwise") {
            branches.push(branch("otherwise", false, child, index, [...path, i], resolver));
        }
    });
    return branchingView(element, "choice", branches, path);
}

function buildTryCatch(element: XmlElement, index: ElementIndex, path: number[], resolver: UriResolver = IDENTITY_RESOLVER): BranchingView {
    // The format has the explicit <try> wrapper (spec children: try/catch/finally); loose
    // steps directly under <tryCatch> are read as the body too.
    const body: BranchView = { path, label: "try", warn: false, steps: [], span: element.span };
    const branches: BranchView[] = [body];
    childElements(element).forEach((child, i) => {
        const childPath = [...path, i];
        if (child.local === "try") {
            body.span = child.span;
            body.path = childPath;
            body.steps.push(...childElements(child).map((c, j) => buildStep(c, index, [...childPath, j], resolver)));
        } else if (child.local === "catch")
            branches.push(branch(`catch ${shortException(attr(child, "exception")?.value)}`, true, child, index, childPath, resolver));
        else if (child.local === "finally")
            branches.push(branch("finally", true, child, index, childPath, resolver));
        else
            body.steps.push(buildStep(child, index, childPath, resolver));
    });
    return branchingView(element, "tryCatch", branches, path);
}

function buildMulticast(element: XmlElement, index: ElementIndex, path: number[], resolver: UriResolver = IDENTITY_RESOLVER): BranchingView {
    // §3.4: like choice, one row per destination, no conditions.
    const branches = childElements(element).map((child, i) => ({
        path: [...path, i],
        label: String(i + 1),
        warn: false,
        steps: [buildStep(child, index, [...path, i], resolver)],
        span: child.span,
    }));
    return branchingView(element, "multicast", branches, path);
}

function branch(label: string, warn: boolean, element: XmlElement, index: ElementIndex, path: number[], resolver: UriResolver = IDENTITY_RESOLVER): BranchView {
    return {
        path,
        label: trimLabel(label, 40),
        warn,
        steps: childElements(element).map((child, i) => buildStep(child, index, [...path, i], resolver)),
        span: element.span,
    };
}

function branchingView(element: XmlElement, type: string, branches: BranchView[], path: number[]): BranchingView {
    return {
        kind: "branching",
        path,
        type,
        category: categoryOf(type) ?? "flow",
        id: attr(element, "id")?.value ?? null,
        branches,
        span: element.span,
    };
}

function unknownView(element: XmlElement, path: number[]): UnknownView {
    return {
        kind: "unknown",
        path,
        type: element.name,
        category: "unknown",
        label: trimLabel(`<${element.name}>`, 20),
        tooltip: `<${element.name}> — kept verbatim`,
        span: element.span,
    };
}

function shortException(full: string | undefined): string {
    if (!full) return "";
    const last = full.split(",")[0].trim().split(".").pop() ?? full;
    return last;
}

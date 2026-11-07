/**
 * Registry references (`#name`) in attribute values: where the cursor sits on one, and which
 * names the document and its module's `context.xml` declare with `<bean name>`. The positions are
 * the ones the pack gate reads as references (RoutePackage.RegistryReferences): the whole value (`dataSource="#db"`),
 * the path right after the scheme (`bean:#x`) and a URI option value (`?headerFilterStrategy=#f`).
 * A '#' anywhere else is text, so the sql parameter `:#name` is left alone.
 */

import { sniff } from "./sniff";
import { ElementIndex } from "./graphmodel";
import { openElements } from "./skeletonscope";
import { tagEnd } from "./tagscan";

const NAME = /[A-Za-z0-9_.\-]*$/;

/** Where a half-typed reference name starts, when the offset is inside one. */
export type ReferencePosition =
    | { ok: true; /** offset right after the `#`. */ replaceFrom: number }
    | { ok: false };

export function referencePosition(text: string, offset: number): ReferencePosition {
    const before = text.slice(0, offset);

    const commentStart = before.lastIndexOf("<!--");
    if (commentStart >= 0 && before.indexOf("-->", commentStart) < 0)
        return { ok: false };
    const cdataStart = before.lastIndexOf("<![CDATA[");
    if (cdataStart >= 0 && before.indexOf("]]>", cdataStart) < 0)
        return { ok: false };

    // Inside a start tag, inside a quoted value: `name="` (or `'`) with no closing quote yet.
    const lastOpen = before.lastIndexOf("<");
    if (lastOpen < 0 || tagEnd(before, lastOpen) >= 0)
        return { ok: false };
    const value = openValue(before.slice(lastOpen));
    if (value === null)
        return { ok: false };

    const name = NAME.exec(value)![0];
    const head = value.slice(0, value.length - name.length);
    if (!head.endsWith("#"))
        return { ok: false };
    const lead = head.slice(0, -1);
    const query = lead.indexOf("?");
    const atReference = lead === ""
        || (query < 0 && /^[A-Za-z][A-Za-z0-9+.\-]*:(\/\/)?$/.test(lead))
        || (query >= 0 && /^[A-Za-z][A-Za-z0-9+.\-]*:/.test(lead) && /[?&][^=&]+=$/.test(lead));
    return atReference ? { ok: true, replaceFrom: offset - name.length } : { ok: false };
}

/**
 * A value of an attribute the element list types as a registry reference (`predicate=`,
 * `repository=`, `processor=` …), before any `#` is typed: the whole value so far is a bare name,
 * and completion replaces it with `#name`. The type comes from the generated list, so no attribute
 * name is known here; a child entry (`<when>` of `<choice>`, `<step>` of `<saga>`) is looked up
 * under its parent, as the skeleton scope does.
 */
export function typedReferencePosition(text: string, offset: number, index: ElementIndex): ReferencePosition {
    const open = openAttribute(text, offset);
    if (!open || !/^[A-Za-z0-9_.\-]*$/.test(open.value))
        return { ok: false };
    return attributeType(text, offset, open, index) === "Reference"
        ? { ok: true, replaceFrom: open.valueStart }
        : { ok: false };
}

/** The element list's type of the open attribute, a child entry looked up under its parent first. */
function attributeType(text: string, offset: number, open: OpenAttribute, index: ElementIndex): string | undefined {
    const parents = openElements(text, text.lastIndexOf("<", offset - 1));
    const parent = parents.length > 0 ? parents[parents.length - 1] : null;
    const info = (parent === null ? undefined : index.get(parent)?.children?.find(c => c.name === open.element))
        ?? index.get(open.element);
    return info?.attributes.find(a => a.name === open.attribute)?.type;
}

/**
 * The expression namespaces that name a registry object, with the name as each reads it. `bean:`
 * calls a method, so the dot ends the name; `ref:` reads the object itself, so the name is the
 * whole run, dots included, and nothing follows it.
 */
const REGISTRY_NAMESPACES: readonly { prefix: string; name: RegExp }[] = [
    { prefix: "bean:", name: /[A-Za-z0-9_-]*$/ },
    { prefix: "ref:", name: /[A-Za-z0-9_.-]*$/ },
];

/**
 * Where an expression names a registry object, when the offset is inside that name: a bean call
 * (`${bean:pricing.Gross(body)}`) or a registry read (`${ref:rates}`). Two spellings each: the
 * template hole `${bean:` / `${ref:` in any attribute value or element text, and the bare
 * `bean:` / `ref:` of a condition or value, which only an attribute the element list types as an
 * expression reads, at the start of the value or after an operator. After a bean's `.` the method
 * follows, which the editor does not know, so nothing is offered there. A namespace inside a quoted
 * literal of the expression is text.
 */
export function beanCallPosition(text: string, offset: number, index: ElementIndex): ReferencePosition {
    const before = text.slice(0, offset);
    const commentStart = before.lastIndexOf("<!--");
    if (commentStart >= 0 && before.indexOf("-->", commentStart) < 0)
        return { ok: false };

    let name = "";
    let namespace: string | null = null;
    for (const candidate of REGISTRY_NAMESPACES) {
        const run = candidate.name.exec(before)![0];
        if (before.slice(0, before.length - run.length).endsWith(candidate.prefix)) {
            name = run;
            namespace = candidate.prefix;
            break;
        }
    }
    if (namespace === null)
        return { ok: false };
    const head = before.slice(0, before.length - name.length);

    // Text of a CDATA section is element text (a template body), not a tag.
    const cdataStart = before.lastIndexOf("<![CDATA[");
    const inCdata = cdataStart >= 0 && before.indexOf("]]>", cdataStart) < 0;
    const lastOpen = before.lastIndexOf("<");
    const inTag = !inCdata && lastOpen >= 0 && tagEnd(before, lastOpen) < 0;
    const open = inTag ? openAttribute(text, offset) : null;
    if (inTag && !open)
        return { ok: false };

    if (head.endsWith("${" + namespace))
        return { ok: true, replaceFrom: offset - name.length };

    if (!open || attributeType(text, offset, open, index) !== "Expression")
        return { ok: false };
    const lead = open.value.slice(0, open.value.length - name.length - namespace.length);
    if (insideLiteral(lead))
        return { ok: false };
    return lead === "" || /[\s(,!=<>&|?:~]$/.test(lead)
        ? { ok: true, replaceFrom: offset - name.length }
        : { ok: false };
}

/** Whether an expression prefix ends inside a quoted literal (backslash escapes the quote). */
function insideLiteral(expression: string): boolean {
    let quote: string | null = null;
    for (let i = 0; i < expression.length; i++) {
        const c = expression[i];
        if (quote) {
            if (c === "\\") i++;
            else if (c === quote) quote = null;
        } else if (c === "'" || c === '"') {
            quote = c;
        }
    }
    return quote !== null;
}

/**
 * The start tag and attribute the offset sits in, with the value typed so far, or null outside an
 * open attribute value. The element is the tag being typed, its parent the innermost open element
 * before it (both local names); the caller asks the element list for the attribute's type.
 */
export interface OpenAttribute {
    element: string;
    attribute: string;
    /** Offset of the first character of the value. */
    valueStart: number;
    value: string;
}

export function openAttribute(text: string, offset: number): OpenAttribute | null {
    const before = text.slice(0, offset);
    const commentStart = before.lastIndexOf("<!--");
    if (commentStart >= 0 && before.indexOf("-->", commentStart) < 0)
        return null;
    const cdataStart = before.lastIndexOf("<![CDATA[");
    if (cdataStart >= 0 && before.indexOf("]]>", cdataStart) < 0)
        return null;
    const lastOpen = before.lastIndexOf("<");
    if (lastOpen < 0 || tagEnd(before, lastOpen) >= 0)
        return null;
    const tag = before.slice(lastOpen);
    const value = openValue(tag);
    if (value === null)
        return null;
    const head = /<([\w.\-:]+)/.exec(tag);
    const attribute = /([\w.\-:]+)\s*=\s*["']$/.exec(tag.slice(0, tag.length - value.length));
    if (!head || !attribute)
        return null;
    const local = (name: string) => name.slice(name.indexOf(":") + 1);
    return {
        element: local(head[1]),
        attribute: local(attribute[1]),
        valueStart: offset - value.length,
        value,
    };
}

/** The text of the attribute value the tag fragment ends in, or null outside a value. */
function openValue(tag: string): string | null {
    let quote: string | null = null;
    let start = 0;
    for (let i = 0; i < tag.length; i++) {
        const c = tag[i];
        if (quote) {
            if (c === quote) quote = null;
        } else if ((c === '"' || c === "'") && /=\s*$/.test(tag.slice(0, i))) {
            quote = c;
            start = i + 1;
        }
    }
    return quote ? tag.slice(start) : null;
}

/**
 * The names of the `<bean name="…">` declarations under the root, in document order, without
 * duplicates; a step or endpoint `<bean>` inside a route declares nothing.
 * A scan rather than a parse: the document is half-typed while the user completes a reference.
 * Commented-out beans do not count.
 */
export function beanNames(text: string): string[] {
    const live = text.replace(/<!--[\s\S]*?(-->|$)/g, "");
    const names: string[] = [];
    for (const match of live.matchAll(/<(?:[\w.\-]+:)?bean\b([^<>]*)/g)) {
        // A declaration sits under the root; a `<bean>` deeper down is a step or an endpoint.
        if (openElements(live, match.index!).length > 1)
            continue;
        const name = /\sname\s*=\s*(?:"([^"]*)"|'([^']*)')/.exec(match[1]);
        const value = name ? (name[1] ?? name[2]) : "";
        if (value && !names.includes(value))
            names.push(value);
    }
    return names;
}

/** A bean name `#` offers, and whether it comes from the module's `context.xml`. */
export interface BeanCandidate {
    name: string;
    fromContext: boolean;
}

/**
 * The names `#` offers in a document: its own `<bean name>` first, then those of the module's
 * `context.xml`, each name once. The context text counts only when it is a redb context document
 * (root `<context>` in the redb namespace); any other file at that path adds nothing.
 */
export function beanCandidates(documentText: string, contextText: string | null): BeanCandidate[] {
    const own = beanNames(documentText);
    const candidates: BeanCandidate[] = own.map(name => ({ name, fromContext: false }));
    if (contextText !== null && sniff(contextText)?.kind === "context")
        for (const name of beanNames(contextText))
            if (!own.includes(name))
                candidates.push({ name, fromContext: true });
    return candidates;
}

/**
 * A position-exact XML tree — the foundation of the Ф8 graph editor. The editor edits THIS
 * tree's source spans surgically (10-VSCODE §8.2: the truth is the text; the graph is a
 * projection): an unknown element stays a node with exact bytes, an attribute edit knows the
 * one span to replace, a block move carries the block verbatim. Nothing here interprets the
 * route format — this is the XML layer under it.
 *
 * Offsets are UTF-16 code units (JavaScript string indexing == VSCode document offsets).
 * Spans are [start, end).
 */

export interface Span {
    start: number;
    end: number;
}

export interface XmlAttribute {
    /** Attribute name as written (may carry a prefix). */
    name: string;
    /** Decoded is NOT attempted — the raw text between the quotes. */
    value: string;
    /** The name token. */
    nameSpan: Span;
    /** Inside the quotes — the span to replace when editing the value. */
    valueSpan: Span;
    /** name="value" including both quotes — the span to remove with the attribute. */
    span: Span;
}

export type XmlNode = XmlElement | XmlText | XmlComment | XmlCData | XmlMisc;

export interface XmlElement {
    kind: "element";
    /** Qualified name as written. */
    name: string;
    prefix: string | null;
    local: string;
    attributes: XmlAttribute[];
    selfClosing: boolean;
    /** The whole element, open tag through close tag. */
    span: Span;
    /** `<name …>` including both angle brackets. */
    openTag: Span;
    /** `</name>` or null when self-closing. */
    closeTag: Span | null;
    /** Between the tags; null when self-closing. */
    content: Span | null;
    children: XmlNode[];
    parent: XmlElement | null;
}

export interface XmlText {
    kind: "text";
    span: Span;
    /** Raw text, entities untouched. */
    text: string;
}

export interface XmlComment {
    kind: "comment";
    span: Span;
    /** Between `<!--` and `-->`. */
    text: string;
}

export interface XmlCData {
    kind: "cdata";
    span: Span;
    /** Between `<![CDATA[` and `]]>`. */
    text: string;
}

/** Prolog, PI, DOCTYPE — kept opaque, byte-preserved like everything else. */
export interface XmlMisc {
    kind: "misc";
    span: Span;
}

export class XmlParseError extends Error {
    constructor(message: string, readonly offset: number) {
        super(message);
    }
}

/** The parsed document: the root element plus everything around it. */
export interface XmlDocumentModel {
    root: XmlElement;
    /** Every top-level node in order (prolog, comments, the root, trailing bits). */
    prolog: XmlNode[];
    text: string;
}

const NAME_START = /[A-Za-z_:]/;
const NAME_CHAR = /[\w.:-]/;

export function parseXml(text: string): XmlDocumentModel {
    let position = 0;
    let root: XmlElement | null = null;
    const prolog: XmlNode[] = [];

    const fail = (message: string, at: number): never => {
        throw new XmlParseError(message, at);
    };

    const parseAttributes = (owner: { attributes: XmlAttribute[] }): void => {
        for (;;) {
            while (position < text.length && /\s/.test(text[position])) position++;
            const ch = text[position];
            if (ch === ">" || ch === "/" || ch === "?" || position >= text.length) return;
            if (!NAME_START.test(ch)) fail(`unexpected '${ch}' in a tag`, position);

            const nameStart = position;
            while (position < text.length && NAME_CHAR.test(text[position])) position++;
            const name = text.slice(nameStart, position);
            const nameSpan = { start: nameStart, end: position };

            while (position < text.length && /\s/.test(text[position])) position++;
            if (text[position] !== "=") fail(`attribute '${name}' has no value`, position);
            position++;
            while (position < text.length && /\s/.test(text[position])) position++;
            const quote = text[position];
            if (quote !== '"' && quote !== "'") fail(`attribute '${name}' value is not quoted`, position);
            position++;
            const valueStart = position;
            const valueEnd = text.indexOf(quote, position);
            if (valueEnd < 0) fail(`attribute '${name}' value never closes`, valueStart);
            position = valueEnd + 1;

            owner.attributes.push({
                name,
                value: text.slice(valueStart, valueEnd),
                nameSpan,
                valueSpan: { start: valueStart, end: valueEnd },
                span: { start: nameStart, end: position },
            });
        }
    };

    const parseElement = (parent: XmlElement | null): XmlElement => {
        const openStart = position;
        position++; // consumed '<' by caller's check
        const nameStart = position;
        if (!NAME_START.test(text[position] ?? "")) fail("element name expected after '<'", position);
        while (position < text.length && NAME_CHAR.test(text[position])) position++;
        const name = text.slice(nameStart, position);
        const colon = name.indexOf(":");

        const element: XmlElement = {
            kind: "element",
            name,
            prefix: colon < 0 ? null : name.slice(0, colon),
            local: colon < 0 ? name : name.slice(colon + 1),
            attributes: [],
            selfClosing: false,
            span: { start: openStart, end: -1 },
            openTag: { start: openStart, end: -1 },
            closeTag: null,
            content: null,
            children: [],
            parent,
        };

        parseAttributes(element);
        if (text[position] === "/") {
            if (text[position + 1] !== ">") fail(`'>' expected in <${name}>`, position);
            position += 2;
            element.selfClosing = true;
            element.openTag.end = element.span.end = position;
            return element;
        }
        if (text[position] !== ">") fail(`'>' expected in <${name}>`, position);
        position++;
        element.openTag.end = position;
        const contentStart = position;

        for (;;) {
            if (position >= text.length)
                fail(`<${name}> is never closed`, openStart);
            if (text[position] !== "<") {
                const textStart = position;
                while (position < text.length && text[position] !== "<") position++;
                element.children.push({
                    kind: "text",
                    span: { start: textStart, end: position },
                    text: text.slice(textStart, position),
                });
                continue;
            }
            if (text.startsWith("</", position)) {
                const closeStart = position;
                const closeEnd = text.indexOf(">", position);
                if (closeEnd < 0) fail(`</${name}> never closes`, position);
                const closing = text.slice(closeStart + 2, closeEnd).trim();
                if (closing !== name)
                    fail(`</${closing}> closes <${name}>`, closeStart);
                position = closeEnd + 1;
                element.closeTag = { start: closeStart, end: position };
                element.content = { start: contentStart, end: closeStart };
                element.span.end = position;
                return element;
            }
            element.children.push(parseNodeAt(element));
        }
    };

    const parseNodeAt = (parent: XmlElement | null): XmlNode => {
        // The caller guarantees text[position] === "<".
        if (text.startsWith("<!--", position)) {
            const end = text.indexOf("-->", position + 4);
            if (end < 0) fail("comment never closes", position);
            const span = { start: position, end: end + 3 };
            const value = text.slice(position + 4, end);
            position = span.end;
            return { kind: "comment", span, text: value };
        }
        if (text.startsWith("<![CDATA[", position)) {
            const end = text.indexOf("]]>", position + 9);
            if (end < 0) fail("CDATA never closes", position);
            const span = { start: position, end: end + 3 };
            const value = text.slice(position + 9, end);
            position = span.end;
            return { kind: "cdata", span, text: value };
        }
        if (text.startsWith("<?", position)) {
            const end = text.indexOf("?>", position + 2);
            if (end < 0) fail("processing instruction never closes", position);
            const span = { start: position, end: end + 2 };
            position = span.end;
            return { kind: "misc", span };
        }
        if (text.startsWith("<!", position)) {
            const end = text.indexOf(">", position + 2);
            if (end < 0) fail("declaration never closes", position);
            const span = { start: position, end: end + 1 };
            position = span.end;
            return { kind: "misc", span };
        }
        return parseElement(parent);
    };

    while (position < text.length) {
        if (text[position] !== "<") {
            const start = position;
            while (position < text.length && text[position] !== "<") position++;
            prolog.push({ kind: "text", span: { start, end: position }, text: text.slice(start, position) });
            continue;
        }
        const node = parseNodeAt(null);
        prolog.push(node);
        if (node.kind === "element") {
            if (root !== null) fail("more than one root element", node.span.start);
            root = node;
        }
    }

    if (root === null) fail("no root element", 0);
    return { root: root!, prolog, text };
}

/** Child ELEMENTS only, in order. */
export function childElements(element: XmlElement): XmlElement[] {
    return element.children.filter((c): c is XmlElement => c.kind === "element");
}

/** The attribute by name, or null. */
export function attr(element: XmlElement, name: string): XmlAttribute | null {
    return element.attributes.find(a => a.name === name) ?? null;
}

/** The concatenated text+cdata content of an element (for inline conditions etc.). */
export function textContent(element: XmlElement): string {
    return element.children
        .filter((c): c is XmlText | XmlCData => c.kind === "text" || c.kind === "cdata")
        .map(c => c.text)
        .join("");
}

/** The deepest element whose span contains the offset (for cursor→node sync). */
export function elementAt(root: XmlElement, offset: number): XmlElement | null {
    if (offset < root.span.start || offset >= root.span.end) return null;
    for (;;) {
        const child = childElements(root).find(
            c => offset >= c.span.start && offset < c.span.end);
        if (!child) return root;
        root = child;
    }
}

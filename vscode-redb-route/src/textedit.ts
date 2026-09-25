/**
 * Surgical text edits over the position-exact tree (10-VSCODE §8.3): an attribute edit
 * touches ONLY its own spot — replace inside the quotes, insert one ` name="value"` before
 * the tag's end, or remove the attribute with its leading whitespace. The file is never
 * reserialized; the diff of an attribute change is one line, pinned by tests.
 */

import { parseXml, XmlElement, childElements, attr, XmlDocumentModel } from "./xmlmodel";

/** One replacement: [start, end) → newText. */
export interface TextReplacement {
    start: number;
    end: number;
    newText: string;
}

/** Index chain among child ELEMENTS from the document root (the webview's node address). */
export type ElementPath = number[];

export function resolveByPath(root: XmlElement, path: ElementPath): XmlElement | null {
    let current = root;
    for (const index of path) {
        const children = childElements(current);
        if (index < 0 || index >= children.length) return null;
        current = children[index];
    }
    return current;
}

const ESCAPES: [RegExp, string][] = [
    [/&/g, "&amp;"],
    [/</g, "&lt;"],
    [/"/g, "&quot;"],
];

export function escapeAttributeValue(value: string): string {
    let out = value;
    for (const [pattern, replacement] of ESCAPES)
        out = out.replace(pattern, replacement);
    return out;
}

/**
 * The edit that sets, adds or removes (value === null) one attribute of the element at the
 * path. Returns null when there is nothing to do (same value; removing the absent).
 */
export function computeSetAttribute(
    text: string, path: ElementPath, name: string, value: string | null): TextReplacement | null {
    const root = parseXml(text).root;
    const element = resolveByPath(root, path);
    if (!element)
        throw new Error(`no element at path [${path.join(",")}] — the document changed under the panel`);

    const existing = attr(element, name);

    if (value === null) {
        if (!existing) return null;
        // Take the attribute together with the whitespace BEFORE it — the line stays tidy.
        let start = existing.span.start;
        while (start > 0 && (text[start - 1] === " " || text[start - 1] === "\t")) start--;
        return { start, end: existing.span.end, newText: "" };
    }

    const escaped = escapeAttributeValue(value);
    if (existing) {
        if (existing.value === escaped) return null;
        return { start: existing.valueSpan.start, end: existing.valueSpan.end, newText: escaped };
    }

    // Insert before the tag's closer: `/>` for self-closing, `>` otherwise.
    const insertAt = element.openTag.end - (element.selfClosing ? 2 : 1);
    return { start: insertAt, end: insertAt, newText: ` ${name}="${escaped}"` };
}

/**
 * Renames the element at the path (`<setProperty …/>` → `<setHeader …/>`): the open tag and,
 * when there is one, the close tag; attributes and content stay byte-identical.
 */
export function computeRenameElement(text: string, path: ElementPath, newName: string): TextReplacement | null {
    const element = resolveByPath(parseXml(text).root, path);
    if (!element)
        throw new Error(`no element at path [${path.join(",")}] — the document changed under the panel`);
    if (element.name === newName) return null;
    const body = text.slice(element.span.start, element.span.end);
    const openName = "<" + element.name;
    if (!body.startsWith(openName))
        throw new Error(`the element at [${path.join(",")}] does not start with ${openName}`);
    let renamed = "<" + newName + body.slice(openName.length);
    if (!element.selfClosing) {
        const closeTag = new RegExp("</" + element.name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "\\s*>$");
        if (!closeTag.test(renamed))
            throw new Error(`the element at [${path.join(",")}] does not end with </${element.name}>`);
        renamed = renamed.replace(closeTag, "</" + newName + ">");
    }
    return { start: element.span.start, end: element.span.end, newText: renamed };
}

/** Applies a replacement — test helper and the non-VSCode half of the apply path. */
export function applyReplacement(text: string, replacement: TextReplacement): string {
    return text.slice(0, replacement.start) + replacement.newText + text.slice(replacement.end);
}

const XML_TEXT_ESCAPES: [RegExp, string][] = [
    [/&/g, "&amp;"],
    [/</g, "&lt;"],
];

export function escapeTextContent(value: string): string {
    let out = value;
    for (const [pattern, replacement] of XML_TEXT_ESCAPES)
        out = out.replace(pattern, replacement);
    return out;
}

/**
 * Replaces the element's TEXT content (`<message>old</message>` → new). A self-closing
 * element grows a close tag: `<message/>` → `<message>new</message>`.
 */
export function computeSetTextContent(
    text: string, path: ElementPath, value: string): TextReplacement | null {
    const element = resolveByPath(parseXml(text).root, path);
    if (!element)
        throw new Error(`no element at path [${path.join(",")}]`);
    const escaped = escapeTextContent(value);

    if (element.selfClosing) {
        const openTag = text.slice(element.openTag.start, element.openTag.end);
        const withoutCloser = openTag.slice(0, -2).trimEnd();
        return {
            start: element.openTag.start,
            end: element.openTag.end,
            newText: `${withoutCloser}>${escaped}</${element.name}>`,
        };
    }
    if (element.content === null) return null;
    const current = text.slice(element.content.start, element.content.end);
    if (current === escaped) return null;
    return { start: element.content.start, end: element.content.end, newText: escaped };
}

/**
 * Removes the element together with its own line when the line holds nothing else — the
 * whitespace before it and the line break go too, neighbouring lines stay byte-identical.
 */
export function computeRemoveElement(text: string, path: ElementPath): TextReplacement {
    const element = resolveByPath(parseXml(text).root, path);
    if (!element)
        throw new Error(`no element at path [${path.join(",")}]`);

    let start = element.span.start;
    while (start > 0 && (text[start - 1] === " " || text[start - 1] === "\t")) start--;
    let end = element.span.end;
    const lineStartsClean = start === 0 || text[start - 1] === "\n";
    if (lineStartsClean) {
        // The element owned its line: consume the trailing line break as well.
        if (text[end] === "\r") end++;
        if (text[end] === "\n") end++;
    }
    return { start, end, newText: "" };
}

/**
 * Inserts a child fragment on its own line before the parent's close tag, indented like the
 * existing children (or the parent's indent plus two spaces). A self-closing parent grows a
 * body first.
 */
export function computeInsertChild(
    text: string, parentPath: ElementPath, fragment: string): TextReplacement {
    const parent = resolveByPath(parseXml(text).root, parentPath);
    if (!parent)
        throw new Error(`no element at path [${parentPath.join(",")}]`);

    const lineIndentAt = (offset: number): string => {
        let lineStart = text.lastIndexOf("\n", offset - 1) + 1;
        let indent = "";
        while (lineStart < text.length && (text[lineStart] === " " || text[lineStart] === "\t")) {
            indent += text[lineStart];
            lineStart++;
        }
        return indent;
    };
    const parentIndent = lineIndentAt(parent.span.start);

    if (parent.selfClosing) {
        const openTag = text.slice(parent.openTag.start, parent.openTag.end);
        const withoutCloser = openTag.slice(0, -2).trimEnd();
        return {
            start: parent.openTag.start,
            end: parent.openTag.end,
            newText: `${withoutCloser}>\n${parentIndent}  ${fragment}\n${parentIndent}</${parent.name}>`,
        };
    }

    const children = childElements(parent);
    const childIndent = children.length > 0
        ? lineIndentAt(children[children.length - 1].span.start)
        : parentIndent + "  ";

    const closeStart = parent.closeTag!.start;
    const closeLineStart = text.lastIndexOf("\n", closeStart - 1) + 1;
    const closeOnOwnLine = /^[ \t]*$/.test(text.slice(closeLineStart, closeStart));

    return closeOnOwnLine
        // Insert a whole line above the close tag's line — that line stays untouched.
        ? { start: closeLineStart, end: closeLineStart, newText: indentFragment(fragment, childIndent) }
        // Inline close tag (<log>text</log>): open a body around the fragment.
        : { start: closeStart, end: closeStart, newText: `\n${indentFragment(fragment, childIndent).slice(0, -1)}\n${parentIndent}` };
}

/** Every line of a (possibly multi-line) fragment indented, with a trailing line break. */
function indentFragment(fragment: string, indent: string): string {
    return fragment.split("\n").map(line => indent + line).join("\n") + "\n";
}

/** Removes the element's whole BLOCK — the step and the comments glued to it (§8.4 Delete). */
export function computeRemoveBlock(text: string, path: ElementPath): TextReplacement {
    const model = parseXml(text);
    const element = resolveByPath(model.root, path);
    if (!element)
        throw new Error(`no element at path [${path.join(",")}]`);
    const block = blockSpan(model, element);
    return { start: block.start, end: block.end, newText: "" };
}

function lineIndent(text: string, offset: number): string {
    let lineStart = text.lastIndexOf("\n", offset - 1) + 1;
    let indent = "";
    while (lineStart < text.length && (text[lineStart] === " " || text[lineStart] === "\t")) {
        indent += text[lineStart];
        lineStart++;
    }
    return indent;
}

/**
 * The element's BLOCK: its own span widened to whole lines, plus the contiguous comment
 * lines directly above it (§8.3: «перестановка переносит блоки целиком, включая комментарии
 * над ними»). A blank line breaks the comment chain.
 */
export function blockSpan(model: XmlDocumentModel, element: XmlElement): { start: number; end: number } {
    const text = model.text;
    let start = element.span.start;
    // Widen to the line start when the element owns its line.
    let lineStart = text.lastIndexOf("\n", start - 1) + 1;
    if (/^[ \t]*$/.test(text.slice(lineStart, start))) start = lineStart;

    // Walk preceding siblings: comments (and the whitespace between) attach to the block.
    if (element.parent) {
        const siblings = element.parent.children;
        let i = siblings.indexOf(element) - 1;
        while (i >= 0) {
            const sibling = siblings[i];
            if (sibling.kind === "text" && /^[ \t\r\n]*$/.test(sibling.text)) {
                // Whitespace: a blank line (two line breaks) breaks the chain.
                const breaks = sibling.text.split("\n").length - 1;
                if (breaks > 1) break;
                i--;
                continue;
            }
            if (sibling.kind !== "comment") break;
            let commentStart = sibling.span.start;
            const commentLine = text.lastIndexOf("\n", commentStart - 1) + 1;
            if (/^[ \t]*$/.test(text.slice(commentLine, commentStart))) commentStart = commentLine;
            start = commentStart;
            i--;
        }
    }

    let end = element.span.end;
    if (text[end] === "\r") end++;
    if (text[end] === "\n") end++;
    return { start, end };
}

/**
 * Inserts a step fragment among the parent's children at the given child index (0 =
 * first, children.length = append). Own line, sibling indentation.
 */
export function computeInsertAt(
    text: string, parentPath: ElementPath, index: number, fragment: string): TextReplacement {
    const model = parseXml(text);
    const parent = resolveByPath(model.root, parentPath);
    if (!parent)
        throw new Error(`no element at path [${parentPath.join(",")}]`);
    const children = childElements(parent);

    if (parent.selfClosing || children.length === 0 || index >= children.length)
        return computeInsertChild(text, parentPath, fragment);

    // Before the anchor's whole BLOCK: a step's comments stay glued to their step (§8.3).
    const anchor = children[Math.max(0, index)];
    const anchorStart = blockSpan(model, anchor).start;
    const indent = lineIndent(text, anchor.span.start);
    return { start: anchorStart, end: anchorStart, newText: indentFragment(fragment, indent) };
}

/**
 * Moves the element's whole BLOCK (comments included) to another position: remove here,
 * insert there — two ranges of ONE workspace edit, both computed against the same original
 * text. Returns null for a no-op (dropping onto itself).
 */
export function computeMoveElement(
    text: string, fromPath: ElementPath, toParentPath: ElementPath, toIndex: number): TextReplacement[] | null {
    const model = parseXml(text);
    const element = resolveByPath(model.root, fromPath);
    const target = resolveByPath(model.root, toParentPath);
    if (!element || !target)
        throw new Error("the document changed under the drag");
    // Into itself or its own subtree — refuse quietly.
    for (let e: XmlElement | null = target; e !== null; e = e.parent)
        if (e === element) return null;

    const block = blockSpan(model, element);
    const blockText = text.slice(block.start, block.end);

    // The insertion point in ORIGINAL coordinates.
    const targetChildren = childElements(target);
    let insertAt: number;
    let indent: string;
    if (target.selfClosing) {
        // Let computeInsertChild handle body expansion; a moved block into a self-closing
        // parent is rare — do it as remove + expanded insert of the bare element text.
        const expanded = computeInsertChild(text, toParentPath, blockText.trim());
        return dedupeRanges([{ start: block.start, end: block.end, newText: "" }, expanded]);
    } else if (targetChildren.length === 0 || toIndex >= targetChildren.length) {
        const closeStart = target.closeTag!.start;
        const closeLineStart = text.lastIndexOf("\n", closeStart - 1) + 1;
        insertAt = /^[ \t]*$/.test(text.slice(closeLineStart, closeStart)) ? closeLineStart : closeStart;
        indent = targetChildren.length > 0
            ? lineIndent(text, targetChildren[targetChildren.length - 1].span.start)
            : lineIndent(text, target.span.start) + "  ";
    } else {
        // Before the anchor's whole block — never between a step and its comments.
        const anchor = targetChildren[Math.max(0, toIndex)];
        insertAt = blockSpan(model, anchor).start;
        indent = lineIndent(text, anchor.span.start);
    }

    if (insertAt >= block.start && insertAt <= block.end) return null; // same spot

    // Re-indent the block to the target's depth: strip the block's own leading indent from
    // every line, prepend the target indent.
    const oldIndent = lineIndent(text, element.span.start);
    const reindented = blockText
        .split("\n")
        .map(line => line.startsWith(oldIndent) ? indent + line.slice(oldIndent.length) : line)
        .join("\n");
    const insertion = reindented.endsWith("\n") ? reindented : reindented + "\n";

    return dedupeRanges([
        { start: block.start, end: block.end, newText: "" },
        { start: insertAt, end: insertAt, newText: insertion },
    ]);
}

function dedupeRanges(ranges: TextReplacement[]): TextReplacement[] {
    return ranges.filter(r => r.start !== r.end || r.newText !== "");
}

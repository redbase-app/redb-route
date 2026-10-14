/**
 * The place a skeleton may take at an offset: the container level (a `<route>` beside the other
 * routes) or a step position (inside an element that holds steps). The answer comes from the
 * generated element list (media/redb-route-elements.json) — the same list the palette and the
 * property panel read — so it cannot drift from the schema.
 *
 * Why this exists: the provider offered every skeleton at every element position, so `<choice>` and
 * `<tryCatch>` appeared directly under `<routes>`, where the loader refuses them
 * ("<choice> is not valid at the container level", XmlRouteLoader.cs) and where the schema never
 * offered them (owner finding 2026-10-08).
 *
 * The open elements are read TEXTUALLY, not with the tree parser: a completion request arrives on a
 * half-typed document (`<ch`), and the parser refuses that by design ("'>' expected") — exactly when
 * the answer is wanted.
 */

import { ElementIndex } from "./graphmodel";
import { SkeletonPlace } from "./skeletons";

/** The local name of a tag (`r:route` → `route`). */
function localName(name: string): string {
    const colon = name.indexOf(":");
    return colon < 0 ? name : name.slice(colon + 1);
}

/**
 * The open element names at the offset, outermost first. Comments, CDATA, processing instructions
 * and a doctype are skipped; a tag without its `>` before the offset is the element being typed and
 * is NOT open yet; an end tag pops the nearest element of that name, so an unbalanced document (the
 * normal state while editing) still reads.
 */
function openElements(text: string, offset: number): string[] {
    const stack: string[] = [];
    for (let i = 0; ;) {
        const open = text.indexOf("<", i);
        if (open < 0 || open >= offset)
            break;
        const skip = (marker: string, closer: string): boolean => {
            const end = text.indexOf(closer, open + marker.length);
            if (end >= 0 && end < offset) {
                i = end + closer.length;
                return true;
            }
            return false;
        };
        if (text.startsWith("<!--", open) && skip("<!--", "-->")) continue;
        if (text.startsWith("<![CDATA[", open) && skip("<![CDATA[", "]]>")) continue;
        if (text.startsWith("<?", open) && skip("<?", "?>")) continue;
        if (text.startsWith("<!", open) && skip("<!", ">")) continue;

        const close = text.indexOf(">", open);
        if (close < 0 || close >= offset)
            break;
        const tag = text.slice(open + 1, close);
        i = close + 1;

        if (tag.startsWith("/")) {
            const at = stack.lastIndexOf(localName(tag.slice(1).trim()));
            if (at >= 0) stack.length = at;
            continue;
        }
        const name = localName(tag.split(/[\s/]/, 1)[0]);
        if (name.length > 0 && !/\/\s*$/.test(tag))
            stack.push(name);
    }
    return stack;
}

/** Whether an element holds steps: its own entry, or the entry it has as a child of its parent. */
function allowsSteps(name: string, parent: string | null, index: ElementIndex): boolean {
    const own = index.get(name);
    if (own)
        return own.allowsSteps === true;
    const child = parent === null ? undefined : index.get(parent)?.children?.find(c => c.name === name);
    return child?.allowsSteps === true;
}

/**
 * The place a skeleton may take at the offset, or null when the position takes none. Null is the
 * honest answer for everything the list does not know: a foreign element, a handler's condition
 * `<when>` (which holds no steps), the inside of a start tag, a position outside the root.
 */
export function skeletonPlace(text: string, offset: number, index: ElementIndex): SkeletonPlace | null {
    const open = openElements(text, offset);
    const element = open[open.length - 1];
    if (element === undefined)
        return null;
    const parent = open.length > 1 ? open[open.length - 2] : null;
    if (parent === null)
        return element === "routes" ? "document-root" : null;
    if (allowsSteps(element, parent, index))
        return "step-content";
    // `route` and `bean` are the loader's own skeleton, not registry contributions, so the element
    // list carries no entry for them: a route holds steps, a bean holds property rows.
    if (parent === "routes" && !index.has(element))
        return element === "bean" ? null : "step-content";
    return null;
}


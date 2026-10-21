/**
 * Registry references (`#name`) in attribute values: where the cursor sits on one, and which
 * names the document declares with `<bean name>`. The positions are the ones the pack gate
 * reads as references (RoutePackage.RegistryReferences): the whole value (`dataSource="#db"`),
 * the path right after the scheme (`bean:#x`) and a URI option value (`?headerFilterStrategy=#f`).
 * A '#' anywhere else is text, so the sql parameter `:#name` is left alone.
 */

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
    if (lastOpen < 0 || before.lastIndexOf(">") > lastOpen)
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
 * The names of every `<bean name="…">` in the document, in document order, without duplicates.
 * A scan rather than a parse: the document is half-typed while the user completes a reference.
 * Commented-out beans do not count.
 */
export function beanNames(text: string): string[] {
    const live = text.replace(/<!--[\s\S]*?(-->|$)/g, "");
    const names: string[] = [];
    for (const match of live.matchAll(/<(?:[\w.\-]+:)?bean\b([^<>]*)/g)) {
        const name = /\sname\s*=\s*(?:"([^"]*)"|'([^']*)')/.exec(match[1]);
        const value = name ? (name[1] ?? name[2]) : "";
        if (value && !names.includes(value))
            names.push(value);
    }
    return names;
}

/**
 * The Ф7 activation contract (owner decision 2026-09-04): features light up because of what
 * the DOCUMENT says, never because of its file name. A file is ours when its root element is
 * <routes> or <context> in the urn:redb:route namespace of the supported major. A foreign
 * route.xml stays untouched.
 *
 * This is a structural sniff of the root start-tag only — not a parser of the format (the
 * format's one parser lives in redb.Route.Xml; Ф7 ships none).
 */

/** The namespace prefix every 1.x document carries; minor versions differ after it (§3.2). */
export const NAMESPACE_MAJOR_PREFIX = "urn:redb:route:1.";

export type SniffResult = {
    kind: "routes" | "context";
    namespace: string;
};

/** Skips the XML declaration, processing instructions, comments and whitespace. */
function skipProlog(text: string): number {
    let i = 0;
    for (;;) {
        while (i < text.length && /\s/.test(text[i])) i++;
        if (text.startsWith("<?", i)) {
            const end = text.indexOf("?>", i);
            if (end < 0) return text.length;
            i = end + 2;
            continue;
        }
        if (text.startsWith("<!--", i)) {
            const end = text.indexOf("-->", i);
            if (end < 0) return text.length;
            i = end + 3;
            continue;
        }
        return i;
    }
}

/**
 * Sniffs the document text. Returns null for anything that is not ours — a different root,
 * a different namespace, a different major, or simply not XML.
 */
export function sniff(text: string): SniffResult | null {
    const start = skipProlog(text);
    if (text[start] !== "<") return null;

    const tagEnd = text.indexOf(">", start);
    if (tagEnd < 0) return null;
    const tag = text.slice(start + 1, tagEnd);

    const nameMatch = /^([A-Za-z_][\w.-]*)(?::([A-Za-z_][\w.-]*))?[\s/>]?/.exec(tag + ">");
    if (!nameMatch) return null;
    const prefix = nameMatch[2] ? nameMatch[1] : null;
    const local = nameMatch[2] ?? nameMatch[1];
    if (local !== "routes" && local !== "context") return null;

    const declaration = prefix === null ? "xmlns" : `xmlns:${prefix}`;
    const nsMatch = new RegExp(`(?:^|\\s)${declaration}\\s*=\\s*(?:"([^"]*)"|'([^']*)')`).exec(tag);
    if (!nsMatch) return null;
    const namespace = nsMatch[1] ?? nsMatch[2];
    if (!namespace.startsWith(NAMESPACE_MAJOR_PREFIX)) return null;

    return { kind: local, namespace };
}

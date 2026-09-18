/**
 * The route-tree scan: every <route> start-tag of a routes document with its position, id,
 * description and first <from> endpoint. A tolerant structural walk for the sidebar — the
 * tree opens lines, it does not interpret the format (Ф7 ships no parser of the language).
 */

export type RouteEntry = {
    /** The route's id attribute, when present. */
    id: string | null;
    /** The route's description attribute, when present. */
    description: string | null;
    /** The first <from uri=…> inside the route, when present. */
    from: string | null;
    /** 0-based line of the <route> start-tag. */
    line: number;
};

function attribute(tag: string, name: string): string | null {
    const match = new RegExp(`(?:^|\\s)${name}\\s*=\\s*(?:"([^"]*)"|'([^']*)')`).exec(tag);
    return match ? (match[1] ?? match[2]) : null;
}

/** Finds start-tags of one local element name, prefix-agnostic, comments and CDATA skipped. */
function* startTags(text: string, local: string): Generator<{ tag: string; index: number }> {
    const pattern = new RegExp(`<(?:[A-Za-z_][\\w.-]*:)?${local}(?=[\\s/>])`, "g");
    for (;;) {
        const match = pattern.exec(text);
        if (!match) return;
        const commentStart = text.lastIndexOf("<!--", match.index);
        if (commentStart >= 0 && text.indexOf("-->", commentStart) > match.index) continue;
        const cdataStart = text.lastIndexOf("<![CDATA[", match.index);
        if (cdataStart >= 0 && text.indexOf("]]>", cdataStart) > match.index) continue;
        const end = text.indexOf(">", match.index);
        if (end < 0) return;
        yield { tag: text.slice(match.index, end), index: match.index };
    }
}

function lineOf(lineStarts: number[], index: number): number {
    let low = 0;
    let high = lineStarts.length - 1;
    while (low < high) {
        const mid = (low + high + 1) >> 1;
        if (lineStarts[mid] <= index) low = mid;
        else high = mid - 1;
    }
    return low;
}

/** Scans a routes document. The caller has already sniffed it as ours. */
export function scanRoutes(text: string): RouteEntry[] {
    const lineStarts = [0];
    for (let i = 0; i < text.length; i++)
        if (text[i] === "\n") lineStarts.push(i + 1);

    const routes = [...startTags(text, "route")];
    const froms = [...startTags(text, "from")];

    return routes.map((route, i) => {
        const routeEnd = i + 1 < routes.length ? routes[i + 1].index : text.length;
        const from = froms.find(f => f.index > route.index && f.index < routeEnd);
        return {
            id: attribute(route.tag, "id"),
            description: attribute(route.tag, "description"),
            from: from ? attribute(from.tag, "uri") : null,
            line: lineOf(lineStarts, route.index),
        };
    });
}

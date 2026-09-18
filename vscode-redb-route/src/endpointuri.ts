/**
 * Raw, order-preserving endpoint-URI surgery for the properties panel: the panel decomposes
 * `kafka://orders?groupId=svc&brokers=…` into catalog-typed fields, and an edit rebuilds the
 * SAME uri with one parameter touched — no decoding, no reordering, `{{placeholders}}`
 * opaque. The rebuilt uri then rides the ordinary one-line attribute edit.
 */

export interface EndpointParam {
    name: string;
    value: string;
}

export interface ParsedEndpointUri {
    /** Everything before the first ':' — the scheme. */
    scheme: string;
    /** Between scheme separator and '?' — kept verbatim (may hold {{placeholders}}). */
    path: string;
    /** Whether the uri used `scheme://` (true) or bare `scheme:` (bean:, cron:). */
    doubleSlash: boolean;
    params: EndpointParam[];
}

export function parseEndpointUri(uri: string): ParsedEndpointUri | null {
    const colon = uri.indexOf(":");
    if (colon <= 0) return null;
    const scheme = uri.slice(0, colon);
    let rest = uri.slice(colon + 1);
    const doubleSlash = rest.startsWith("//");
    if (doubleSlash) rest = rest.slice(2);

    const question = rest.indexOf("?");
    const path = question < 0 ? rest : rest.slice(0, question);
    const query = question < 0 ? "" : rest.slice(question + 1);

    const params: EndpointParam[] = [];
    if (query.length > 0) {
        for (const piece of query.split("&")) {
            const eq = piece.indexOf("=");
            params.push(eq < 0
                ? { name: piece, value: "" }
                : { name: piece.slice(0, eq), value: piece.slice(eq + 1) });
        }
    }
    return { scheme, path, doubleSlash, params };
}

export function buildEndpointUri(parsed: ParsedEndpointUri): string {
    const head = parsed.scheme + ":" + (parsed.doubleSlash ? "//" : "") + parsed.path;
    if (parsed.params.length === 0) return head;
    return head + "?" + parsed.params.map(p => `${p.name}=${p.value}`).join("&");
}

/**
 * Sets, replaces or removes (value === null) one query parameter — the name matched
 * case-insensitively (the options bind that way), the FILE's spelling and position kept for
 * an existing parameter, a new one appended with the given name.
 */
export function setUriOption(uri: string, name: string, value: string | null): string | null {
    const parsed = parseEndpointUri(uri);
    if (!parsed) return null;
    const index = parsed.params.findIndex(p => p.name.toLowerCase() === name.toLowerCase());

    if (value === null) {
        if (index < 0) return uri;
        parsed.params.splice(index, 1);
    } else if (index >= 0) {
        if (parsed.params[index].value === value) return uri;
        parsed.params[index] = { name: parsed.params[index].name, value };
    } else {
        parsed.params.push({ name, value });
    }
    return buildEndpointUri(parsed);
}

/** Replaces the path part, everything else verbatim. */
export function setUriPath(uri: string, newPath: string): string | null {
    const parsed = parseEndpointUri(uri);
    if (!parsed) return null;
    parsed.path = newPath;
    return buildEndpointUri(parsed);
}

/** camelCase of a catalog option name — the spelling for a NEWLY added parameter. */
export function parameterSpelling(optionName: string): string {
    return optionName.length === 0 ? optionName : optionName[0].toLowerCase() + optionName.slice(1);
}

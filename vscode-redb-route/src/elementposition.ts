/**
 * Decides whether the offset is a NEW-ELEMENT position — where inserting a `<element …>`
 * skeleton makes sense: element content, or a half-typed element name right after `<`.
 * Inside a start tag (attribute position), a comment or CDATA the answer is no — that is
 * exactly where declarative snippets used to inject garbage (live finding, 2026-09-04).
 */
export type ElementPosition =
    | { ok: true; /** offset of the `<` when the name is half-typed, else the offset itself. */ replaceFrom: number }
    | { ok: false };

export function elementPosition(text: string, offset: number): ElementPosition {
    const before = text.slice(0, offset);

    const commentStart = before.lastIndexOf("<!--");
    if (commentStart >= 0 && before.indexOf("-->", commentStart) < 0)
        return { ok: false };
    const cdataStart = before.lastIndexOf("<![CDATA[");
    if (cdataStart >= 0 && before.indexOf("]]>", cdataStart) < 0)
        return { ok: false };

    const lastOpen = before.lastIndexOf("<");
    const lastClose = before.lastIndexOf(">");
    if (lastOpen <= lastClose)
        return { ok: true, replaceFrom: offset }; // element content, nothing half-typed

    // After an unclosed `<`: a pure element-name fragment means the user is typing the
    // element — offer skeletons replacing from the `<`. Anything else (whitespace reached,
    // attributes, quotes) is the inside of a tag.
    const fragment = before.slice(lastOpen + 1);
    return /^[A-Za-z_][\w.-]*$|^$/.test(fragment)
        ? { ok: true, replaceFrom: lastOpen }
        : { ok: false };
}

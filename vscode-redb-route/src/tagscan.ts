/**
 * The end of a start tag read textually. A `>` inside a quoted attribute value does not close the
 * tag: the format writes conditions unescaped (`expr="header.amount>1000"`), and XML allows it.
 * A `<` cannot appear unescaped in a value, so the last `<` before an offset is still the tag the
 * offset may sit in; only its end needs the quotes.
 */

/**
 * The index of the `>` that closes the tag opened at `open`, searching below `limit`, or -1 when
 * the tag is still open there (an unclosed quote keeps it open).
 */
export function tagEnd(text: string, open: number, limit: number = text.length): number {
    let quote: string | null = null;
    for (let i = open + 1; i < limit; i++) {
        const c = text[i];
        if (quote) {
            if (c === quote) quote = null;
        } else if (c === '"' || c === "'") {
            quote = c;
        } else if (c === ">") {
            return i;
        }
    }
    return -1;
}

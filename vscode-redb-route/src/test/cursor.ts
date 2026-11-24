import * as assert from "node:assert/strict";

/**
 * A test text with one "|" marking the cursor: the text without that marker and the cursor offset. Exactly one marker
 * is allowed, so a "|" that belongs to the text under test (an expression's "||", a pipe in a value) cannot be taken
 * for the cursor by mistake.
 */
export function withCursor(textWithCursor: string): { text: string; offset: number } {
    const offset = textWithCursor.indexOf("|");
    assert.ok(offset >= 0, "the test text has no | cursor marker");
    assert.equal(textWithCursor.indexOf("|", offset + 1), -1, "the test text has more than one |; mark the cursor once");
    return { text: textWithCursor.slice(0, offset) + textWithCursor.slice(offset + 1), offset };
}

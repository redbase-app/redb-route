import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import { tagEnd } from "../tagscan";

describe("tagEnd — a '>' in a quoted value does not close the tag", () => {
    it("finds the closing '>' of a plain tag", () => {
        const text = `<filter expr="true">`;
        assert.equal(tagEnd(text, 0), text.length - 1);
    });

    it("skips a '>' inside double and single quotes", () => {
        const text = `<filter expr="header.amount>1000" other='a>b'>`;
        assert.equal(tagEnd(text, 0), text.length - 1);
    });

    it("keeps the tag open while a quote is open or the '>' lies beyond the limit", () => {
        assert.equal(tagEnd(`<filter expr="a>b`, 0), -1);
        const text = `<to uri="x"/>`;
        assert.equal(tagEnd(text, 0, 5), -1);
    });
});

import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import { elementPosition } from "../elementposition";

function at(textWithCursor: string) {
    const offset = textWithCursor.indexOf("|");
    return elementPosition(textWithCursor.replace("|", ""), offset);
}

describe("elementPosition — where skeletons are allowed", () => {
    it("allows element content", () => {
        assert.equal(at(`<route>\n  |\n</route>`).ok, true);
    });

    it("allows a half-typed element name and replaces from the <", () => {
        const result = at(`<route>\n  <cho|`);
        assert.deepEqual(result, { ok: true, replaceFrom: 10 });
    });

    it("allows a bare < just typed", () => {
        assert.equal(at(`<route>\n  <|`).ok, true);
    });

    it("REFUSES the attribute position — the live garbage-insert case", () => {
        assert.equal(at(`<throttle maxPerPeriod="10" |>`).ok, false);
    });

    it("refuses inside an attribute value", () => {
        assert.equal(at(`<to uri="direct://|"/>`).ok, false);
    });

    it("refuses inside a comment", () => {
        assert.equal(at(`<route>\n<!-- |\n-->`).ok, false);
    });

    it("refuses inside CDATA", () => {
        assert.equal(at(`<redbQuery><![CDATA[ a |< b ]]></redbQuery>`).ok, false);
    });

    it("allows content right after a closed tag", () => {
        assert.equal(at(`<log level="Error"/>|`).ok, true);
    });
});

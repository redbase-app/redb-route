import { strict as assert } from "node:assert";
import { describe, it } from "node:test";
import * as fs from "node:fs";
import * as path from "node:path";
import { buildIndex, ElementInfo } from "../graphmodel";
import { skeletonPlace } from "../skeletonscope";

/**
 * Which skeletons a position gets: the container level takes a `<route>`, a place that holds steps
 * takes the composite skeletons (`<choice>`, `<tryCatch>`), everything else takes none. The provider
 * used to offer the whole list at every element position, so `<choice>` was suggested directly under
 * `<routes>` — where the loader refuses it ("<choice> is not valid at the container level") and the
 * schema never offered it (owner finding 2026-10-08).
 *
 * The element list here is the generated one the extension ships, so the answers are pinned against
 * what the schema says, not against a copy.
 */
describe("the skeleton place", () => {
    const media = path.join(__dirname, "..", "..", "media");
    const index = buildIndex(JSON.parse(
        fs.readFileSync(path.join(media, "redb-route-elements.json"), "utf8")) as ElementInfo[]);

    /** The place at the `|` marker. */
    const placeOf = (document: string): string | null =>
        skeletonPlace(document, document.indexOf("|"), index);

    const document = (inner: string): string =>
        `<?xml version="1.0" encoding="utf-8"?>\n<routes xmlns="urn:redb:route:1.0">\n${inner}\n</routes>`;

    it("takes a route beside the other routes, not a step", () => {
        assert.equal(placeOf(document("  |")), "document-root");
    });

    it("takes steps inside a route", () => {
        assert.equal(placeOf(document(`  <route id="r">\n    |\n  </route>`)), "step-content");
    });

    it("takes steps inside an onException — a handler is a route of its own", () => {
        assert.equal(
            placeOf(document(`  <onException exceptions="System.Exception">\n    |\n  </onException>`)),
            "step-content");
    });

    it("takes steps inside a choice branch and a tryCatch handler", () => {
        assert.equal(
            placeOf(document(`  <route id="r">\n    <choice>\n      <when expr="true">\n        |\n      </when>\n    </choice>\n  </route>`)),
            "step-content");
        assert.equal(
            placeOf(document(`  <route id="r">\n    <tryCatch>\n      <catch exception="System.Exception">\n        |\n      </catch>\n    </tryCatch>\n  </route>`)),
            "step-content");
    });

    it("takes nothing in a handler's condition — the <when> of an onException holds no steps", () => {
        assert.equal(
            placeOf(document(`  <onException exceptions="System.Exception">\n    <when expr="true">\n      |\n    </when>\n  </onException>`)),
            null);
    });

    it("takes nothing inside a bean, a step or a foreign element", () => {
        assert.equal(placeOf(document(`  <bean id="b" type="System.Object">\n    |\n  </bean>`)), null);
        assert.equal(placeOf(document(`  <route id="r">\n    <log message="x">\n      |\n    </log>\n  </route>`)), null);
        assert.equal(placeOf(document(`  <route id="r">\n    <notAnElement>\n      |\n    </notAnElement>\n  </route>`)), null);
    });

    it("reads a half-typed document — the tree parser refuses it by design ('>' expected)", () => {
        const text = document(`  <route id="r">\n    <ch`);
        assert.equal(skeletonPlace(text, text.lastIndexOf("<ch"), index), "step-content");
        // The element being typed is not open yet, so the context is its parent — and no exception
        // reaches the completion provider.
        assert.doesNotThrow(() => skeletonPlace(text, text.lastIndexOf("<ch"), index));
    });

    it("takes nothing outside the document — an offset past the root", () => {
        const text = document("  ");
        assert.equal(skeletonPlace(text, text.length + 5, index), null);
    });
});

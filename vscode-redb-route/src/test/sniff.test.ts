import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import { sniff } from "../sniff";

describe("sniff — the activation contract", () => {
    it("recognizes a routes document", () => {
        const result = sniff(`<?xml version="1.0"?>\n<routes xmlns="urn:redb:route:1.0">\n</routes>`);
        assert.deepEqual(result, { kind: "routes", namespace: "urn:redb:route:1.0" });
    });

    it("recognizes a context document behind comments and whitespace", () => {
        const result = sniff(`<?xml version="1.0"?>\n<!-- package context -->\n\n<context xmlns="urn:redb:route:1.0"/>`);
        assert.equal(result?.kind, "context");
    });

    it("accepts a newer MINOR of the same major (§3.2)", () => {
        assert.equal(sniff(`<routes xmlns="urn:redb:route:1.7"/>`)?.kind, "routes");
    });

    it("rejects another MAJOR", () => {
        assert.equal(sniff(`<routes xmlns="urn:redb:route:2.0"/>`), null);
    });

    it("recognizes a prefixed root", () => {
        const result = sniff(`<r:routes xmlns:r="urn:redb:route:1.0"/>`);
        assert.equal(result?.kind, "routes");
    });

    it("REJECTS a foreign route.xml — the negative half of the contract", () => {
        assert.equal(sniff(`<routes xmlns="http://camel.apache.org/schema/spring"/>`), null);
        assert.equal(sniff(`<routes>\n  <route/>\n</routes>`), null, "no namespace at all");
        assert.equal(sniff(`<beans xmlns="urn:redb:route:1.0"/>`), null, "wrong root name");
    });

    it("rejects things that are not XML", () => {
        assert.equal(sniff(""), null);
        assert.equal(sniff("just text"), null);
        assert.equal(sniff("{\"routes\": []}"), null);
    });

    it("does not confuse a prefixed root with a default namespace", () => {
        assert.equal(sniff(`<r:routes xmlns="urn:redb:route:1.0"/>`), null,
            "the prefix r has no declaration — the default namespace is not its");
    });
});

import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import { buildPalette } from "../palette";

describe("buildPalette — Send to", () => {
    const sendTo = (schemes: Parameters<typeof buildPalette>[1]) =>
        buildPalette([], schemes).find(c => c.title === "Send to (endpoints)")?.items.map(i => i.label) ?? [];

    it("offers every component with a producer side and skips a consumer-only one", () => {
        assert.deepEqual(sendTo([
            { scheme: "smtp", role: "producer" },
            { scheme: "imap", role: "consumer" },
            { scheme: "kafka" },
            { scheme: "cron", role: "consumer" },
        ]), ["kafka", "smtp"]);
    });

    it("reads no role as both sides", () => {
        assert.deepEqual(sendTo([{ scheme: "sql" }, { scheme: "direct", pathSynonym: "name" }]), ["direct", "sql"]);
    });

    it("keeps the sender fragment and the path hint", () => {
        const item = buildPalette([], [{ scheme: "direct", pathSynonym: "name" }])
            .find(c => c.title === "Send to (endpoints)")?.items[0];
        assert.deepEqual(item, { label: "direct", fragment: `<to uri="direct://"/>`, hint: "path = name" });
    });
});

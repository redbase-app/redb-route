import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import { readZipEntries, zipEntries, ZipFormatError } from "../zipreader";
import { buildZip } from "./zipfixture";

describe("zipreader — entries of a .nupkg", () => {
    it("reads deflated and stored entries", () => {
        const archive = buildZip([
            { name: "a/deflated.json", content: '{"x":1}'.repeat(50) },
            { name: "b/stored.txt", content: "plain", stored: true },
        ]);
        const read = readZipEntries(archive, () => true);
        assert.equal(read.get("a/deflated.json")!.toString("utf8"), '{"x":1}'.repeat(50));
        assert.equal(read.get("b/stored.txt")!.toString("utf8"), "plain");
    });

    it("reads only what the predicate accepts and skips folder entries", () => {
        const archive = buildZip([
            { name: "templates/", content: "", stored: true },
            { name: "templates/x/.template.config/template.json", content: "{}" },
            { name: "templates/x/Program.cs", content: "class P {}" },
        ]);
        const read = readZipEntries(archive, name => name.endsWith(".json") || name.endsWith("/"));
        assert.deepEqual([...read.keys()], ["templates/x/.template.config/template.json"]);
    });

    it("finds the directory behind an archive comment", () => {
        const archive = buildZip([{ name: "f.txt", content: "ok" }], "a comment at the end");
        assert.equal(zipEntries(archive)[0].name, "f.txt");
    });

    it("refuses what is not a zip archive", () => {
        assert.throws(() => zipEntries(Buffer.from("not a zip archive at all, but long enough")), ZipFormatError);
        assert.throws(() => zipEntries(Buffer.from("short")), ZipFormatError);
    });

    it("refuses encrypted entries and unknown compression", () => {
        const encrypted = buildZip([{ name: "secret", content: "x", flags: 1 }]);
        assert.throws(() => readZipEntries(encrypted, () => true), /encrypted/);
        const bzip = buildZip([{ name: "packed", content: "x", stored: true, method: 12 }]);
        assert.throws(() => readZipEntries(bzip, () => true), /compression method 12/);
    });
});

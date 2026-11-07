import { strict as assert } from "node:assert";
import { describe, it } from "node:test";
import * as fs from "node:fs";
import * as path from "node:path";

/**
 * The schema the OASIS catalog binds must be the one that knows the PACKAGE elements
 * (<redb>, <redbSave>, <cache>, ...) and the typed endpoint options - not the bare registry
 * schema. A document only gets a schema through this binding, so a catalog pointing at the
 * lean file made every package element red in the editor (owner finding 2026-09-17).
 */
describe("the OASIS catalog binding", () => {
    const media = path.join(__dirname, "..", "..", "media");
    const catalog = fs.readFileSync(path.join(media, "catalog.xml"), "utf8");

    const bound = (): string => {
        const match = /<uri\s+name="urn:redb:route:1\.0"\s+uri="([^"]+)"/.exec(catalog);
        assert.ok(match, "catalog.xml must bind the urn:redb:route:1.0 namespace");
        return match![1];
    };

    it("binds a schema file that ships in the extension", () => {
        assert.ok(fs.existsSync(path.join(media, bound())), `${bound()} must exist in media/`);
    });

    it("binds the schema that knows the package contributions", () => {
        const schema = fs.readFileSync(path.join(media, bound()), "utf8");
        for (const element of ["redb", "redbSave", "redbQuery", "cache"])
            assert.ok(schema.includes(`<xs:element name="${element}">`),
                `the bound schema must declare <${element}>`);
        assert.ok(schema.includes('<xs:element ref="r:redb" />'),
            "<context> must accept the redb bridge block");
    });
});

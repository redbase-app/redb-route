import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import {
    catalogByScheme, CatalogComponent, catalogReferenceHover, catalogReferencePosition, expectedType,
} from "../catalog";
import { referencePosition } from "../beanref";
import * as fs from "node:fs";
import * as path from "node:path";

const COMPONENTS: CatalogComponent[] = [
    {
        scheme: "sql", alternateSchemes: [], options: [
            { name: "DataSource", type: "string", sensitive: false, reference: "System.Data.Common.DbDataSource, System.Data.Common" },
            { name: "OutputType", type: "enum", sensitive: false, enumValues: ["SelectList", "SelectOne"] },
        ],
    },
    {
        scheme: "ftp", alternateSchemes: ["ftps"], options: [
            { name: "ConnectionFactory", type: "string", sensitive: false, reference: "object" },
        ],
    },
    {
        scheme: "https", alternateSchemes: [], options: [
            { name: "HeaderFilterStrategy", type: "string", sensitive: false, reference: "redb.Route.IHeaderFilterStrategy, redb.Route" },
        ],
    },
];

const CATALOG = catalogByScheme(COMPONENTS);

function at(textWithCursor: string) {
    const offset = textWithCursor.indexOf("|");
    return catalogReferencePosition(textWithCursor.replace("|", ""), offset, CATALOG);
}

function hoverAt(textWithCursor: string) {
    const offset = textWithCursor.indexOf("|");
    return catalogReferenceHover(textWithCursor.replace("|", ""), offset, CATALOG);
}

describe("catalogReferencePosition — options the catalog marks as registry references", () => {
    it("takes an empty or bare value of the structured endpoint attribute, the name replaced from its start", () => {
        const text = `<route><to><sql dataSource="ma|"/></to>`;
        const where = at(text);
        assert.equal(where?.replaceFrom, text.indexOf("ma|"));
        assert.equal(where?.hashTyped, false);
        assert.equal(where?.option.name, "DataSource");
        assert.ok(at(`<route><to><sql dataSource="|"`));
    });

    it("takes a query value of an endpoint URI, after ? and after &amp; or &", () => {
        const text = `<route><to uri="sql:select 1?outputType=SelectOne&amp;dataSource=m|"/>`;
        assert.equal(at(text)?.replaceFrom, text.indexOf("m|"));
        assert.ok(at(`<route><to uri="sql:select 1?dataSource=|"`));
        assert.ok(at(`<route><to uri="sql:x?a=1&dataSource=|"`));
    });

    it("matches the option name case-insensitively and the scheme with its alternates and http", () => {
        assert.ok(at(`<route><to uri="sql:x?DATASOURCE=|"`));
        assert.ok(at(`<route><to uri="ftps://host/in?connectionFactory=|"`));
        assert.ok(at(`<route><from uri="http://0.0.0.0:8080/x?headerFilterStrategy=|"`));
        assert.ok(at(`<route><to><ftps connectionFactory="|"/></to>`));
    });

    it("reports a typed '#', so the completion does not insert a second one", () => {
        const text = `<route><to><sql dataSource="#ma|"/></to>`;
        assert.deepEqual(
            { replaceFrom: at(text)?.replaceFrom, hashTyped: at(text)?.hashTyped },
            { replaceFrom: text.indexOf("ma|"), hashTyped: true });
        assert.equal(at(`<route><to uri="sql:x?dataSource=#|"`)?.hashTyped, true);
    });

    it("refuses an unmarked option, an unknown scheme, a non-name value and a path position", () => {
        assert.equal(at(`<route><to uri="sql:x?outputType=|"`), null);
        assert.equal(at(`<route><to><sql outputType="|"/></to>`), null);
        assert.equal(at(`<route><to uri="nosuch:x?dataSource=|"`), null);
        assert.equal(at(`<route><to><sql dataSource="{{ds}}|"/></to>`), null);
        assert.equal(at(`<route><to uri="sql:dataSource=|"`), null);
        assert.equal(at(`<route><to><sql dataSource="x"/>|`), null);
    });

    it("leaves the '#' rule as it is: a '#' value of any attribute is still a reference position", () => {
        const text = `<route><to><sql outputType="#`;
        assert.equal(referencePosition(text, text.length).ok, true);
    });
});

describe("expectedType and catalogReferenceHover", () => {
    it("gives the expected type, and nothing for an untyped reference", () => {
        assert.equal(expectedType(COMPONENTS[0].options[0]), "System.Data.Common.DbDataSource, System.Data.Common");
        assert.equal(expectedType(COMPONENTS[1].options[0]), null);
        assert.equal(expectedType(COMPONENTS[0].options[1]), null);
    });

    it("describes a marked option on a structured attribute name and on a URI query key", () => {
        const structured = hoverAt(`<route><to><sql dataS|ource="#db"/></to>`);
        assert.ok(structured?.includes("**DataSource** names a registry object"));
        assert.ok(structured?.includes("Expects `System.Data.Common.DbDataSource, System.Data.Common`"));
        assert.ok(hoverAt(`<route><to uri="sql:x?a=1&amp;dataSo|urce=#db"/>`)?.includes("**DataSource**"));
        assert.equal(hoverAt(`<route><to uri="ftp://h?connection|Factory=#f"/>`)?.includes("Expects"), false);
    });

    it("is null on an unmarked option, a query value, the path and a non-endpoint attribute", () => {
        assert.equal(hoverAt(`<route><to uri="sql:x?output|Type=SelectOne"/>`), null);
        assert.equal(hoverAt(`<route><to uri="sql:x?dataSource=#d|b"/>`), null);
        assert.equal(hoverAt(`<route><to uri="sql:sel|ect 1?dataSource=#db"/>`), null);
        assert.equal(hoverAt(`<route><log mess|age="x"/>`), null);
    });
});

describe("the generated connector catalog", () => {
    it("marks the registry-reference options, sql dataSource with its expected type", () => {
        const media = path.join(__dirname, "..", "..", "media", "redb-route-catalog.json");
        const generated = catalogByScheme(JSON.parse(fs.readFileSync(media, "utf8")) as CatalogComponent[]);
        const text = `<route><to uri="sql:select 1?dataSource=`;
        assert.equal(catalogReferencePosition(text, text.length, generated)?.option.reference,
            "redb.Route.Sql.Connection.ISqlConnectionFactory, redb.Route.Sql");
        const plain = `<route><to uri="sql:select 1?outputType=`;
        assert.equal(catalogReferencePosition(plain, plain.length, generated), null);
    });
});

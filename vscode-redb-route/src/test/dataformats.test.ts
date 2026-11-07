import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { DataFormatInfo, formatCompletions, formatHover, startTagAt } from "../dataformats";

const FORMATS = JSON.parse(fs.readFileSync(
    path.join(__dirname, "..", "..", "media", "redb-route-dataformats.json"), "utf8")) as DataFormatInfo[];

const XMLJSON = "redb.Route.DataFormats.XmlJson.XmlJsonDataFormat, redb.Route.DataFormats.XmlJson";

function completeAt(textWithCursor: string) {
    const offset = textWithCursor.indexOf("|");
    return formatCompletions(textWithCursor.replace("|", ""), offset, FORMATS);
}

function hoverAt(textWithCursor: string) {
    const offset = textWithCursor.indexOf("|");
    return formatHover(textWithCursor.replace("|", ""), offset, FORMATS);
}

const labels = (textWithCursor: string) => completeAt(textWithCursor).map(c => c.label);

describe("the generated data format list", () => {
    it("carries xmljson with its options and unmarshal targets", () => {
        const xmljson = FORMATS.find(f => f.name === "xmljson");
        assert.ok(xmljson);
        assert.equal(xmljson.type, XMLJSON);
        assert.deepEqual(xmljson.attributes?.find(a => a.name === "namespaces")?.enumValues, ["Keep", "Drop"]);
        assert.ok(xmljson.unmarshalTargets?.includes("System.Xml.Linq.XDocument"));
    });
});

describe("startTagAt — the start tag around the offset", () => {
    it("reads an unfinished tag up to the next element", () => {
        const text = `<marshal type="A, a" sch\n<to uri="x"/>`;
        const tag = startTagAt(text, text.indexOf("sch") + 3);
        assert.equal(tag?.element, "marshal");
        assert.deepEqual(tag?.attributes.map(a => a.name), ["type", "sch"]);
    });

    it("is null after the tag, in a comment and in an end tag", () => {
        assert.equal(startTagAt(`<marshal format="x"/> `, 22), null);
        assert.equal(startTagAt(`<!-- <marshal `, 14), null);
        assert.equal(startTagAt(`</marshal `, 10), null);
    });
});

describe("formatCompletions — values", () => {
    it("offers the registered names in format= of marshal and unmarshal", () => {
        const names = labels(`<route><marshal format="|"/>`);
        assert.ok(names.includes("xmljson") && names.includes("csv"));
        assert.ok(labels(`<route><unmarshal format="x|" target="System.String"/>`).includes("xmljson"));
        assert.deepEqual(labels(`<context><dataFormat format="|"/>`), []);
    });

    it("offers the type names in type= of marshal, unmarshal and dataFormat", () => {
        const text = `<context><dataFormat name="j" type="redb.Ro|"/>`;
        const completions = completeAt(text);
        assert.ok(completions.some(c => c.label === XMLJSON));
        assert.equal(completions[0].replaceFrom, text.indexOf("redb.Ro|"));
        assert.ok(labels(`<route><marshal type="|"/>`).includes(XMLJSON));
    });

    it("offers the targets of the format named by type= or format=", () => {
        assert.ok(labels(`<route><unmarshal type="${XMLJSON}" target="|"/>`).includes("System.Xml.XmlDocument"));
        assert.ok(labels(`<route><unmarshal format="XmlJson" target="|"/>`)
            .includes("System.Text.Json.Nodes.JsonNode, System.Text.Json"));
        assert.deepEqual(labels(`<route><unmarshal target="|"/>`), []);
    });

    it("offers the enum values and true/false of an option once type= is set", () => {
        assert.deepEqual(labels(`<route><marshal type="${XMLJSON}" namespaces="|"/>`), ["Keep", "Drop"]);
        assert.deepEqual(labels(`<route><marshal type="${XMLJSON}" plural="|"/>`), ["true", "false"]);
        assert.deepEqual(labels(`<route><marshal type="${XMLJSON}" root="|"/>`), []);
        assert.deepEqual(labels(`<route><marshal namespaces="|"/>`), []);
    });
});

describe("formatCompletions — option names", () => {
    it("offers the options not yet written once type= names the format, the default in the detail", () => {
        const completions = completeAt(`<route><marshal type="${XMLJSON}" arrays="//Item" |/>`);
        const names = completions.map(c => c.label);
        assert.ok(names.includes("schema") && names.includes("namespaces"));
        assert.ok(!names.includes("arrays") && !names.includes("type"));
        assert.equal(completions.find(c => c.label === "textKey")?.detail, "String, default #text");
        assert.equal(completions.find(c => c.label === "namespaces")?.detail, "Keep | Drop, default Keep");
    });

    it("takes the type written after the cursor and a name being typed", () => {
        const text = `<route><marshal sch| type="${xmljsonSpaced()}"/>`;
        const completions = completeAt(text);
        assert.ok(completions.some(c => c.label === "schemaRoot"));
        assert.equal(completions[0].replaceFrom, text.indexOf("sch|"));
    });

    it("offers nothing without type=, with an unknown type, or glued to a value", () => {
        assert.deepEqual(labels(`<route><marshal format="xmljson" |/>`), []);
        assert.deepEqual(labels(`<route><marshal type="No.Such, x" |/>`), []);
        assert.deepEqual(labels(`<route><marshal type="${XMLJSON}"|/>`), []);
        assert.deepEqual(labels(`<route><to type="${XMLJSON}" |/>`), []);
    });
});

describe("formatHover", () => {
    it("describes an option name of the format", () => {
        const hover = hoverAt(`<route><marshal type="${XMLJSON}" name|spaces="Drop"/>`);
        assert.ok(hover?.startsWith("**namespaces** (Keep | Drop, default Keep)"));
    });

    it("describes the format of type= and format= values", () => {
        assert.ok(hoverAt(`<route><marshal type="redb.Route.Data|Formats.XmlJson.XmlJsonDataFormat, redb.Route.DataFormats.XmlJson"/>`)
            ?.includes("**xmljson**"));
        assert.ok(hoverAt(`<route><unmarshal format="xml|json" target="System.String"/>`)?.includes(XMLJSON));
    });

    it("is null elsewhere", () => {
        assert.equal(hoverAt(`<route><marshal ty|pe="${XMLJSON}"/>`), null);
        assert.equal(hoverAt(`<route><marshal plural="tr|ue"/>`), null);
        assert.equal(hoverAt(`<route><log message="na|me"/>`), null);
    });
});

/** The type name with other spacing and case, as a hand-written document may carry it. */
function xmljsonSpaced(): string {
    return "redb.Route.DataFormats.XmlJson.XmlJsonDataFormat,redb.route.dataformats.xmljson";
}

describe("formatCompletions — Path options over the module's resources/", () => {
    const TREE = {
        files: ["map.xslt", "xsd/common/types.xsd", "xsd/order.xsd"],
        dirs: ["", "xsd", "xsd/common"],
    };
    const pathsAt = (textWithCursor: string, tree: typeof TREE | null = TREE) => {
        const offset = textWithCursor.indexOf("|");
        return formatCompletions(textWithCursor.replace("|", ""), offset, FORMATS, () => tree);
    };

    it("offers the files of resources/ for schema=, in the short form, replacing the value typed", () => {
        const text = `<route><marshal type="${XMLJSON}" schema="xsd/o|"/>`;
        const completions = pathsAt(text);
        assert.deepEqual(completions.map(c => c.label), TREE.files);
        assert.equal(completions[0].replaceFrom, text.indexOf("xsd/o|"));
        assert.equal(completions.find(c => c.label === "xsd/order.xsd")?.detail, "resources/xsd/order.xsd");
    });

    it("offers the folders relative to the schema's directory for schemaRoot=", () => {
        assert.deepEqual(pathsAt(`<route><marshal type="${XMLJSON}" schema="xsd/order.xsd" schemaRoot="|"/>`)
            .map(c => c.label), ["..", "common"]);
        assert.deepEqual(pathsAt(`<route><marshal type="${XMLJSON}" schemaRoot="|" schema="resources/xsd/common/types.xsd"/>`)
            .map(c => c.label), ["../..", ".."]);
    });

    it("offers no folders while the schema is missing, outside resources/ or a placeholder", () => {
        assert.deepEqual(pathsAt(`<route><marshal type="${XMLJSON}" schemaRoot="|"/>`), []);
        assert.deepEqual(pathsAt(`<route><marshal type="${XMLJSON}" schema="loose.xsd" schemaRoot="|"/>`), []);
        assert.deepEqual(pathsAt(`<route><marshal type="${XMLJSON}" schema="{{orders.xsd}}" schemaRoot="|"/>`), []);
    });

    it("offers nothing without a resources folder or without type=", () => {
        assert.deepEqual(pathsAt(`<route><marshal type="${XMLJSON}" schema="|"/>`, null), []);
        assert.deepEqual(pathsAt(`<route><marshal schema="|"/>`), []);
    });
});

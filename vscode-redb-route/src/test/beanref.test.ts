import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import { beanNames, referencePosition } from "../beanref";

function at(textWithCursor: string) {
    const offset = textWithCursor.indexOf("|");
    return referencePosition(textWithCursor.replace("|", ""), offset);
}

describe("referencePosition — where `#` offers bean names", () => {
    it("takes a URI option value", () => {
        const text = `<from uri="rabbitmq:orders?headerFilterStrategy=#|"/>`;
        assert.deepEqual(at(text), { ok: true, replaceFrom: text.indexOf("|") });
    });

    it("takes a later option and replaces the half-typed name", () => {
        const text = `<to uri="kafka:t?brokers=b&headerFilterStrategy=#fil|"/>`;
        assert.deepEqual(at(text), { ok: true, replaceFrom: text.indexOf("fil|") });
    });

    it("takes the whole value of a structured option", () => {
        assert.equal(at(`<sql dataSource="#|">`).ok, true);
    });

    it("takes the path right after the scheme", () => {
        assert.equal(at(`<to uri="bean:#|"/>`).ok, true);
        assert.equal(at(`<to uri='bean://#my|'/>`).ok, true);
    });

    it("refuses a `#` inside text of a value — the sql parameter", () => {
        assert.equal(at(`<to uri="sql:select * from t where id = :#|"/>`).ok, false);
    });

    it("refuses a `#` in the middle of an option value", () => {
        assert.equal(at(`<to uri="http://h/p?q=a#|"/>`).ok, false);
    });

    it("refuses a `#` after a path that is not the scheme's", () => {
        assert.equal(at(`<to uri="direct:a#|"/>`).ok, false);
    });

    it("refuses element content, a closed value and comments", () => {
        assert.equal(at(`<log>#|</log>`).ok, false);
        assert.equal(at(`<to uri="bean:x" #|`).ok, false);
        assert.equal(at(`<!-- <to uri="bean:#| -->`).ok, false);
    });

    it("refuses a value after the tag was closed", () => {
        assert.equal(at(`<to uri="bean:x"/> "#|`).ok, false);
    });
});

describe("beanNames — the beans a `#` may name", () => {
    it("lists every named bean once, in document order", () => {
        const text = `<routes>
  <bean name="filter" type="A, a"/>
  <bean type="B, b" name='db'><property key="x" value="1"/></bean>
  <bean name="filter" type="C, c"/>
  <route><to><bean path="#filter"/></to></route>
</routes>`;
        assert.deepEqual(beanNames(text), ["filter", "db"]);
    });

    it("skips commented-out beans and elements that only start with bean", () => {
        const text = `<!-- <bean name="old" type="A, a"/> --><beanish name="no"/><bean name="yes" type="A, a"/>`;
        assert.deepEqual(beanNames(text), ["yes"]);
    });
});

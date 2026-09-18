import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import { scanRoutes } from "../routescan";

const SAMPLE = `<?xml version="1.0"?>
<routes xmlns="urn:redb:route:1.0">
  <!-- <route id="commented-out"> -->
  <route id="orders-intake" description="Kafka to redb">
    <from uri="kafka://orders?groupId=svc"/>
    <to uri="log://x"/>
  </route>
  <route>
    <from uri="direct://anonymous"/>
  </route>
  <route id="multi"
         description="attributes on
          their own lines">
    <redbQuery type="T"><![CDATA[ a < b AND contains(x, '<route id="fake">') ]]></redbQuery>
  </route>
</routes>
`;

describe("scanRoutes — the sidebar tree", () => {
    it("finds every route with id, description, from and the LINE", () => {
        const routes = scanRoutes(SAMPLE);

        assert.equal(routes.length, 3);
        assert.deepEqual(routes[0], {
            id: "orders-intake",
            description: "Kafka to redb",
            from: "kafka://orders?groupId=svc",
            line: 3,
        });
        assert.equal(routes[1].id, null, "a route without id still shows");
        assert.equal(routes[1].from, "direct://anonymous");
        assert.equal(routes[1].line, 7);
    });

    it("skips commented-out and CDATA-buried route tags", () => {
        const ids = scanRoutes(SAMPLE).map(r => r.id);
        assert.ok(!ids.includes("commented-out"));
        assert.ok(!ids.includes("fake"));
    });

    it("reads attributes split across lines", () => {
        const multi = scanRoutes(SAMPLE)[2];
        assert.equal(multi.id, "multi");
        assert.equal(multi.line, 10);
    });

    it("returns nothing for a document without routes", () => {
        assert.deepEqual(scanRoutes(`<routes xmlns="urn:redb:route:1.0"/>`), []);
    });
});

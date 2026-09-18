import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import { parseEndpointUri, buildEndpointUri, setUriOption, setUriPath, parameterSpelling } from "../endpointuri";

describe("endpoint-uri surgery — raw, order-preserving", () => {
    it("parse/build is the identity on untouched uris", () => {
        for (const uri of [
            "kafka://orders?groupId=svc&brokers={{kafka.brokers}}",
            "direct://in",
            "bean:#stamps?method=go",
            "https://api.example.com/v1/orders?method=POST",
            "cron://close?pattern=0 3 * * *",
        ])
            assert.equal(buildEndpointUri(parseEndpointUri(uri)!), uri);
    });

    it("replaces one parameter in place, order kept, spelling kept", () => {
        assert.equal(
            setUriOption("kafka://t?groupId=a&brokers=b", "GroupId", "svc-2"),
            "kafka://t?groupId=svc-2&brokers=b",
            "the catalog name GroupId matches the file's groupId case-insensitively");
    });

    it("appends a new parameter at the end in camelCase", () => {
        assert.equal(parameterSpelling("MaxPollIntervalMs"), "maxPollIntervalMs");
        assert.equal(
            setUriOption("kafka://t?groupId=a", parameterSpelling("Acks"), "All"),
            "kafka://t?groupId=a&acks=All");
    });

    it("removes a parameter cleanly", () => {
        assert.equal(
            setUriOption("kafka://t?groupId=a&acks=All&brokers=b", "acks", null),
            "kafka://t?groupId=a&brokers=b");
        assert.equal(
            setUriOption("kafka://t?groupId=a", "acks", null),
            "kafka://t?groupId=a", "removing the absent is a no-op");
    });

    it("placeholders stay opaque bytes", () => {
        assert.equal(
            setUriOption("amqp://{{demo.amqp.consumer}}?durable=true", "durable", "false"),
            "amqp://{{demo.amqp.consumer}}?durable=false");
    });

    it("path replacement keeps the query and the slash style", () => {
        assert.equal(setUriPath("kafka://old?groupId=a", "new-topic"), "kafka://new-topic?groupId=a");
        assert.equal(setUriPath("bean:#old?method=go", "#new"), "bean:#new?method=go");
    });
});

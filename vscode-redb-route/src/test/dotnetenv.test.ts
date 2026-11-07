import { describe, it } from "node:test";
import * as assert from "node:assert/strict";
import {
    compareVersions, latestStableVersion, parseSdkList, templateInstallArguments, templateState,
} from "../dotnetenv";
import { InstalledTemplatePackage } from "../templates";

const installed = (version: string | null): InstalledTemplatePackage =>
    ({ packageId: "redb.Templates", version, mountPoint: "x.nupkg" });

describe("dotnetenv — the SDKs", () => {
    it("reads dotnet --list-sdks on Windows and Unix", () => {
        const output = "8.0.404 [C:\\Program Files\\dotnet\\sdk]\r\n10.0.100-rc.2.25502.107 [C:\\Program Files\\dotnet\\sdk]\r\n"
            + "9.0.101 [/usr/share/dotnet/sdk]\n\n";
        assert.deepEqual(parseSdkList(output), [
            { version: "8.0.404", major: 8 },
            { version: "10.0.100-rc.2.25502.107", major: 10 },
            { version: "9.0.101", major: 9 },
        ]);
    });

    it("reads no SDKs from an empty or foreign output", () => {
        assert.deepEqual(parseSdkList(""), []);
        assert.deepEqual(parseSdkList("No .NET SDKs were found."), []);
    });
});

describe("dotnetenv — versions", () => {
    it("orders numeric parts numerically", () => {
        assert.equal(compareVersions("4.4.10", "4.4.9"), 1);
        assert.equal(compareVersions("4.4", "4.4.0"), 0);
        assert.equal(compareVersions("4.3.9", "4.4.0"), -1);
    });

    it("puts a release above its prereleases and orders prereleases by parts", () => {
        assert.equal(compareVersions("4.4.0", "4.4.0-beta.4"), 1);
        assert.equal(compareVersions("4.4.0-beta.10", "4.4.0-beta.4"), 1);
        assert.equal(compareVersions("4.4.0-alpha", "4.4.0-beta"), -1);
        assert.equal(compareVersions("4.4.0-beta", "4.4.0-beta.1"), -1);
        assert.equal(compareVersions("4.4.0+abc", "4.4.0"), 0);
    });

    it("takes the latest stable version from the NuGet index", () => {
        const index = JSON.stringify({ versions: ["4.3.0", "4.4.0-beta.4", "4.4.2", "4.4.10", "4.5.0-rc.1"] });
        assert.equal(latestStableVersion(index), "4.4.10");
        assert.equal(latestStableVersion(JSON.stringify({ versions: ["1.0.0-beta"] })), null);
        assert.equal(latestStableVersion("{}"), null);
    });
});

describe("dotnetenv — where the templates stand", () => {
    it("reports missing templates with the version to install", () => {
        assert.deepEqual(templateState(null, "4.4.3"), { kind: "missing", latest: "4.4.3" });
    });

    it("reports an older installed package as outdated", () => {
        const state = templateState(installed("4.4.2"), "4.4.3");
        assert.equal(state.kind, "outdated");
    });

    it("counts the latest, a newer, a folder from disk and an offline check as current", () => {
        assert.equal(templateState(installed("4.4.3"), "4.4.3").kind, "current");
        assert.equal(templateState(installed("4.5.0-beta.1"), "4.4.3").kind, "current");
        assert.equal(templateState(installed(null), "4.4.3").kind, "current");
        assert.equal(templateState(installed("4.4.2"), null).kind, "current");
    });

    it("installs a pinned version when one is known", () => {
        assert.deepEqual(templateInstallArguments("redb.Templates", "4.4.3"), ["new", "install", "redb.Templates::4.4.3"]);
        assert.deepEqual(templateInstallArguments("redb.Tsak.Templates", null), ["new", "install", "redb.Tsak.Templates"]);
    });
});

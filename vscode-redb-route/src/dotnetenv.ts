/**
 * What the New Project page needs on the machine: a .NET SDK and the redb template packages
 * installed into the `dotnet new` engine, preferably the latest stable ones from nuget.org. The
 * parsing and comparing is pure and tested; the process and HTTP calls at the bottom are thin.
 */

import { execFile } from "child_process";
import * as fs from "fs";
import * as os from "os";
import { findTemplatePackage, InstalledTemplatePackage, parseTemplateRegistry, templateRegistryPath } from "./templates";

export interface InstalledSdk {
    version: string;
    major: number;
}

/** Reads `dotnet --list-sdks`: one "10.0.100 [C:\Program Files\dotnet\sdk]" per line. */
export function parseSdkList(output: string): InstalledSdk[] {
    const sdks: InstalledSdk[] = [];
    for (const line of output.split(/\r?\n/)) {
        const match = /^\s*((\d+)\.\d+\.\d+\S*)\s+\[/.exec(line);
        if (match)
            sdks.push({ version: match[1], major: Number(match[2]) });
    }
    return sdks;
}

/**
 * Orders NuGet versions: numeric parts first, then a release above any of its prereleases,
 * prerelease labels compared part by part (numbers numerically). Build metadata is ignored.
 */
export function compareVersions(a: string, b: string): number {
    const [coreA, preA] = splitVersion(a);
    const [coreB, preB] = splitVersion(b);
    const length = Math.max(coreA.length, coreB.length);
    for (let i = 0; i < length; i++) {
        const difference = (coreA[i] ?? 0) - (coreB[i] ?? 0);
        if (difference !== 0) return Math.sign(difference);
    }
    if (preA === null || preB === null)
        return preA === preB ? 0 : preA === null ? 1 : -1;
    const partsA = preA.split(".");
    const partsB = preB.split(".");
    for (let i = 0; i < Math.max(partsA.length, partsB.length); i++) {
        const x = partsA[i];
        const y = partsB[i];
        if (x === undefined || y === undefined) return x === undefined ? -1 : 1;
        const numeric = /^\d+$/.test(x) && /^\d+$/.test(y);
        const difference = numeric ? Number(x) - Number(y) : x.toLowerCase().localeCompare(y.toLowerCase());
        if (difference !== 0) return Math.sign(difference);
    }
    return 0;
}

function splitVersion(version: string): [number[], string | null] {
    const withoutMetadata = version.split("+")[0];
    const dash = withoutMetadata.indexOf("-");
    const core = dash < 0 ? withoutMetadata : withoutMetadata.slice(0, dash);
    return [core.split(".").map(part => Number(part) || 0), dash < 0 ? null : withoutMetadata.slice(dash + 1)];
}

/** The highest stable version in a NuGet flat-container index (`{"versions":[...]}`). */
export function latestStableVersion(indexJson: string): string | null {
    const versions = (JSON.parse(indexJson) as { versions?: unknown }).versions;
    if (!Array.isArray(versions))
        return null;
    const stable = versions.filter((v): v is string => typeof v === "string" && !v.includes("-"));
    return stable.sort(compareVersions).at(-1) ?? null;
}

export type TemplateState =
    | { kind: "missing"; latest: string | null }
    | { kind: "outdated"; installed: InstalledTemplatePackage; latest: string }
    | { kind: "current"; installed: InstalledTemplatePackage };

/**
 * Where the installed templates stand against nuget.org. A folder installed from disk has no
 * version and counts as current: it is someone developing the templates. Without an answer from
 * nuget.org (offline) the installed package counts as current too.
 */
export function templateState(installed: InstalledTemplatePackage | null, latest: string | null): TemplateState {
    if (!installed)
        return { kind: "missing", latest };
    if (latest && installed.version && compareVersions(installed.version, latest) < 0)
        return { kind: "outdated", installed, latest };
    return { kind: "current", installed };
}

/** The `dotnet new install` arguments for a template package, pinned to a version when one is known. */
export function templateInstallArguments(packageId: string, version: string | null): string[] {
    return ["new", "install", version ? `${packageId}::${version}` : packageId];
}

/** The NuGet flat-container index of a package, listing its versions. */
export function nugetIndexUrl(packageId: string): string {
    return `https://api.nuget.org/v3-flatcontainer/${packageId.toLowerCase()}/index.json`;
}

// ── the machine ──────────────────────────────────────────────────────

/** The installed SDKs, or null when `dotnet` is not on the PATH. */
export function installedSdks(): Promise<InstalledSdk[] | null> {
    return new Promise(resolve => {
        execFile("dotnet", ["--list-sdks"], { windowsHide: true, timeout: 30_000 }, (error, stdout) => {
            if (error && (error as NodeJS.ErrnoException).code === "ENOENT")
                resolve(null);
            else
                resolve(parseSdkList(stdout ?? ""));
        });
    });
}

/** An installed template package from the engine's registry, or null when it is not installed. */
export function installedTemplatePackage(packageId: string): InstalledTemplatePackage | null {
    const registry = templateRegistryPath(process.env, os.homedir());
    if (!fs.existsSync(registry))
        return null;
    return findTemplatePackage(parseTemplateRegistry(fs.readFileSync(registry, "utf8")), packageId);
}

/** The latest stable version of a package on nuget.org, or null when it cannot be reached. */
export async function latestTemplateVersion(packageId: string): Promise<string | null> {
    try {
        const response = await fetch(nugetIndexUrl(packageId), { signal: AbortSignal.timeout(10_000) });
        return response.ok ? latestStableVersion(await response.text()) : null;
    } catch {
        return null;
    }
}

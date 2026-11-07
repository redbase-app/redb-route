/**
 * The redb project templates as the `dotnet new` template engine has them installed. The list
 * is not scraped from `dotnet new list` or `--help` tables: their layout is not a contract. It is
 * read from the engine's own registry (`.templateengine/packages.json`) and from the
 * `.template.config/template.json` files inside the installed packages, so the names,
 * descriptions and parameters are exactly what each template declares.
 *
 * Every project and solution template of the redb template packages is offered. The redb.Route
 * XML modules (agreed with redb.Templates: `classifications` holds "RouteXml") form their own
 * group and the basic `redb-route` module is the one preselected.
 */

import * as fs from "fs";
import * as path from "path";
import { readZipEntries } from "./zipreader";

/** The packages whose templates the New Project page offers, in the order it shows them. */
export const TEMPLATE_PACKAGE_IDS = ["redb.Templates", "redb.Tsak.Templates"] as const;
export type TemplatePackageId = typeof TEMPLATE_PACKAGE_IDS[number];
/** The template the New Project page preselects. */
export const DEFAULT_TEMPLATE = "redb-route";
const ROUTE_CLASSIFICATION = "RouteXml";
const TSAK_CLASSIFICATION = "redb.Tsak";

/** One package the template engine has installed. */
export interface InstalledTemplatePackage {
    packageId: string;
    /** Absent for a template folder installed from disk. */
    version: string | null;
    /** The installed .nupkg, or the folder installed from disk. */
    mountPoint: string;
}

/** Where the template engine keeps its registry: DOTNET_CLI_HOME when set, else the user's home. */
export function templateRegistryPath(env: NodeJS.ProcessEnv, homeDirectory: string): string {
    const home = env.DOTNET_CLI_HOME && env.DOTNET_CLI_HOME.trim() ? env.DOTNET_CLI_HOME : homeDirectory;
    return path.join(home, ".templateengine", "packages.json");
}

/** Reads `packages.json`. An entry without a package id or a mount point is skipped. */
export function parseTemplateRegistry(text: string): InstalledTemplatePackage[] {
    const root = JSON.parse(stripBom(text)) as { Packages?: unknown };
    if (!Array.isArray(root.Packages))
        return [];
    const result: InstalledTemplatePackage[] = [];
    for (const item of root.Packages as Record<string, unknown>[]) {
        const details = (item?.Details ?? {}) as Record<string, unknown>;
        const packageId = typeof details.PackageId === "string" ? details.PackageId : null;
        const mountPoint = typeof item?.MountPointUri === "string" ? item.MountPointUri : null;
        if (!packageId || !mountPoint)
            continue;
        result.push({
            packageId,
            version: typeof details.Version === "string" ? details.Version : null,
            mountPoint,
        });
    }
    return result;
}

/** An installed package by id, matched case-insensitively as NuGet ids are. */
export function findTemplatePackage(packages: InstalledTemplatePackage[], packageId: string): InstalledTemplatePackage | null {
    return packages.find(p => p.packageId.toLowerCase() === packageId.toLowerCase()) ?? null;
}

/** True for a path inside a package or a folder that is a template definition. */
export function isTemplateDefinition(entryPath: string): boolean {
    return /(^|\/)\.template\.config\/template\.json$/i.test(entryPath.replace(/\\/g, "/"));
}

/**
 * The template.json texts of an installed package: the entries of its .nupkg, or the files of a
 * template folder installed from disk (`dotnet new install <folder>`, used while developing
 * templates).
 */
export function readTemplateDefinitions(mountPoint: string): string[] {
    if (fs.statSync(mountPoint).isDirectory()) {
        const found: string[] = [];
        const walk = (dir: string): void => {
            for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
                const full = path.join(dir, entry.name);
                if (entry.isDirectory()) {
                    if (!["bin", "obj", "node_modules", ".git"].includes(entry.name)) walk(full);
                } else if (isTemplateDefinition(path.relative(mountPoint, full))) {
                    found.push(fs.readFileSync(full, "utf8"));
                }
            }
        };
        walk(mountPoint);
        return found;
    }
    return [...readZipEntries(fs.readFileSync(mountPoint), isTemplateDefinition).values()]
        .map(bytes => bytes.toString("utf8"));
}

export type ParameterKind ="choice" | "bool" | "text";

export interface TemplateChoice {
    value: string;
    description: string | null;
}

/** One parameter a template declares (`symbols` of type "parameter"). */
export interface TemplateParameter {
    /** The symbol name, passed as `--<name>`. */
    name: string;
    displayName: string | null;
    description: string | null;
    kind: ParameterKind;
    /** The template's own datatype, kept for text parameters (int, float, ...). */
    datatype: string;
    defaultValue: string | null;
    isRequired: boolean;
    choices: TemplateChoice[];
}

/** The groups of the New Project page, in the order it shows them. */
export type TemplateGroup = "route-xml" | "tsak" | "apps";
export const TEMPLATE_GROUPS: { group: TemplateGroup; title: string }[] = [
    { group: "route-xml", title: "redb.Route XML modules" },
    { group: "tsak", title: "redb.Tsak hosts" },
    { group: "apps", title: "Applications" },
];

export interface RedbTemplate {
    shortName: string;
    name: string;
    description: string | null;
    identity: string | null;
    /** "project" or "solution", as the template declares it. */
    type: string;
    group: TemplateGroup;
    classifications: string[];
    /** The package the template came from. */
    packageId: string;
    parameters: TemplateParameter[];
}

/**
 * The project and solution templates among one package's template.json texts. A definition that
 * is not JSON, an item template or one without a short name is skipped.
 */
export function selectTemplates(definitions: string[], packageId: string): RedbTemplate[] {
    const templates: RedbTemplate[] = [];
    for (const text of definitions) {
        let json: Record<string, unknown>;
        try {
            json = JSON.parse(stripJsonComments(stripBom(text))) as Record<string, unknown>;
        } catch {
            continue;
        }
        const template = toTemplate(json, packageId);
        if (template)
            templates.push(template);
    }
    return templates;
}

/** Templates of every package in page order: by group, the default first, then by name. */
export function orderTemplates(templates: RedbTemplate[]): RedbTemplate[] {
    const groupIndex = (t: RedbTemplate) => TEMPLATE_GROUPS.findIndex(g => g.group === t.group);
    return [...templates].sort((a, b) =>
        groupIndex(a) - groupIndex(b)
        || (a.shortName === DEFAULT_TEMPLATE ? -1 : b.shortName === DEFAULT_TEMPLATE ? 1 : 0)
        || a.name.localeCompare(b.name));
}

function toTemplate(json: Record<string, unknown>, packageId: string): RedbTemplate | null {
    const shortName = firstShortName(json.shortName);
    const tags = (json.tags ?? {}) as Record<string, unknown>;
    if (!shortName || (tags.type !== "project" && tags.type !== "solution"))
        return null;
    const classifications = (Array.isArray(json.classifications) ? json.classifications : [])
        .filter((c): c is string => typeof c === "string");
    return {
        shortName,
        name: typeof json.name === "string" && json.name ? json.name : shortName,
        description: typeof json.description === "string" && json.description ? json.description : null,
        identity: typeof json.identity === "string" ? json.identity : null,
        type: tags.type,
        group: classifications.includes(ROUTE_CLASSIFICATION) ? "route-xml"
            : classifications.includes(TSAK_CLASSIFICATION) ? "tsak"
            : "apps",
        classifications,
        packageId,
        parameters: parameters(json.symbols),
    };
}

/** The short name is a string or a list of aliases; `dotnet new` takes the first. */
function firstShortName(value: unknown): string | null {
    const names = typeof value === "string" ? [value] : Array.isArray(value) ? value : [];
    const first = names[0];
    return typeof first === "string" && first ? first : null;
}

function parameters(symbols: unknown): TemplateParameter[] {
    if (!symbols || typeof symbols !== "object")
        return [];
    const result: TemplateParameter[] = [];
    for (const [name, raw] of Object.entries(symbols as Record<string, Record<string, unknown>>)) {
        if (!raw || raw.type !== "parameter")
            continue;
        const datatype = typeof raw.datatype === "string" ? raw.datatype.toLowerCase() : "text";
        const kind: ParameterKind = datatype === "choice" ? "choice"
            : datatype === "bool" || datatype === "boolean" ? "bool"
            : "text";
        result.push({
            name,
            displayName: text(raw.displayName),
            description: text(raw.description),
            kind,
            datatype,
            defaultValue: raw.defaultValue === undefined || raw.defaultValue === null ? null : String(raw.defaultValue),
            isRequired: raw.isRequired === true || raw.isRequired === "true",
            choices: kind === "choice" && Array.isArray(raw.choices)
                ? (raw.choices as Record<string, unknown>[])
                    .filter(c => typeof c?.choice === "string")
                    .map(c => ({ value: c.choice as string, description: text(c.description) }))
                : [],
        });
    }
    return result;
}

function text(value: unknown): string | null {
    return typeof value === "string" && value ? value : null;
}

function stripBom(text: string): string {
    return text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
}

/** Removes // and block comments outside strings: the template engine reads template.json leniently. */
export function stripJsonComments(text: string): string {
    let result = "";
    let inString = false;
    for (let i = 0; i < text.length; i++) {
        const c = text[i];
        if (inString) {
            result += c;
            if (c === "\\" && i + 1 < text.length) result += text[++i];
            else if (c === '"') inString = false;
        } else if (c === '"') {
            inString = true;
            result += c;
        } else if (c === "/" && text[i + 1] === "/") {
            while (i < text.length && text[i] !== "\n") i++;
            result += "\n";
        } else if (c === "/" && text[i + 1] === "*") {
            const end = text.indexOf("*/", i + 2);
            i = end < 0 ? text.length : end + 1;
        } else {
            result += c;
        }
    }
    return result;
}

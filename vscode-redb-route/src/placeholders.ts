/**
 * {{key}} / {{key:default}} resolution for the VIEW (§8: the placeholder is shown together
 * with its resolved value; what gets EDITED is always the placeholder — Р18). The resolver
 * only substitutes for display and scheme detection; it never writes.
 */

/** Placeholder values by key. Keys are stored lower-cased: the runtime's layer is case-insensitive. */
export type ConfigMap = Map<string, string>;

const PLACEHOLDER = /\{\{([^{}:]+)(?::([^{}]*))?\}\}/g;

export interface ResolvedUri {
    /** The text with every placeholder substituted (value, else default, else kept as-is). */
    resolved: string;
    /** True when at least one substitution happened — the view came from config. */
    fromConfig: boolean;
    /** Keys that had neither a value nor a default. */
    missing: string[];
}

export function resolvePlaceholders(text: string, config: ConfigMap): ResolvedUri {
    let fromConfig = false;
    const missing: string[] = [];
    let resolved = text;
    // A config value may itself hold a placeholder ({{db.main.connection}} inside a sql
    // uri) — iterate to a fixed point, bounded against cycles.
    for (let pass = 0; pass < 3; pass++) {
        missing.length = 0;
        const next = resolved.replace(PLACEHOLDER, (whole, key: string, fallback: string | undefined) => {
            // As PropertyPlaceholderResolver: an empty value counts as none and the default applies.
            const configured = config.get(key.trim().toLowerCase());
            const value = configured ? configured : fallback;
            if (value === undefined) {
                missing.push(key.trim());
                return whole;
            }
            fromConfig = true;
            return value;
        });
        if (next === resolved) break;
        resolved = next;
    }
    return { resolved, fromConfig, missing };
}

/**
 * Merges one of a module's config files (config/context.sample.json, deployed as context.json,
 * and config/<package>.config.json, which the packager puts into the package) the way the Tsak
 * runtime builds a context's layer from each (TsakCoordinator.ExtractLayerForContext):
 * with a "Contexts" section, the entries of Contexts.default and then of Contexts.<contextName>
 * over them; without one, the entries at the root. Every top-level key of that section becomes
 * one context property, taken as written: {"hello.period": 5000} gives hello.period, while
 * {"hello": {"period": 5000}} gives nothing, because the runtime keeps a nested object as one
 * property holding a dictionary and a placeholder never reads into it. A key whose value is an
 * object, an array or null hides what an earlier layer set for it.
 */
export function mergeContextLayer(json: unknown, contextName: string | null, into: ConfigMap): void {
    const root = asObject(json);
    if (!root) return;
    const contexts = entry(root, "Contexts");
    if (contexts === undefined) {
        mergeEntries(root, into);
        return;
    }
    const sections = asObject(contexts);
    if (!sections) return;
    const shared = asObject(entry(sections, "default"));
    if (shared) mergeEntries(shared, into);
    const own = contextName ? asObject(entry(sections, contextName)) : null;
    if (own) mergeEntries(own, into);
}

function mergeEntries(section: Record<string, unknown>, into: ConfigMap): void {
    for (const [key, value] of Object.entries(section)) {
        const scalar = scalarText(value);
        if (scalar === null) into.delete(key.toLowerCase());
        else into.set(key.toLowerCase(), scalar);
    }
}

/** A JSON scalar as Microsoft.Extensions.Configuration reads it (true becomes "True"); null for anything else. */
function scalarText(value: unknown): string | null {
    if (typeof value === "string") return value;
    if (typeof value === "number") return String(value);
    if (typeof value === "boolean") return value ? "True" : "False";
    return null;
}

/** A property looked up case-insensitively, as configuration sections are. */
function entry(object: Record<string, unknown>, name: string): unknown {
    const key = Object.keys(object).find(k => k.toLowerCase() === name.toLowerCase());
    return key === undefined ? undefined : object[key];
}

function asObject(value: unknown): Record<string, unknown> | null {
    return value !== null && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : null;
}

/** The package name `redb-route-xml pack` uses: <RoutePackageName>, else the csproj's file name. */
export function packageName(csprojFileName: string, csprojText: string): string {
    return msbuildProperty(csprojText, "RoutePackageName") ?? csprojFileName.replace(/\.csproj$/i, "");
}

/**
 * The context a module runs in when the host's appsettings do not assign it: "ContextName" of
 * config/<package>.config.json, which the packager puts into the package; without that file the
 * packager writes the csproj's <RouteContextName>; without both the runtime opens a context
 * named after the module.
 */
export function moduleContextName(moduleConfig: unknown, csprojText: string, pkg: string): string {
    const root = asObject(moduleConfig);
    const configured = root ? entry(root, "ContextName") : undefined;
    if (typeof configured === "string" && configured.trim())
        return configured.trim();
    return msbuildProperty(csprojText, "RouteContextName") ?? pkg;
}

function msbuildProperty(csprojText: string, name: string): string | null {
    const match = new RegExp(`<${name}>\\s*([^<\\s]+)\\s*</${name}>`).exec(csprojText);
    return match ? match[1] : null;
}

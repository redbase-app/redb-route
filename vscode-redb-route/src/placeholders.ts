/**
 * {{key}} / {{key:default}} resolution for the VIEW (§8: the placeholder is shown together
 * with its resolved value; what gets EDITED is always the placeholder — Р18). The resolver
 * only substitutes for display and scheme detection; it never writes.
 */

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
            const value = config.get(key.trim()) ?? fallback;
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
 * Flattens one parsed JSON config into dot keys: {"demo":{"tag":"x"}} and {"demo.tag":"x"}
 * both land as demo.tag — the merged L1–L5 pipeline reads them the same way.
 */
export function flattenConfig(json: unknown, into: ConfigMap, prefix = ""): void {
    if (json === null || typeof json !== "object" || Array.isArray(json)) {
        if (prefix.length > 0 && json !== null && typeof json !== "object")
            into.set(prefix, String(json));
        return;
    }
    for (const [key, value] of Object.entries(json as Record<string, unknown>)) {
        const path = prefix.length > 0 ? `${prefix}.${key}` : key;
        flattenConfig(value, into, path);
    }
}

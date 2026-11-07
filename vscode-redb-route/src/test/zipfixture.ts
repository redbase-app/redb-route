import { deflateRawSync } from "zlib";

export interface FixtureEntry {
    name: string;
    content: string;
    /** Store without compression instead of deflating. */
    stored?: boolean;
    /** Override the general purpose flags (bit 0 marks an encrypted entry). */
    flags?: number;
    /** Override the compression method written to the headers. */
    method?: number;
}

/** Builds a minimal zip archive in memory for the reader's tests. CRCs are not checked by the reader and left zero. */
export function buildZip(entries: FixtureEntry[], comment = ""): Buffer {
    const locals: Buffer[] = [];
    const centrals: Buffer[] = [];
    let offset = 0;
    for (const entry of entries) {
        const name = Buffer.from(entry.name, "utf8");
        const raw = Buffer.from(entry.content, "utf8");
        const data = entry.stored ? raw : deflateRawSync(raw);
        const method = entry.method ?? (entry.stored ? 0 : 8);
        const flags = entry.flags ?? 0;

        const local = Buffer.alloc(30);
        local.writeUInt32LE(0x04034b50, 0);
        local.writeUInt16LE(20, 4);
        local.writeUInt16LE(flags, 6);
        local.writeUInt16LE(method, 8);
        local.writeUInt32LE(data.length, 18);
        local.writeUInt32LE(raw.length, 22);
        local.writeUInt16LE(name.length, 26);
        locals.push(local, name, data);

        const central = Buffer.alloc(46);
        central.writeUInt32LE(0x02014b50, 0);
        central.writeUInt16LE(20, 4);
        central.writeUInt16LE(20, 6);
        central.writeUInt16LE(flags, 8);
        central.writeUInt16LE(method, 10);
        central.writeUInt32LE(data.length, 20);
        central.writeUInt32LE(raw.length, 24);
        central.writeUInt16LE(name.length, 28);
        central.writeUInt32LE(offset, 42);
        centrals.push(central, name);

        offset += local.length + name.length + data.length;
    }
    const directory = Buffer.concat(centrals);
    const commentBytes = Buffer.from(comment, "utf8");
    const end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50, 0);
    end.writeUInt16LE(entries.length, 8);
    end.writeUInt16LE(entries.length, 10);
    end.writeUInt32LE(directory.length, 12);
    end.writeUInt32LE(offset, 16);
    end.writeUInt16LE(commentBytes.length, 20);
    return Buffer.concat([...locals, directory, end, commentBytes]);
}

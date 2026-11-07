/**
 * Reads entries out of a zip archive (a .nupkg is one) with Node's zlib, so the extension
 * ships without a zip dependency. Enough for template packages: stored and deflated entries
 * found through the central directory. ZIP64 archives and encrypted entries are refused.
 */

import { inflateRawSync } from "zlib";

const END_OF_CENTRAL_DIRECTORY = 0x06054b50;
const CENTRAL_DIRECTORY_ENTRY = 0x02014b50;
const LOCAL_FILE_HEADER = 0x04034b50;
const STORED = 0;
const DEFLATED = 8;

export class ZipFormatError extends Error {
    constructor(message: string) {
        super(message);
        this.name = "ZipFormatError";
    }
}

/** One entry of the central directory. */
export interface ZipEntry {
    /** The path inside the archive, with forward slashes. */
    name: string;
    method: number;
    compressedSize: number;
    uncompressedSize: number;
    localHeaderOffset: number;
    flags: number;
}

/** Lists the archive's entries in central directory order. */
export function zipEntries(archive: Buffer): ZipEntry[] {
    const end = findEndOfCentralDirectory(archive);
    const count = archive.readUInt16LE(end + 10);
    const size = archive.readUInt32LE(end + 12);
    const start = archive.readUInt32LE(end + 16);
    if (count === 0xffff || size === 0xffffffff || start === 0xffffffff)
        throw new ZipFormatError("ZIP64 archives are not supported");
    if (start + size > end)
        throw new ZipFormatError("the central directory lies outside the archive");

    const entries: ZipEntry[] = [];
    let offset = start;
    for (let i = 0; i < count; i++) {
        if (offset + 46 > archive.length || archive.readUInt32LE(offset) !== CENTRAL_DIRECTORY_ENTRY)
            throw new ZipFormatError(`central directory entry ${i} is damaged`);
        const nameLength = archive.readUInt16LE(offset + 28);
        const extraLength = archive.readUInt16LE(offset + 30);
        const commentLength = archive.readUInt16LE(offset + 32);
        entries.push({
            flags: archive.readUInt16LE(offset + 8),
            method: archive.readUInt16LE(offset + 10),
            compressedSize: archive.readUInt32LE(offset + 20),
            uncompressedSize: archive.readUInt32LE(offset + 24),
            localHeaderOffset: archive.readUInt32LE(offset + 42),
            name: archive.toString("utf8", offset + 46, offset + 46 + nameLength).replace(/\\/g, "/"),
        });
        offset += 46 + nameLength + extraLength + commentLength;
    }
    return entries;
}

/** The uncompressed bytes of one entry. */
export function readZipEntry(archive: Buffer, entry: ZipEntry): Buffer {
    if (entry.flags & 0x1)
        throw new ZipFormatError(`'${entry.name}' is encrypted`);
    const header = entry.localHeaderOffset;
    if (header + 30 > archive.length || archive.readUInt32LE(header) !== LOCAL_FILE_HEADER)
        throw new ZipFormatError(`the local header of '${entry.name}' is damaged`);
    // The local header carries its own name and extra lengths, which may differ from the
    // central directory's; the sizes are taken from the central directory, which is always set.
    const dataStart = header + 30 + archive.readUInt16LE(header + 26) + archive.readUInt16LE(header + 28);
    const data = archive.subarray(dataStart, dataStart + entry.compressedSize);
    if (data.length !== entry.compressedSize)
        throw new ZipFormatError(`'${entry.name}' is truncated`);
    if (entry.method === STORED)
        return Buffer.from(data);
    if (entry.method === DEFLATED)
        return inflateRawSync(data);
    throw new ZipFormatError(`'${entry.name}' uses unsupported compression method ${entry.method}`);
}

/** Reads every entry whose name the predicate accepts, keyed by name. */
export function readZipEntries(archive: Buffer, accept: (name: string) => boolean): Map<string, Buffer> {
    const result = new Map<string, Buffer>();
    for (const entry of zipEntries(archive))
        if (!entry.name.endsWith("/") && accept(entry.name))
            result.set(entry.name, readZipEntry(archive, entry));
    return result;
}

/** The end-of-central-directory record sits in the last 22 bytes plus an archive comment of up to 64 KiB. */
function findEndOfCentralDirectory(archive: Buffer): number {
    if (archive.length < 22)
        throw new ZipFormatError("not a zip archive: too short");
    const lowest = Math.max(0, archive.length - 22 - 0xffff);
    for (let offset = archive.length - 22; offset >= lowest; offset--)
        if (archive.readUInt32LE(offset) === END_OF_CENTRAL_DIRECTORY)
            return offset;
    throw new ZipFormatError("not a zip archive: no end of central directory record");
}

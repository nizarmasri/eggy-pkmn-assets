// Just enough of the zip format to pull single entries out of a remote archive by byte range:
// the end-of-central-directory records (plain and zip64), the central directory itself, and each
// entry's local header. Entries are stored (0) or deflated (8); every one is checked against its CRC32.
import zlib from 'node:zlib';

const EOCD = 0x06054b50;
const EOCD64 = 0x06064b50;
const EOCD64_LOCATOR = 0x07064b50;
const CENTRAL = 0x02014b50;
const LOCAL = 0x04034b50;

export const crc32 = (buf) => zlib.crc32(buf) >>> 0;

// The tail of the file (the last ~64 KB) holds the end records. Returns where the central directory is.
export function readEnd(tail, fileSize) {
  const at = lastIndexOfU32(tail, EOCD);
  if (at < 0) throw new Error('no end-of-central-directory record in the tail');
  let count = tail.readUInt16LE(at + 10);
  let cdSize = tail.readUInt32LE(at + 12);
  let cdOffset = tail.readUInt32LE(at + 16);
  const loc = at - 20;
  if (loc >= 0 && tail.readUInt32LE(loc) === EOCD64_LOCATOR) {
    const tailStart = fileSize - tail.length;
    const rec = Number(tail.readBigUInt64LE(loc + 8)) - tailStart;
    if (rec >= 0 && tail.readUInt32LE(rec) === EOCD64) {
      count = Number(tail.readBigUInt64LE(rec + 32));
      cdSize = Number(tail.readBigUInt64LE(rec + 40));
      cdOffset = Number(tail.readBigUInt64LE(rec + 48));
    }
  }
  return { count, cdSize, cdOffset };
}

function lastIndexOfU32(buf, value) {
  for (let i = buf.length - 22; i >= 0; i--) if (buf.readUInt32LE(i) === value) return i;
  return -1;
}

// Every entry of a central directory: name, method, crc, compressed/uncompressed size, local header offset.
export function readCentralDirectory(cd, count) {
  const entries = [];
  let p = 0;
  while (p + 46 <= cd.length && entries.length < count) {
    if (cd.readUInt32LE(p) !== CENTRAL) throw new Error(`bad central directory record at ${p}`);
    const flags = cd.readUInt16LE(p + 8);
    const method = cd.readUInt16LE(p + 10);
    const crc = cd.readUInt32LE(p + 16);
    let csize = cd.readUInt32LE(p + 20);
    let usize = cd.readUInt32LE(p + 24);
    const n = cd.readUInt16LE(p + 28), x = cd.readUInt16LE(p + 30), c = cd.readUInt16LE(p + 32);
    let lho = cd.readUInt32LE(p + 42);
    const name = cd.toString(flags & 0x800 ? 'utf8' : 'latin1', p + 46, p + 46 + n);
    // zip64 extra field: only the values that overflowed are present, in this order.
    let e = p + 46 + n;
    const end = e + x;
    while (e + 4 <= end) {
      const id = cd.readUInt16LE(e), len = cd.readUInt16LE(e + 2);
      if (id === 0x0001) {
        let q = e + 4;
        if (usize === 0xffffffff) { usize = Number(cd.readBigUInt64LE(q)); q += 8; }
        if (csize === 0xffffffff) { csize = Number(cd.readBigUInt64LE(q)); q += 8; }
        if (lho === 0xffffffff) { lho = Number(cd.readBigUInt64LE(q)); q += 8; }
      }
      e += 4 + len;
    }
    if (!name.endsWith('/')) entries.push({ name, method, crc, csize, usize, lho });
    p += 46 + n + x + c;
  }
  return entries;
}

// A local header's fixed part is 30 bytes; the name and extra field that follow can differ in
// length from the central directory's copy, so the data start is read from the header itself.
export const LOCAL_SLACK = 30 + 1024 + 256;

export function dataStart(buf, at) {
  if (buf.readUInt32LE(at) !== LOCAL) throw new Error(`no local header at ${at}`);
  return at + 30 + buf.readUInt16LE(at + 26) + buf.readUInt16LE(at + 28);
}

// Decompresses one entry and proves it is the right bytes (size and CRC32 from the central directory).
export function extract(entry, compressed) {
  let data;
  if (entry.method === 0) data = compressed;
  else if (entry.method === 8) data = zlib.inflateRawSync(compressed);
  else throw new Error(`${entry.name}: compression method ${entry.method} is not supported`);
  if (data.length !== entry.usize) throw new Error(`${entry.name}: ${data.length} bytes, expected ${entry.usize}`);
  const crc = crc32(data);
  if (crc !== entry.crc) throw new Error(`${entry.name}: CRC ${crc.toString(16)}, expected ${entry.crc.toString(16)}`);
  return data;
}

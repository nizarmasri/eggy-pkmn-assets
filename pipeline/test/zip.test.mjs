// node --test pipeline/test
// The ranged-zip reader against archives built here: stored and deflated entries, a zip64
// central directory entry, a local extra field longer than the central one, and a CRC mismatch.
import test from 'node:test';
import assert from 'node:assert/strict';
import zlib from 'node:zlib';
import { crc32, readEnd, readCentralDirectory, dataStart, extract, LOCAL_SLACK } from '../lib/zip.mjs';

function makeZip(files, { zip64 = false, localExtra = 0 } = {}) {
  const locals = [], centrals = [];
  let offset = 0;
  for (const f of files) {
    const data = Buffer.from(f.data);
    const body = f.deflate ? zlib.deflateRawSync(data) : data;
    const name = Buffer.from(f.name);
    const lx = Buffer.alloc(localExtra);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(f.deflate ? 8 : 0, 8);
    local.writeUInt32LE(crc32(data), 14);
    local.writeUInt32LE(body.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(name.length, 26);
    local.writeUInt16LE(lx.length, 28);
    const cx = zip64 ? Buffer.alloc(4 + 24) : Buffer.alloc(0);
    if (zip64) {
      cx.writeUInt16LE(1, 0);
      cx.writeUInt16LE(24, 2);
      cx.writeBigUInt64LE(BigInt(data.length), 4);
      cx.writeBigUInt64LE(BigInt(body.length), 12);
      cx.writeBigUInt64LE(BigInt(offset), 20);
    }
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(f.deflate ? 8 : 0, 10);
    central.writeUInt32LE(f.badCrc ? 1234 : crc32(data), 16);
    central.writeUInt32LE(zip64 ? 0xffffffff : body.length, 20);
    central.writeUInt32LE(zip64 ? 0xffffffff : data.length, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt16LE(cx.length, 30);
    central.writeUInt32LE(zip64 ? 0xffffffff : offset, 42);
    const rec = Buffer.concat([local, name, lx, body]);
    locals.push(rec);
    centrals.push(Buffer.concat([central, name, cx]));
    offset += rec.length;
  }
  const cd = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(files.length, 10);
  end.writeUInt32LE(cd.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, cd, end]);
}

const files = [
  { name: 'pokemon/data/pm0025/pm0025_00_00/pm0025_00_00.trmdl', data: 'model'.repeat(50), deflate: true },
  { name: 'pokemon/data/pm0025/pm0025_00_00/a.bntx', data: Buffer.alloc(3000, 7), deflate: false },
  { name: 'dir/', data: '' },
];

for (const variant of [{}, { zip64: true }, { localExtra: 300 }]) {
  test(`reads every entry back by range (${JSON.stringify(variant)})`, () => {
    const zip = makeZip(files, variant);
    const tail = zip.subarray(Math.max(0, zip.length - 70000));
    const { count, cdSize, cdOffset } = readEnd(tail, zip.length);
    assert.equal(count, 3);
    const entries = readCentralDirectory(zip.subarray(cdOffset, cdOffset + cdSize), count);
    assert.deepEqual(entries.map((e) => e.name), files.slice(0, 2).map((f) => f.name)); // directories are skipped
    for (const [i, e] of entries.entries()) {
      // What fetch-entries reads: the local header plus slack, then the data from where the header says.
      const seg = zip.subarray(e.lho, Math.min(zip.length, e.lho + LOCAL_SLACK + e.csize));
      const from = dataStart(seg, 0);
      const data = extract(e, seg.subarray(from, from + e.csize));
      assert.deepEqual(data, Buffer.from(files[i].data));
    }
  });
}

test('a CRC mismatch is an error, not data', () => {
  const zip = makeZip([{ name: 'x.bin', data: 'hello', badCrc: true }]);
  const { count, cdSize, cdOffset } = readEnd(zip, zip.length);
  const [e] = readCentralDirectory(zip.subarray(cdOffset, cdOffset + cdSize), count);
  const from = dataStart(zip, e.lho);
  assert.throws(() => extract(e, zip.subarray(from, from + e.csize)), /CRC/);
});

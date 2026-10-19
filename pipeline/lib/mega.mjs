// Ranged, decrypted reads of single files inside the public PokéModels MEGA folder.
// MEGA encrypts files with AES-CTR, so any byte range decrypts on its own (megajs does it).
import { File } from 'megajs';

export const FOLDER = 'https://mega.nz/folder/elJhVC5D#NU-yzmXuTlsIIzXAMLKVaA';

// MEGA's free transfer quota is per IP. Hitting it is not an error in our code: say so plainly,
// keep what was fetched (the cache), and let a later run (a new runner, a new IP) carry on.
export class QuotaError extends Error {
  constructor(seconds, cause) {
    super(`MEGA transfer quota reached${seconds ? `, resets in ${seconds} s` : ''}`);
    this.seconds = seconds;
    this.cause = cause;
  }
}

export async function openFolder(url = FOLDER) {
  const folder = File.fromURL(url);
  await retry('list the MEGA folder', () => folder.loadAttributes());
  const files = new Map();
  const walk = (n) => {
    if (n.directory) for (const c of n.children ?? []) walk(c);
    else files.set(n.name, n);
  };
  walk(folder);
  return files;
}

export const stats = { bytes: 0, requests: 0 };

// Bytes [start, end] (inclusive) of a MEGA file.
export function readRange(file, start, end, label = file.name) {
  return retry(`${label} ${start}-${end}`, () => once(file, start, end));
}

function once(file, start, end) {
  return new Promise((resolve, reject) => {
    const bufs = [];
    let got = 0;
    stats.requests++;
    const s = file.download({ start, end, maxConnections: 1 });
    s.on('data', (b) => { bufs.push(b); got += b.length; });
    s.on('error', reject);
    s.on('end', () => {
      stats.bytes += got;
      const want = end - start + 1;
      if (got !== want) reject(new Error(`short read: ${got} of ${want} bytes`));
      else resolve(Buffer.concat(bufs));
    });
  });
}

const isQuota = (e) => e?.timeLimit !== undefined || /Bandwidth limit|EOVERQUOTA|over quota|\b509\b/i.test(String(e?.message));

async function retry(what, fn, tries = 6) {
  for (let i = 1; ; i++) {
    try {
      return await fn();
    } catch (e) {
      if (isQuota(e)) {
        const seconds = Number(e.timeLimit) || 0;
        // A short wait is cheaper than a new run; a long one is not worth idle runner minutes.
        if (seconds > 0 && seconds <= 600 && i < tries) {
          console.log(`  quota: waiting ${seconds} s for MEGA to reset (${what})`);
          await sleep((seconds + 5) * 1000);
          continue;
        }
        throw new QuotaError(seconds, e);
      }
      if (i >= tries) throw new Error(`${what}: ${e.message} (after ${tries} tries)`);
      const wait = Math.min(60, 2 ** i) * 1000;
      console.log(`  retry ${i}/${tries - 1} in ${wait / 1000} s: ${what}: ${e.message}`);
      await sleep(wait);
    }
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

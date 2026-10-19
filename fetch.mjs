#!/usr/bin/env node
// Pulls a release of this repo (the GLBs, models.json, report.json) over a slow, flaky connection:
// every file in Range segments, resumed from its last good byte after any failure or restart
// (<file>.part + <file>.state), checked against SHA256SUMS, skipped when it is already there and
// verifies. No dependencies beyond Node 18+.
//
//   node fetch.mjs [--tag b0] [--out <dir>] [--rate 200k] [--segment 1m] [--only <regex>] [--detach]
//
//   --tag      release tag (default b0)
//   --out      where to put the files (default: out/ next to this script)
//   --rate     speed cap in bytes per second, with k/m suffixes (default: none)
//   --segment  bytes per Range request (default 1m)
//   --only     only the files whose names match this regular expression
//   --detach   keep going in the background under caffeinate (macOS), logging to <out>/fetch.log
//   --repo     owner/name (default nizarmasri/eggy-pkmn-assets)
//
// A token (GH_TOKEN / GITHUB_TOKEN, or `gh auth token`) is used when there is one; the repo is public.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawn, execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const script = fileURLToPath(import.meta.url);
const args = process.argv.slice(2);
const opt = (name, fallback) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};
const flag = (name) => args.includes(`--${name}`);
const bytes = (s) => {
  if (s == null) return null;
  const m = /^(\d+(?:\.\d+)?)([kmg]?)b?$/i.exec(String(s).trim());
  if (!m) throw new Error(`not a size: ${s}`);
  return Math.round(Number(m[1]) * { '': 1, k: 1024, m: 1024 ** 2, g: 1024 ** 3 }[m[2].toLowerCase()]);
};

const repo = opt('repo', 'nizarmasri/eggy-pkmn-assets');
const tag = opt('tag', 'b0');
const outDir = path.resolve(opt('out', path.join(path.dirname(script), 'out')));
const rate = bytes(opt('rate', null));
const segment = bytes(opt('segment', '1m'));
const only = opt('only', null) ? new RegExp(opt('only')) : null;
fs.mkdirSync(outDir, { recursive: true });

if (flag('detach')) {
  const rest = args.filter((a) => a !== '--detach');
  const log = fs.openSync(path.join(outDir, 'fetch.log'), 'a');
  const cmd = process.platform === 'darwin' ? 'caffeinate' : process.execPath;
  const cmdArgs = process.platform === 'darwin' ? ['-i', process.execPath, script, ...rest] : [script, ...rest];
  const child = spawn(cmd, cmdArgs, { detached: true, stdio: ['ignore', log, log], env: { ...process.env, GH_TOKEN: token() } });
  child.unref();
  console.log(`fetching in the background (pid ${child.pid}); log: ${path.join(outDir, 'fetch.log')}`);
  process.exit(0);
}

// One fetcher per folder at a time.
const lockPath = path.join(outDir, '.fetch.lock');
try {
  const pid = Number(fs.readFileSync(lockPath, 'utf8'));
  if (pid && pid !== process.pid && alive(pid)) {
    console.log(`another fetch (pid ${pid}) is already filling ${outDir}`);
    process.exit(0);
  }
} catch {}
fs.writeFileSync(lockPath, String(process.pid));
process.on('exit', () => { try { fs.unlinkSync(lockPath); } catch {} });
for (const sig of ['SIGINT', 'SIGTERM', 'SIGHUP']) process.on(sig, () => process.exit(130));

const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function alive(pid) {
  try { process.kill(pid, 0); return true; } catch { return false; }
}

function token() {
  if (process.env.GH_TOKEN || process.env.GITHUB_TOKEN) return process.env.GH_TOKEN || process.env.GITHUB_TOKEN;
  for (const extra of [['--user', 'nizarmasri'], []]) {
    try { return execFileSync('gh', ['auth', 'token', '--hostname', 'github.com', ...extra], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim(); } catch {}
  }
  return null;
}
const auth = token();
const api = (url, accept = 'application/vnd.github+json') =>
  fetch(url, { headers: { accept, 'user-agent': 'eggy-fetch', ...(auth ? { authorization: `Bearer ${auth}` } : {}) }, redirect: 'manual' });

// Retries forever on network trouble (the connection comes back), with backoff capped at a minute.
async function patiently(what, fn) {
  for (let i = 0; ; i++) {
    try {
      return await fn();
    } catch (e) {
      if (e.fatal) throw e;
      const wait = Math.min(60, 2 ** Math.min(i, 6)) * 1000;
      log(`${what}: ${e.message}; again in ${wait / 1000} s`);
      await sleep(wait);
    }
  }
}
const fatal = (msg) => Object.assign(new Error(msg), { fatal: true });

// The asset's current download URL: the API answers with a redirect to a short-lived signed URL,
// which is asked for again before every segment.
async function assetUrl(asset) {
  const res = await api(`https://api.github.com/repos/${repo}/releases/assets/${asset.id}`, 'application/octet-stream');
  if (res.status === 302 || res.status === 301) return res.headers.get('location');
  if (res.status === 200) return asset.browser_download_url;
  if (res.status === 401 || res.status === 403 || res.status === 404) throw fatal(`asset ${asset.name}: HTTP ${res.status} (token?)`);
  throw new Error(`asset ${asset.name}: HTTP ${res.status}`);
}

async function small(asset) {
  return patiently(asset.name, async () => {
    const res = await fetch(await assetUrl(asset));
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return Buffer.from(await res.arrayBuffer());
  });
}

function sha256File(file) {
  const h = crypto.createHash('sha256');
  const fd = fs.openSync(file, 'r');
  const buf = Buffer.alloc(1 << 20);
  for (let n; (n = fs.readSync(fd, buf, 0, buf.length, null)) > 0; ) h.update(buf.subarray(0, n));
  fs.closeSync(fd);
  return h.digest('hex');
}

let windowStart = Date.now(), windowBytes = 0;
async function throttle(n) {
  if (!rate) return;
  windowBytes += n;
  const ahead = windowBytes / rate * 1000 - (Date.now() - windowStart);
  if (ahead > 0) await sleep(ahead);
  if (Date.now() - windowStart > 10000) { windowStart = Date.now(); windowBytes = 0; }
}

async function download(asset, sha256) {
  const final = path.join(outDir, asset.name);
  const part = final + '.part', statePath = final + '.state';
  if (fs.existsSync(final) && fs.statSync(final).size === asset.size && sha256File(final) === sha256) {
    log(`${asset.name}: already here`);
    return;
  }
  // Resume only a .part of this exact file (same size and hash); anything else starts over.
  let done = 0;
  try {
    const st = JSON.parse(fs.readFileSync(statePath, 'utf8'));
    if (st.sha256 === sha256 && st.size === asset.size && fs.existsSync(part)) done = Math.min(st.done, fs.statSync(part).size);
  } catch {}
  const fd = fs.openSync(part, done > 0 ? 'r+' : 'w');
  fs.ftruncateSync(fd, done);
  const save = () => {
    fs.fsyncSync(fd);
    fs.writeFileSync(statePath, JSON.stringify({ file: asset.name, size: asset.size, sha256, done, updated: new Date().toISOString() }));
  };
  if (done > 0) log(`${asset.name}: resuming at ${done} of ${asset.size} bytes`);
  const started = Date.now(), startedAt = done;
  while (done < asset.size) {
    await patiently(asset.name, async () => {
      const end = Math.min(asset.size, done + segment) - 1;
      const res = await fetch(await assetUrl(asset), { headers: { range: `bytes=${done}-${end}` }, signal: AbortSignal.timeout(120000) });
      if (res.status !== 206 && !(res.status === 200 && done === 0)) throw new Error(`HTTP ${res.status} for bytes ${done}-${end}`);
      let skip = res.status === 200 ? done : 0; // a server that ignored the Range starts at byte 0
      const want = end - done + 1;
      let got = 0;
      try {
        for await (const chunk of res.body) {
          let c = Buffer.from(chunk);
          if (skip) { const s = Math.min(skip, c.length); c = c.subarray(s); skip -= s; }
          if (got + c.length > want) c = c.subarray(0, want - got);
          if (!c.length) continue;
          fs.writeSync(fd, c, 0, c.length, done);
          done += c.length;
          got += c.length;
          await throttle(c.length);
          if (got >= want) break;
        }
      } finally {
        save(); // whatever arrived counts, even when the connection broke mid-segment
      }
      if (got < want) throw new Error(`connection dropped at byte ${done}`);
    });
    const secs = (Date.now() - started) / 1000;
    log(`${asset.name}: ${(done / 1e6).toFixed(2)}/${(asset.size / 1e6).toFixed(2)} MB (${((done - startedAt) / 1024 / Math.max(secs, 0.001)).toFixed(0)} KB/s)`);
  }
  fs.closeSync(fd);
  const got = sha256File(part);
  if (got !== sha256) {
    fs.rmSync(part, { force: true });
    fs.rmSync(statePath, { force: true });
    throw new Error(`${asset.name}: sha256 ${got} does not match ${sha256}; the partial file was removed, run again`);
  }
  fs.renameSync(part, final);
  fs.rmSync(statePath, { force: true });
  log(`${asset.name}: done, sha256 verified`);
}

const release = await patiently('release', async () => {
  const res = await api(`https://api.github.com/repos/${repo}/releases/tags/${tag}`);
  if (res.status === 404) throw fatal(`no release ${tag} in ${repo} (or no access)`);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json();
});
const assets = new Map(release.assets.map((a) => [a.name, a]));
if (!assets.has('SHA256SUMS')) throw new Error(`release ${tag} has no SHA256SUMS`);
const sums = (await small(assets.get('SHA256SUMS'))).toString('utf8').trim().split('\n').map((l) => {
  const [hash, name] = l.trim().split(/\s+\*?/);
  return { hash, name };
});
const wanted = sums.filter((s) => !only || only.test(s.name));
const total = wanted.reduce((n, s) => n + (assets.get(s.name)?.size ?? 0), 0);
log(`${repo}@${tag}: ${wanted.length} files, ${(total / 1e6).toFixed(2)} MB -> ${outDir}${rate ? ` (capped at ${(rate / 1024).toFixed(0)} KB/s)` : ''}`);
let failed = 0;
for (const { hash, name } of wanted) {
  const asset = assets.get(name);
  if (!asset) { log(`${name}: listed in SHA256SUMS but not in the release`); failed++; continue; }
  try { await download(asset, hash); } catch (e) { log(e.message); failed++; }
}
fs.writeFileSync(path.join(outDir, 'SHA256SUMS'), sums.map((s) => `${s.hash}  ${s.name}`).join('\n') + '\n');
log(failed ? `${failed} file(s) failed; run again to resume` : 'all files verified');
process.exitCode = failed ? 1 : 0;

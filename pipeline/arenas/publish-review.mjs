// Replaces the arenas-b0-review pre-release's files with those of <dir> (plus a SHA256SUMS, so
// fetch.mjs can bring them over resumably).
//
//   node pipeline/arenas/publish-review.mjs <dir>
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';

const dir = process.argv[2];
const tag = `${process.env.TAG}-review`;
const files = fs.readdirSync(dir).filter((f) => f !== 'SHA256SUMS').sort();
fs.writeFileSync(path.join(dir, 'SHA256SUMS'), files.map((f) => `${crypto.createHash('sha256').update(fs.readFileSync(path.join(dir, f))).digest('hex')}  ${f}`).join('\n') + '\n');
const gh = (...a) => execFileSync('gh', a, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] });
try { gh('release', 'view', tag); } catch { gh('release', 'create', tag, '--prerelease', '--title', 'Arena review', '--notes', 'Inventory and review sheets of the arena build.'); }
for (const a of gh('release', 'view', tag, '--json', 'assets', '-q', '.assets[].name').split('\n').filter(Boolean)) gh('release', 'delete-asset', tag, a, '-y');
gh('release', 'upload', tag, '--clobber', ...[...files, 'SHA256SUMS'].map((f) => path.join(dir, f)));
console.log(`${tag}: ${files.length} files`);

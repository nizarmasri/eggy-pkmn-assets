// The converter's jobs for the ids the plan sends down the Unity path (Brilliant Diamond / Shining Pearl):
// each one's model folder as extract.sh left it (pmDDDD_FF_00), and its shiny bundle's (pmDDDD_FF_01) when
// there is one. Also prints the folders extract.sh has to make, for the workflow.
//
//   node pipeline/bdsp/jobs.mjs <plan.json> <extract out dir> <jobs.json>      writes the jobs
//   node pipeline/bdsp/jobs.mjs <plan.json> --folders                           prints the folders
import fs from 'node:fs';
import path from 'node:path';

const [planPath, outDir, jobsPath] = process.argv.slice(2);
const plan = JSON.parse(fs.readFileSync(planPath, 'utf8'));
const bdsp = plan.jobs.filter((j) => j.src === 'bdsp');
const folderOf = (j) => `pm${String(j.dex).padStart(4, '0')}_${j.ff}_00`;
if (outDir === '--folders') {
  console.log(bdsp.flatMap((j) => [folderOf(j), folderOf(j).replace(/_00$/, '_01')]).join(' '));
  process.exit(0);
}
const jobs = [];
for (const j of bdsp) {
  const folder = folderOf(j);
  const dir = path.resolve(outDir, folder);
  if (!fs.existsSync(path.join(dir, `${folder}.glb`))) {
    console.log(`${j.id}: no ${folder}.glb from the Unity path`);
    continue;
  }
  const shinyDir = path.resolve(outDir, folder.replace(/_00$/, '_01'));
  const shiny = fs.existsSync(path.join(shinyDir, 'materials'));
  jobs.push({ id: j.id, dex: j.dex, src: 'bdsp', folder, modelDir: dir, animDir: dir, shiny, ...(shiny ? { shinyDir } : {}) });
}
fs.writeFileSync(jobsPath, JSON.stringify(jobs, null, 1));
console.log(`${jobs.length} BDSP jobs (${jobs.filter((x) => x.shiny).length} with a shiny)`);

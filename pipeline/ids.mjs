// Every model id the pack should hold: the sprite ids the game draws (the p1 pack's index), the forms
// Showdown can put on the field that the sprite pack has no sheet for, and a female model wherever the
// sprite pack has a female sheet. Each id says which game folder holds it ("ff": pmDDDD_FF_00 in
// Scarlet/Violet-family games, pmDDDD_FF in Sword/Shield and Let's Go) and, for Arceus, which type.
//
//   node pipeline/ids.mjs <p1 ids.json> <out ids.json>
import fs from 'node:fs';
import path from 'node:path';

// Species with forms: the forms in the games' own order. A species' first form lives in folder 11, the
// next in 12, and so on (pm0479_11_00 is Rotom, pm0479_13_00 Rotom-Wash). "" is the base sprite id.
export const FORMS = {
  rotom: ['', 'heat', 'wash', 'frost', 'fan', 'mow'],
  deoxys: ['', 'attack', 'defense', 'speed'],
  giratina: ['', 'origin'],
  shaymin: ['', 'sky'],
  burmy: ['', 'sandy', 'trash'],
  wormadam: ['', 'sandy', 'trash'],
  shellos: ['', 'east'],
  gastrodon: ['', 'east'],
  cherrim: ['', 'sunshine'],
  castform: ['', 'sunny', 'rainy', 'snowy'],
  unown: ['', ...'bcdefghijklmnopqrstuvwxyz'.split(''), 'exclamation', 'question'],
  pichu: ['', 'spikyeared'],
};

// Arceus keeps one model; its types are material sets in its .trmmt (Multitype with a plate). Showdown's
// Arceus-<Type> for every type of Generation 4 (no Fairy: the game's dex has no Fairy type).
export const ARCEUS_TYPES = ['fighting', 'flying', 'poison', 'ground', 'rock', 'bug', 'ghost', 'steel', 'fire', 'water', 'grass', 'electric', 'psychic', 'ice', 'dragon', 'dark'];

export function buildIds(p1) {
  const out = [];
  const seen = new Set();
  const add = (e) => {
    if (seen.has(e.id)) return;
    seen.add(e.id);
    out.push(e);
  };
  const dexOf = p1.ids;
  for (const [id, dex] of Object.entries(dexOf)) {
    const [base, ...rest] = id.split('-');
    const form = rest.join('-');
    const forms = FORMS[base];
    if (forms) {
      const i = forms.indexOf(form);
      if (i < 0) throw new Error(`${id}: form ${form} is not in the forms table`);
      add({ id, dex, ff: String(11 + i), why: 'p1' });
    } else if (id === 'arceus') {
      add({ id, dex, ff: '11', why: 'p1' });
    } else {
      if (form) throw new Error(`${id}: a form the table does not know`);
      add({ id, dex, ff: '00', why: 'p1' });
    }
  }
  // Forms the sprite pack draws as their base sprite: a model each.
  for (const [base, forms] of Object.entries(FORMS)) {
    const dex = dexOf[base];
    if (!dex) continue;
    forms.forEach((form, i) => {
      if (form) add({ id: `${base}-${form}`, dex, ff: String(11 + i), why: 'showdown form' });
    });
  }
  for (const type of ARCEUS_TYPES) add({ id: `arceus-${type}`, dex: dexOf.arceus, ff: '11', variant: type, why: 'showdown form' });
  // Female models: Showdown draws these species' females with their own sheets (folder 01).
  for (const id of p1.female) add({ id: `${id}-f`, dex: dexOf[id], ff: '01', female: id, why: 'female sheet' });
  return out.sort((a, b) => a.dex - b.dex || a.id.localeCompare(b.id));
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const [inPath, outPath] = process.argv.slice(2);
  const ids = buildIds(JSON.parse(fs.readFileSync(inPath, 'utf8')));
  fs.mkdirSync(path.dirname(path.resolve(outPath)), { recursive: true });
  fs.writeFileSync(outPath, JSON.stringify(ids, null, 1) + '\n');
  const by = {};
  for (const e of ids) by[e.why] = (by[e.why] ?? 0) + 1;
  console.log(`${ids.length} ids:`, JSON.stringify(by));
}

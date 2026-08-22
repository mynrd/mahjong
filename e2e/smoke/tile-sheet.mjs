// Renders every tile face onto one page and screenshots it, so the artwork can be checked by
// eye rather than assumed. Catches the things a file listing cannot: an SVG that is really just
// the face art with no tile body behind it, a missing glyph, a bonus tile that does not sit
// alongside the real ones.
//
//   node smoke/tile-sheet.mjs

import { chromium } from '@playwright/test';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';
import fs from 'node:fs/promises';

const here = path.dirname(fileURLToPath(import.meta.url));
const tiles = path.resolve(here, '../../web/public/tiles');
const shots = path.resolve(here, '../screenshots');

const groups = [
  ['Dots (bulaklak)', ['D1', 'D2', 'D3', 'D4', 'D5', 'D6', 'D7', 'D8', 'D9']],
  ['Bamboo (kahoy)', ['B1', 'B2', 'B3', 'B4', 'B5', 'B6', 'B7', 'B8', 'B9']],
  ['Characters (letra)', ['C1', 'C2', 'C3', 'C4', 'C5', 'C6', 'C7', 'C8', 'C9']],
  ['Winds - bonus tiles here', ['W1', 'W2', 'W3', 'W4']],
  ['Dragons - bonus tiles here', ['R1', 'R2', 'R3']],
  ['Flowers - drawn for this project', ['F1', 'F2', 'F3', 'F4']],
  ['Seasons - drawn for this project', ['S1', 'S2', 'S3', 'S4']],
  ['Back', ['back']],
];

const base = pathToFileURL(tiles + path.sep).href;

/** The corner mark, same rule as the tile component: rank on a suit, a letter on a wind or dragon. */
const LETTERS = { W1: 'E', W2: 'S', W3: 'W', W4: 'N', R1: 'R', R2: 'F', R3: 'P' };
const cornerOf = (code) => (/^[DBC][1-9]$/.test(code) ? code.slice(1) : (LETTERS[code] ?? null));

const html = `<!doctype html>
<meta charset="utf-8">
<style>
  body { margin: 0; padding: 28px; background: #16663f; font: 15px/1.4 "Segoe UI", system-ui, sans-serif; color: #e8f3ec; }
  h2 { font-size: 15px; margin: 26px 0 10px; text-transform: uppercase; letter-spacing: .08em; opacity: .85; }
  h2:first-child { margin-top: 0; }
  .row { display: flex; flex-wrap: wrap; gap: 10px; }
  figure { margin: 0; text-align: center; }

  /* The tile body. The CC0 faces are transparent art with no body of their own, so one is drawn
     here, exactly as the tile component in the app does it. */
  .tile {
    --tile-w: 78px;
    /* Share of the tile's width the face art fills - the same knob the tile component uses. */
    --tile-art: 78%;
    position: relative;
    width: var(--tile-w); height: 104px; border-radius: 9px;
    background: linear-gradient(#fffdf7, #f2ead6);
    border: 1px solid #cdbf9f;
    box-shadow: 0 3px 0 #b8a884, 0 5px 8px rgba(0,0,0,.35);
    display: grid; place-items: center; overflow: hidden;
  }

  /* Sized and placed exactly as the tile component does it, so the cream margin the corner mark
     sits in is the same here as in the app. */
  .tile img {
    position: absolute; left: 50%; bottom: calc(var(--tile-w) * .02);
    transform: translateX(-50%);
    display: block; width: var(--tile-art);
  }
  /* The white dragon's frame is inset inside its own viewBox, so it gets a bigger share. */
  .tile[data-code='R3'] { --tile-art: 92%; }
  .tile.back { background: none; border: none; box-shadow: 0 5px 8px rgba(0,0,0,.35); }
  .tile.back img { --tile-art: 100%; bottom: 0; }

  /* Copied from the tile component, so the sheet shows the mark the app actually draws. */
  .corner {
    position: absolute; top: calc(var(--tile-w) * .03); left: calc(var(--tile-w) * .08);
    font-size: calc(var(--tile-w) * .24); font-weight: 700; line-height: 1;
    letter-spacing: -.03em; color: #6d6152;
  }
  .tile[data-code^='D'] .corner,
  .tile[data-code^='W'] .corner,
  .tile[data-code='R3'] .corner { color: #1d4f8c; }
  .tile[data-code^='B'] .corner,
  .tile[data-code='R2'] .corner { color: #1d7a49; }
  .tile[data-code^='C'] .corner,
  .tile[data-code='R1'] .corner { color: #a83228; }
  figcaption { font-size: 12px; margin-top: 6px; opacity: .8; font-variant-numeric: tabular-nums; }
</style>
${groups
  .map(
    ([title, codes]) => `<h2>${title}</h2><div class="row">${codes
      .map(
        (code) =>
          `<figure><div class="tile ${code === 'back' ? 'back' : ''}" data-code="${code}">` +
          `<img src="${base}${code}.svg" alt="${code}">` +
          `${cornerOf(code) ? `<span class="corner">${cornerOf(code)}</span>` : ''}` +
          `</div><figcaption>${code}</figcaption></figure>`,
      )
      .join('')}</div>`,
  )
  .join('')}
`;

const sheet = path.join(shots, 'tile-sheet.html');
await fs.mkdir(shots, { recursive: true });
await fs.writeFile(sheet, html, 'utf8');

const browser = await chromium.launch();
// At 1x the corner mark is 16px and antialiasing hides whether it clears the artwork, which is
// the one thing this sheet is now being read for.
const page = await browser.newPage({ viewportSize: { width: 1000, height: 900 }, deviceScaleFactor: 2 });

const failed = [];
page.on('requestfailed', (request) => failed.push(request.url()));

await page.goto(pathToFileURL(sheet).href);
await page.waitForLoadState('networkidle');

// An <img> pointing at a broken or empty SVG still lays out, so check the decoded size instead.
const broken = await page.$$eval('img', (nodes) =>
  nodes.filter((n) => !n.complete || n.naturalWidth === 0).map((n) => n.alt),
);

const out = path.join(shots, 'tiles.png');
await page.screenshot({ path: out, fullPage: true });
await browser.close();

if (failed.length) {
  console.error(`FAIL: ${failed.length} tile file(s) could not be loaded`);
  failed.slice(0, 10).forEach((url) => console.error(`  ${url}`));
  process.exit(1);
}

if (broken.length) {
  console.error(`FAIL: ${broken.length} tile(s) rendered as nothing: ${broken.join(', ')}`);
  process.exit(1);
}

const count = groups.reduce((n, [, codes]) => n + codes.length, 0);
console.log(`OK - ${count} tile images rendered`);
console.log(`screenshot: ${out}`);

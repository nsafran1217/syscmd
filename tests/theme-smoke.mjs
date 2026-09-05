// Palettes, backdrops and the Motif colour derivation.
//
// This is the one suite that checks colour, and it does it against numbers with an independent
// source rather than against whatever the code happens to emit: colours sampled from real CDE
// screenshots in docs/website_reference. If the port of Motif's arithmetic ever drifts, the
// derived shadows stop matching those samples and this fails. Nothing else in the suite would
// notice - the other scripts capture screenshots but never compare them.

import { chromium } from 'playwright';

const base = 'http://localhost:5080';
const shots = process.env.SHOTS || '.';
let pass = 0, fail = 0;
const ok = (c, l, x = '') => { console.log(`  ${c ? 'PASS' : 'FAIL'}  ${l}${x ? '   ' + x : ''}`); c ? pass++ : fail++; };

const theme = async (query) => {
  const css = await (await fetch(`${base}/cde/theme.css?${query}`)).text();
  const vars = {};
  for (const [, name, value] of css.matchAll(/--([\w-]+):\s*([^;]+);/g)) vars['--' + name] = value.trim();
  return vars;
};

console.log('\n[Motif colour derivation]');

// A .dp file stores only a background per colour set; everything else is computed. Crimson is
// the palette Solaris shipped: sampling docs/website_reference/img/sun-css/term-full.png gives
// #b24d7a chrome over an #aeb2c3 face, and the reference site's own CSS recorded that face's
// highlight and shadow as #dcdee5 and #5d6069. Those two are the derivation's witnesses.
const crimson = await theme('p=Crimson&b=NoBackdrop&cs=3');
ok(crimson['--cs1-bg'] === '#b24d7a', 'Crimson colour set 1 is the Solaris title bar', crimson['--cs1-bg']);
ok(crimson['--cs2-bg'] === '#aeb2c3', 'Crimson colour set 2 is the Solaris widget face', crimson['--cs2-bg']);
ok(crimson['--cs2-ts'] === '#dcdee5', 'derived top shadow matches the sampled screenshot', crimson['--cs2-ts']);
ok(crimson['--cs2-bs'] === '#5d6069', 'derived bottom shadow matches the sampled screenshot', crimson['--cs2-bs']);

// Default is CDE's own out-of-the-box palette, and it is what this UI was already wearing: the
// hand-picked hexes it used to carry turn out to be this palette run through the same arithmetic.
const dflt = await theme('p=Default&b=Toronto&cs=3');
const expected = {
  '--cs1-bg': '#eda870', '--cs2-bg': '#999999', '--cs2-ts': '#d1d1d2', '--cs2-bs': '#4e4e4e',
  '--cs4-bg': '#686f82', '--cs6-bg': '#4992a7', '--cs6-ts': '#adced7', '--cs6-bs': '#244953',
};
for (const [name, want] of Object.entries(expected)) {
  ok(dflt[name] === want, `Default ${name} reproduces the previous theme`, `${dflt[name]} vs ${want}`);
}

// Motif picks black or white text off one brightness threshold, with nothing in between.
ok(/^#(000000|ffffff)$/.test(dflt['--cs5-fg']), 'foregrounds are pure black or white', dflt['--cs5-fg']);

console.log('\n[backdrops]');

// dtwm stencils a backdrop in the colour set's background over its bottom shadow, which is why a
// CDE desktop reads as a light pattern on a darker ground. Toronto is two-colour, so its PNG
// palette should be exactly that pair and nothing else.
const png = Buffer.from(await (await fetch(`${base}/cde/backdrop.png?p=Default&b=Toronto&cs=3`)).arrayBuffer());
ok(png.subarray(1, 4).toString('ascii') === 'PNG', 'the backdrop endpoint returns a PNG');

const plteAt = png.indexOf(Buffer.from('PLTE', 'ascii'));
const plteLen = png.readUInt32BE(plteAt - 4);
const palette = [];
for (let i = 0; i < plteLen; i += 3) {
  palette.push('#' + png.subarray(plteAt + 4 + i, plteAt + 7 + i).toString('hex'));
}
ok(palette.length === 2, 'Toronto is a two-colour stencil', palette.join(' '));
ok(palette.includes(dflt['--cs3-bs']), 'stencilled over the colour set bottom shadow', dflt['--cs3-bs']);
ok(palette.includes(dflt['--cs3-bg']), 'pattern drawn in the colour set background', dflt['--cs3-bg']);

// The tile has to be tinted by the colour set that was asked for, not a fixed one.
const png7 = Buffer.from(await (await fetch(`${base}/cde/backdrop.png?p=Default&b=Toronto&cs=7`)).arrayBuffer());
ok(!png.equals(png7), 'a different colour set gives a differently tinted tile');

// NoBackdrop means a bare desktop, so the stylesheet must not ask for an image at all.
const bare = await theme('p=Default&b=NoBackdrop&cs=3');
ok(bare['--desktop-image'] === 'none', 'NoBackdrop leaves the desktop unpatterned', bare['--desktop-image']);
ok(bare['--desktop-ground'] === bare['--cs3-bs'], 'the bare desktop is the colour set ground');

console.log('\n[picking a theme in the browser]');

const b = await chromium.launch();
const ctx = await b.newContext({ viewport: { width: 1500, height: 950 } });
const p = await ctx.newPage();
p.on('pageerror', e => console.log('  PAGE ERROR:', e.message));

const faceOf = () => p.evaluate(() =>
  getComputedStyle(document.documentElement).getPropertyValue('--cs2-bg').trim());

await p.goto(`${base}/config/appearance`, { waitUntil: 'networkidle' });
await p.waitForFunction(() => window.Blazor !== undefined);
await p.waitForTimeout(1500);

ok(await p.locator('.palette-row').count() > 30, 'every shipped palette is offered',
   (await p.locator('.palette-row').count()) + ' palettes');
ok(await p.locator('.backdrop-tile').count() > 20, 'every shipped backdrop is offered',
   (await p.locator('.backdrop-tile').count()) + ' backdrops');

const before = await faceOf();
await p.locator('.palette-row', { hasText: 'Neptune' }).first().click();
await p.waitForTimeout(900);
const picked = await faceOf();
ok(picked !== before && picked.length > 0, 'choosing a palette repaints without a reload',
   `${before} -> ${picked}`);
await p.screenshot({ path: `${shots}/theme-neptune.png` });

// The choice rides in a cookie, so the server can render the first paint in the right colours
// rather than flashing the default and correcting it.
await p.reload({ waitUntil: 'networkidle' });
await p.waitForTimeout(1200);
ok(await faceOf() === picked, 'the choice survives a reload', await faceOf());

console.log('\n[random palette on each load]');

await ctx.addCookies([
  { name: 'syscmd.theme', value: 'random', url: base },
  // Escaped the way the picker writes it: a comma is not legal in a cookie value.
  { name: 'syscmd.themePool', value: 'Neptune%2CDesert', url: base },
]);

const seen = new Set();
for (let i = 0; i < 12; i++) {
  await p.goto(base + '/', { waitUntil: 'domcontentloaded' });
  seen.add(await faceOf());
}
const neptune = (await theme('p=Neptune&b=Toronto&cs=3'))['--cs2-bg'];
const desert = (await theme('p=Desert&b=Toronto&cs=3'))['--cs2-bg'];
ok(seen.size === 2, 'random mode uses more than one palette', [...seen].join(' '));
ok([...seen].every(v => v === neptune || v === desert), 'and never one outside the chosen pool',
   [...seen].join(' '));

console.log('\n[chrome]');

await ctx.clearCookies();
await p.goto(base + '/', { waitUntil: 'networkidle' });
await p.waitForFunction(() => window.Blazor !== undefined);
await p.waitForTimeout(1200);

// The join lines that cut the border into corner handles and edge stretchers are what make the
// frame read as CDE rather than as a generic bevel, and they are drawn as background layers.
const joins = await p.locator('.app-main .cde-window').first()
  .evaluate(el => getComputedStyle(el).backgroundImage.split('gradient').length - 1);
ok(joins === 8, 'the frame draws all eight corner join lines', joins + ' lines');

// A rolled-up window is frame plus title bar and nothing else; log-column-smoke asserts it fits
// in 80px, so a title bar that grew past that would break an unrelated suite.
const shaded = await p.locator('.app-main .cde-window').first().evaluate(async (el) => {
  el.querySelector('.cw-min').click();
  await new Promise(r => setTimeout(r, 300));
  const h = el.getBoundingClientRect().height;
  el.querySelector('.cw-min').click();
  return h;
});
ok(shaded < 80, 'a rolled-up frame still fits the height other suites assume', Math.round(shaded) + 'px');

await b.close();
console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);

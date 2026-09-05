// The console window: the grid it hands you, the black-background override, and the Power menu.
//
// The size checks read xterm's own cols and rows rather than measuring pixels, because that is
// the thing being promised - a terminal is entitled to 80x24, and how many pixels that takes
// depends on the font stack that actually resolved.

import { chromium } from 'playwright';

const base = 'http://localhost:5080';
const shots = process.env.SHOTS || '.';
let pass = 0, fail = 0;
const ok = (c, l, x = '') => { console.log(`  ${c ? 'PASS' : 'FAIL'}  ${l}${x ? '   ' + x : ''}`); c ? pass++ : fail++; };
const api = async (path, init) => (await fetch(base + '/api/v1' + path, init)).json();

const b = await chromium.launch();
const p = await (await b.newContext({ viewport: { width: 1500, height: 950 } })).newPage();
p.on('pageerror', e => console.log('  PAGE ERROR:', e.message));

await p.goto(base + '/', { waitUntil: 'networkidle' });
await p.waitForFunction(() => window.Blazor !== undefined);
await p.waitForTimeout(1500);

// A service processor that is still booting would refuse the connection and make this look like
// a bug in the window.
const started = await api('/machines/rp3440/power', {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ action: 'on' }),
});
for (let i = 0; i < 60; i++) {
  const job = await api('/jobs/' + started.jobId);
  if (['Succeeded', 'Failed'].includes(job.status)) { console.log('  (rp3440 power-on: ' + job.status + ')'); break; }
  await p.waitForTimeout(2000);
}

await p.locator('tr', { hasText: 'HP rp3440' }).locator('button', { hasText: /^MP$/ }).first().click();
await p.waitForTimeout(3500);

const win = p.locator('.window-layer .cde-window').first();
const termId = await p.locator('.terminal-host').first().getAttribute('id');
const grid = () => p.evaluate(id => window.syscmdConsole.size(id), termId);

console.log('\n[the console is never smaller than a terminal]');

let g = await grid();
ok(g.cols >= 80 && g.rows >= 24, 'a console opens at 80x24 or better', `${g.cols}x${g.rows}`);

// Drag the south-east corner as far in as it will go. The frame declares its own floor, so this
// should stop while the grid is still whole.
const box = await win.boundingBox();
await p.mouse.move(box.x + box.width - 3, box.y + box.height - 3);
await p.mouse.down();
await p.mouse.move(box.x + 80, box.y + 80, { steps: 12 });
await p.mouse.up();
await p.waitForTimeout(900);
const small = await win.boundingBox();
g = await grid();
ok(g.cols >= 80 && g.rows >= 24, 'and cannot be dragged below it',
   `${g.cols}x${g.rows} at ${Math.round(small.width)}x${Math.round(small.height)}`);

console.log('\n[black background override]');

const options = () => win.locator('.cde-menubar > .menu-anchor > button', { hasText: 'Options' });
const blackItem = () => win.locator('.cde-menubar .cde-dropdown button', { hasText: 'Black background' });

await options().click();
await p.waitForTimeout(350);
ok(await blackItem().locator('.menu-toggle').count() === 1, 'it is a toggle, drawn with Motif\'s indicator');
ok(await blackItem().getAttribute('aria-checked') === 'false', 'and starts clear');
await p.screenshot({ path: `${shots}/console-options.png` });

await blackItem().click();
await p.waitForTimeout(700);
const viewport = await p.evaluate(() => {
  const v = document.querySelector('.terminal-host .xterm-viewport');
  return v ? getComputedStyle(v).backgroundColor : null;
});
ok(/rgb\(0,\s*0,\s*0\)/.test(viewport || ''), 'the terminal goes black', viewport);

await options().click();
await p.waitForTimeout(350);
ok(await blackItem().getAttribute('aria-checked') === 'true', 'and the indicator now reads set');
await blackItem().click();
await p.waitForTimeout(600);
ok(!/rgb\(0,\s*0,\s*0\)/.test(await p.evaluate(() => {
  const v = document.querySelector('.terminal-host .xterm-viewport');
  return v ? getComputedStyle(v).backgroundColor : '';
})), 'toggling back returns it to the palette');

console.log('\n[power, through the management processor]');

const power = () => win.locator('.cde-menubar > .menu-anchor > button', { hasText: 'Power' });
const item = label => win.locator('.cde-menubar .cde-dropdown button', { hasText: label });

await power().click();
await p.waitForTimeout(350);
for (const label of ['Power on', 'Power off', 'Reset']) {
  ok(!await item(label).isDisabled(), `${label} is offered on a machine with an MP`);
}
await p.screenshot({ path: `${shots}/console-power.png` });

// Anything that takes power away asks first, and cancelling must queue nothing.
const before = (await api('/jobs')).length;
await item('Reset').click();
await p.waitForTimeout(600);
ok(await p.locator('.cde-dialog').isVisible(), 'Reset asks before acting');
await p.locator('.cde-dialog button', { hasText: 'Cancel' }).click();
await p.waitForTimeout(500);
ok(!await p.locator('.cde-dialog').isVisible(), 'and cancelling closes the dialog');
ok((await api('/jobs')).length === before, 'cancelling queued nothing');

// Close this one before opening the next, so the two windows do not confuse the locators.
await win.locator('.cw-menu-btn').dblclick();
await p.waitForTimeout(800);

console.log('\n[a machine with no management processor]');

// The PDP-11 is in the simulated lab precisely for this: an outlet and a terminal-server line,
// no service processor anywhere. Everything on the Power menu goes through one, so all of it
// has to grey out rather than offer actions that could only fail.
const row = p.locator('tr', { hasText: 'PDP-11/34A' });
ok(await row.locator('button', { hasText: /^MP$/ }).count() === 0, 'it has no MP console to open');
await row.locator('button', { hasText: /^Serial$/ }).first().click();
await p.waitForTimeout(3000);

const serial = p.locator('.window-layer .cde-window').first();
ok(await serial.count() > 0, 'its serial console still opens');
await serial.locator('.cde-menubar > .menu-anchor > button', { hasText: 'Power' }).click();
await p.waitForTimeout(400);
for (const label of ['Power on', 'Power off', 'Reset']) {
  ok(await serial.locator('.cde-menubar .cde-dropdown button', { hasText: label }).isDisabled(),
     `${label} is greyed out without one`);
}
await p.screenshot({ path: `${shots}/console-no-mp.png` });

await b.close();
console.log(`\n${pass}/${pass + fail} checks passed`);
process.exit(fail ? 1 : 0);

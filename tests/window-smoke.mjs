import { chromium } from 'playwright';
const shots = process.env.SHOTS || '.';
let pass = 0, fail = 0;
const ok = (c, l, x = '') => { console.log(`  ${c ? 'PASS' : 'FAIL'}  ${l}${x ? '   ' + x : ''}`); c ? pass++ : fail++; };

const b = await chromium.launch();
const ctx = await b.newContext({ viewport: { width: 1500, height: 950 } });
const p = await ctx.newPage();
p.on('pageerror', e => console.log('  PAGE ERROR:', e.message));

await p.goto('http://localhost:5080/', { waitUntil: 'networkidle' });
await p.waitForFunction(() => window.Blazor !== undefined);
await p.waitForTimeout(1500);

console.log('\n[page windows]');
const first = p.locator('.app-main .cde-window').first();
ok(await first.locator('.cw-menu-btn').isVisible(), 'window-menu box drawn');
ok(await first.locator('.cw-min').isVisible(), 'minimise box drawn');

// Roll up / unroll a page window.
const content = first.locator('> .window-content');
ok(await content.isVisible(), 'client area visible before roll-up');
await first.locator('.cw-min').click();
await p.waitForTimeout(300);
ok(!await content.isVisible(), 'minimise box rolls the window up');
await first.locator('.cw-min').click();
await p.waitForTimeout(300);
ok(await content.isVisible(), 'minimise box unrolls it');

// Window menu with Close disabled for a page window.
await first.locator('.cw-menu-btn').click();
await p.waitForTimeout(300);
const wm = first.locator('.cde-dropdown').first();
ok(await wm.isVisible(), 'window menu opens');
ok(await wm.locator('button', { hasText: 'Close' }).isDisabled(), 'Close disabled where there is nothing to close');
await p.mouse.click(1400, 900);
await p.waitForTimeout(300);
ok(!await wm.isVisible(), 'clicking away dismisses the window menu');

console.log('\n[console window]');

// Bring the machine up first; a service processor that is still booting would refuse the
// connection and make this look like a bug in the window.
const api = async (path, init) =>
  (await fetch('http://localhost:5080/api/v1' + path, init)).json();
const started = await api('/machines/rp3440/power', {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ action: 'on' })
});
for (let i = 0; i < 60; i++) {
  const job = await api('/jobs/' + started.jobId);
  if (['Succeeded', 'Failed'].includes(job.status)) {
    console.log('  (rp3440 power-on: ' + job.status + ')');
    break;
  }
  await p.waitForTimeout(2000);
}

await p.locator('tr', { hasText: 'HP rp3440' }).locator('button', { hasText: /^MP$/ }).first().click();
await p.waitForTimeout(3500);

const win = p.locator('.window-layer .cde-window').first();
ok(await win.count() > 0 && await win.isVisible(), 'console opens as a window on the page');
const text = await p.locator('.terminal-host').first().innerText();
ok(/MP Host Name|login:/i.test(text), 'terminal is connected to the MP', text.slice(0, 46).replace(/\s+/g, ' '));

// Menus do something.
await win.locator('.cde-menubar button', { hasText: 'Send' }).click();
await p.waitForTimeout(300);
const sendMenu = win.locator('.cde-menubar .cde-dropdown');
ok(await sendMenu.isVisible(), 'Send menu opens');
const items = await sendMenu.locator('button').allInnerTexts();
ok(items.some(i => /MP main menu/.test(i)), 'Send menu carries real control keys', items.length + ' items');
await sendMenu.locator('button', { hasText: 'Return' }).click();
await p.waitForTimeout(800);
ok(!await sendMenu.isVisible(), 'menu closes after choosing an item');

// Drag by the title bar.
const before = await win.boundingBox();
await p.mouse.move(before.x + before.width / 2, before.y + 10);
await p.mouse.down();
await p.mouse.move(before.x + before.width / 2 + 160, before.y + 120, { steps: 12 });
await p.mouse.up();
await p.waitForTimeout(400);
const after = await win.boundingBox();
ok(Math.abs(after.x - before.x - 160) < 12 && Math.abs(after.y - before.y - 110) < 12,
   'title bar drags the window', `moved ${Math.round(after.x - before.x)},${Math.round(after.y - before.y)}`);

// dtwm splits the resize border into eight pieces, and each one resizes from its own side.
ok(await win.locator('.cw-handle').count() === 8, 'the frame has all eight resize pieces');

// Resize by the south-east corner. The corner handles are L-shaped, so the grab has to be on an
// arm: the middle of the handle's bounding box is clipped away and belongs to the client area.
const g = await win.locator('.cw-grip').boundingBox();
await p.mouse.move(g.x + g.width - 3, g.y + g.height - 3);
await p.mouse.down();
await p.mouse.move(g.x + g.width - 3 + 130, g.y + g.height - 3 + 90, { steps: 10 });
await p.mouse.up();
await p.waitForTimeout(400);
const resized = await win.boundingBox();
ok(resized.width > after.width + 100 && resized.height > after.height + 60,
   'south-east corner resizes the window', `${Math.round(resized.width)}x${Math.round(resized.height)}`);

// Dragging a west edge has to move the left edge as well as the width, or the window would grow
// away from the pointer instead of following it.
const w = await win.locator('.cw-h-w').boundingBox();
await p.mouse.move(w.x + 2, w.y + w.height / 2);
await p.mouse.down();
await p.mouse.move(w.x + 2 - 90, w.y + w.height / 2, { steps: 10 });
await p.mouse.up();
await p.waitForTimeout(400);
const widened = await win.boundingBox();
ok(Math.abs(widened.x - (resized.x - 90)) < 12 && widened.width > resized.width + 70,
   'west edge resizes from the left', `x ${Math.round(widened.x)} w ${Math.round(widened.width)}`);

await p.screenshot({ path: `${shots}/window-moved.png` });

// Maximise, then restore.
await win.locator('.cw-max').click();
await p.waitForTimeout(600);
const max = await win.boundingBox();
ok(max.width > 1400, 'maximise box fills the viewport', `${Math.round(max.width)}px`);
await p.screenshot({ path: `${shots}/window-max.png` });
await win.locator('.cw-max').click();
await p.waitForTimeout(500);
ok((await win.boundingBox()).width < 1200, 'maximise box restores');

// Session survives navigation.
await p.locator('.app-nav a[href="/jobs"]').click();
await p.waitForTimeout(1500);
ok(await p.locator('.window-layer .cde-window').count() === 1, 'console survives navigating to another page');
ok(/MP Host Name|login:|MP>/i.test(await p.locator('.terminal-host').first().innerText()),
   'terminal still holds its session');
await p.screenshot({ path: `${shots}/window-survives-nav.png` });

// Close via the window menu.
await p.locator('.window-layer .cw-menu-btn').click();
await p.waitForTimeout(300);
await p.locator('.window-layer .cde-dropdown button', { hasText: 'Close' }).click();
await p.waitForTimeout(800);
ok(await p.locator('.window-layer .cde-window').count() === 0, 'window menu closes the window');

// And the endpoint is released again.
const evts = await (await fetch('http://localhost:5080/api/v1/events?limit=20')).json();
ok(evts.some(e => /Console closed on HP rp3440/.test(e.message)), 'closing released the console endpoint');

await b.close();
console.log(`\n${pass}/${pass + fail} checks passed`);
process.exit(fail ? 1 : 0);

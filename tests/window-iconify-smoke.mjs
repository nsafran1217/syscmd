import { chromium } from 'playwright';
const shots = process.env.SHOTS || '.';
let pass = 0, fail = 0;
const ok = (c, l, x = '') => { console.log(`  ${c ? 'PASS' : 'FAIL'}  ${l}${x ? '   ' + x : ''}`); c ? pass++ : fail++; };

const api = async (path, init) => (await fetch('http://localhost:5080/api/v1' + path, init)).json();

// The simulated MP only answers with its machine's outlet on, as a real one would.
const j = await api('/machines/rp3440/power', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"action":"on"}' });
for (let i = 0; i < 40; i++) {
  const s = await api('/jobs/' + j.jobId);
  if (['Succeeded', 'Failed'].includes(s.status)) break;
  await new Promise(r => setTimeout(r, 1500));
}

const b = await chromium.launch();
const ctx = await b.newContext({ viewport: { width: 1400, height: 800 } });
const p = await ctx.newPage();
p.on('pageerror', e => console.log('  PAGE ERROR:', e.message));

await p.goto('http://localhost:5080/', { waitUntil: 'networkidle' });
await p.waitForFunction(() => window.Blazor !== undefined);
await p.waitForTimeout(1400);

// The navigation starts as a column on a screen this wide unless a previous run hid it.
const shell = p.locator('.app-shell');
if (await shell.evaluate(el => el.classList.contains('nav-closed'))) {
  await p.locator('.hamburger').click();
  await p.waitForTimeout(400);
}

await p.locator('tr', { hasText: 'HP rp3440' }).locator('button', { hasText: /^MP$/ }).first().click();
await p.waitForTimeout(3000);
await p.evaluate(() => window.scrollTo(0, 0));

const win = p.locator('.window-layer .cde-window').first();
const termId = await win.locator('.terminal-host').getAttribute('id');
const menu = async (name) => {
  await win.locator('.cde-menubar > .menu-anchor > button', { hasText: name }).click();
  await p.waitForTimeout(300);
  return (await win.locator('.cde-menubar .cde-dropdown button').allInnerTexts()).map(t => t.trim().replace(/\s+/g, ' '));
};

console.log('\n[the wheel stays inside the terminal]');
{
  const t = await win.locator('.terminal-host').boundingBox();
  await p.mouse.move(t.x + t.width / 2, t.y + t.height / 2);
  const before = await p.evaluate(() => window.scrollY);
  await p.mouse.wheel(0, 800);
  await p.waitForTimeout(300);
  await p.mouse.wheel(0, -800);
  await p.waitForTimeout(300);
  ok(await p.evaluate(() => window.scrollY) === before, 'scrolling past the end of the scrollback leaves the page alone');
}

console.log('\n[custom Send keys come from app.yaml]');
{
  const items = await menu('Send');
  ok(items.includes('Ctrl-T ^T'), 'consoles.sendKeys adds Ctrl-T to the Send menu', items.join(' | '));
  await win.locator('.cde-menubar .cde-dropdown button', { hasText: 'Ctrl-T' }).click();
  await p.waitForTimeout(300);
  ok(await win.locator('.cde-menubar .cde-dropdown').count() === 0, 'choosing it closes the menu');
}

console.log('\n[the minimise box iconifies under the navigation]');
{
  ok(await win.locator('.cw-min').getAttribute('title') === 'Minimize', 'the box says Minimize, not Roll up');
  await win.locator('.cw-min').click();
  await p.waitForTimeout(500);
  ok(!await win.isVisible(), 'the window leaves the screen');
  const connected = await p.evaluate(id => window.syscmdConsole.isConnected(id), termId);
  ok(connected, 'its session stays connected',
     connected ? '' : (await win.locator('.xterm-rows').innerText()).replace(/\s+/g, ' ').slice(-160));

  const nav = await p.locator('.app-nav').boundingBox();
  const tray = await p.locator('.icon-tray').boundingBox();
  ok(!!tray && Math.abs(tray.x - nav.x) < 2 && tray.y >= nav.y + nav.height && tray.y - (nav.y + nav.height) < 20,
     'its icon sits directly under the navigation panel', tray ? `${Math.round(tray.x)},${Math.round(tray.y)}` : 'no tray');
  ok((await p.locator('.cde-icon').innerText()).includes('HP rp3440 MP'), 'the icon is labelled with the machine');
  await p.screenshot({ path: `${shots}/iconified.png` });

  await p.locator('.hamburger').click();
  await p.waitForTimeout(400);
  const corner = await p.locator('.icon-tray').boundingBox();
  ok(!!corner && corner.x < 20 && corner.y + corner.height > 780,
     'with the navigation hidden the icons go to the bottom-left corner',
     corner ? `${Math.round(corner.x)},${Math.round(corner.y)}` : 'no tray');
  await p.locator('.hamburger').click();
  await p.waitForTimeout(400);

  await p.locator('.cde-icon').first().click();
  await p.waitForTimeout(800);
  ok(await win.isVisible(), 'clicking the icon restores the window');
  ok(await p.locator('.icon-tray').count() === 0, 'and the icon goes');
  const size = await p.evaluate(id => window.syscmdConsole.size(id), termId);
  ok(size && size.cols >= 80 && size.rows >= 24, 'the terminal refits to at least 80x24', size ? `${size.cols}x${size.rows}` : '');
  ok(!await win.evaluate(el => el.classList.contains('inactive')), 'the restored window has focus');
}

console.log('\n[pop out to a window of its own]');
{
  const items = await menu('Window');
  ok(items.some(i => i.startsWith('Pop out')), 'the Window menu offers Pop out', items.join(' | '));
  const [popup] = await Promise.all([
    ctx.waitForEvent('page'),
    win.locator('.cde-menubar .cde-dropdown button', { hasText: 'Pop out' }).click(),
  ]);
  await popup.waitForLoadState('networkidle');
  await popup.waitForTimeout(3000);

  ok(await p.locator('.window-layer .cde-window').count() === 0, 'the in-page window closes');
  ok(await popup.title() === 'HP rp3440 - management processor', 'the pop-out is titled for its console', await popup.title());
  const grid = await popup.evaluate(() => window.syscmdConsole.size(document.querySelector('.terminal-host').id));
  ok(grid && grid.cols >= 80 && grid.rows >= 24, 'the pop-out terminal is at least 80x24', grid ? `${grid.cols}x${grid.rows}` : '');
  const text = await popup.locator('.xterm-rows').innerText();
  ok(/Connected to/.test(text) && !/in use/i.test(text), 'it takes over the session without finding the port busy');
  ok(await popup.locator('.cw-min, .cw-max, .cw-handle').count() === 0, 'it has no boxes or handles of its own to fight the window manager');
  await popup.screenshot({ path: `${shots}/popout.png` });

  await popup.locator('.cde-menubar > .menu-anchor > button', { hasText: 'Window' }).click();
  await popup.waitForTimeout(300);
  const closed = popup.waitForEvent('close');
  await popup.locator('.cde-menubar .cde-dropdown button', { hasText: 'Close' }).click();
  await closed;
  ok(popup.isClosed(), 'Close shuts the pop-out window');
}

await b.close();
console.log(`\n${pass}/${pass + fail} checks passed`);
process.exit(fail ? 1 : 0);

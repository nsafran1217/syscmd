// Layering inside a window while a pull-down is open. The catcher that dismisses a menu has
// to sit above the client area but below the chrome; get that wrong and the second half of a
// double-click on the window-menu box lands on the catcher, so the window never closes.
import { chromium } from 'playwright';

let pass = 0, fail = 0;
const ok = (c, l, x = '') => { console.log(`  ${c ? 'PASS' : 'FAIL'}  ${l}${x ? '   ' + x : ''}`); c ? pass++ : fail++; };
const api = async (path, init) => (await fetch('http://localhost:5080/api/v1' + path, init)).json();
const sleep = ms => new Promise(r => setTimeout(r, ms));

const b = await chromium.launch();
const p = await (await b.newContext({ viewport: { width: 1500, height: 950 } })).newPage();
p.on('pageerror', e => console.log('  PAGE ERROR:', e.message));

// The console needs a management processor that answers.
const j = await api('/machines/rp3440/power', {
  method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"action":"on"}'
});
for (let i = 0; i < 40; i++) {
  const s = await api('/jobs/' + j.jobId);
  if (['Succeeded', 'Failed'].includes(s.status)) break;
  await sleep(1500);
}

await p.goto('http://localhost:5080/', { waitUntil: 'networkidle' });
await p.waitForFunction(() => window.Blazor !== undefined);
await p.waitForTimeout(1400);

const openConsole = async () => {
  await p.locator('tr', { hasText: 'HP rp3440' }).locator('button', { hasText: /^MP$/ }).first().click();
  await p.waitForTimeout(2500);
};

console.log('\n[double-click the window-menu box closes a console window]');
// Repeated because the bug this covers was intermittent: it only bit when the catcher had
// rendered between the two halves of the double-click.
for (let attempt = 1; attempt <= 3; attempt++) {
  await openConsole();
  if (await p.locator('.window-layer .cde-window').count() !== 1) {
    ok(false, `attempt ${attempt}: window did not open`);
    break;
  }
  await p.locator('.window-layer .cw-menu-btn').dblclick();
  await p.waitForTimeout(700);
  const left = await p.locator('.window-layer .cde-window').count();
  ok(left === 0, `attempt ${attempt}: one double-click closes it`, `${left} window(s) left`);

  if (left > 0) {
    // Clean up so the next attempt starts from a known state.
    await p.locator('.window-layer .cw-menu-btn').click();
    await p.waitForTimeout(300);
    await p.locator('.window-layer .cde-dropdown button', { hasText: 'Close' }).click();
    await p.waitForTimeout(500);
  }
}

console.log('\n[the chrome stays usable while a pull-down is open]');
await openConsole();
const win = p.locator('.window-layer .cde-window').first();

await win.locator('.cde-menubar > .menu-anchor > button', { hasText: 'Window' }).click();
await p.waitForTimeout(350);
ok(await win.locator('.cde-menubar .cde-dropdown').isVisible(), 'Window menu opens');

// Switching straight to another menu should work, not merely dismiss the first.
await win.locator('.cde-menubar > .menu-anchor > button', { hasText: 'Edit' }).click();
await p.waitForTimeout(350);
const items = await win.locator('.cde-menubar .cde-dropdown button').allInnerTexts();
ok(items.some(i => /Copy/.test(i)), 'clicking another bar entry switches menus',
   items.map(i => i.trim().replace(/\s+/g, ' ')).join(' | '));

// Clicking over the terminal still dismisses. A raw pointer click, because the catcher is
// deliberately above the terminal and is the element that should receive it.
const term = await p.locator('.terminal-host').boundingBox();
await p.mouse.click(term.x + term.width - 30, term.y + term.height - 30);
await p.waitForTimeout(400);
ok(await win.locator('.cde-menubar .cde-dropdown').count() === 0, 'clicking away still dismisses');

// A title-bar box has to keep working with a menu open.
await win.locator('.cde-menubar > .menu-anchor > button', { hasText: 'Send' }).click();
await p.waitForTimeout(350);
await win.locator('.cw-min').click();
await p.waitForTimeout(400);
ok(!await win.locator('> .window-content').isVisible(),
   'the minimise box works while a menu is open');

await b.close();
console.log(`\n${pass}/${pass + fail} checks passed`);
process.exit(fail ? 1 : 0);

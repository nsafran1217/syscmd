import { chromium } from 'playwright';
const shots = process.env.SHOTS || '.';
let pass = 0, fail = 0;
const ok = (c, l, x = '') => { console.log(`  ${c ? 'PASS' : 'FAIL'}  ${l}${x ? '   ' + x : ''}`); c ? pass++ : fail++; };
const api = async (path, init) => (await fetch('http://localhost:5080/api/v1' + path, init)).json();
const sleep = ms => new Promise(r => setTimeout(r, ms));

const powerOn = async (p, id) => {
  const j = await api(`/machines/${id}/power`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"action":"on"}' });
  for (let i = 0; i < 40; i++) { const s = await api('/jobs/' + j.jobId); if (['Succeeded','Failed'].includes(s.status)) return s.status; await sleep(1500); }
  return 'timeout';
};

const b = await chromium.launch();
const p = await (await b.newContext({ viewport: { width: 1500, height: 950 } })).newPage();
p.on('pageerror', e => console.log('  PAGE ERROR:', e.message));

console.log('  (bringing machines up)');
console.log('  rp3440:', await powerOn(p, 'rp3440'), ' ultra10:', await powerOn(p, 'ultra10'));

await p.goto('http://localhost:5080/', { waitUntil: 'networkidle' });
await p.waitForFunction(() => window.Blazor !== undefined);
await p.waitForTimeout(1500);

console.log('\n[HP MP console]');
await p.locator('tr', { hasText: 'HP rp3440' }).locator('button', { hasText: /^MP$/ }).first().click();
await p.waitForTimeout(3500);
const win = p.locator('.window-layer .cde-window').first();
const termText = async () => (await p.locator('.terminal-host').first().innerText()).replace(/\s+/g, ' ');

ok(await win.locator('.cde-menubar > .menu-anchor > button', { hasText: /^Login$/ }).isVisible(),
   'Login sits on the menu bar of an MP console',
   (await win.locator('.cde-menubar > .menu-anchor > button').allInnerTexts()).map(t => t.trim()).join(' | '));
const before = await termText();
ok(/login:/i.test(before) && !/MP>/.test(before), 'sitting at the login prompt', before.slice(-50));

await win.locator('.cde-menubar > .menu-anchor > button', { hasText: /^Login$/ }).click();
await p.waitForTimeout(4000);
const after = await termText();
ok(/MP>/.test(after), 'reaches the MP prompt without typing', after.slice(-70));
ok(/Login sent/.test(after), 'reports that it sent the login');
ok(await win.locator('.cde-menubar .cde-dropdown').count() === 0,
   'the bar entry acts rather than opening a pull-down');

// The accelerator hint promised a key that closes the browser, not the window.
const dismiss = async () => {
  await p.locator('.cde-overlay').first().click({ position: { x: 4, y: 4 } }).catch(() => {});
  await p.waitForTimeout(350);
};

await win.locator('.cw-menu-btn').click();
await p.waitForTimeout(350);
const closeText = await win.locator('.cde-dropdown button', { hasText: 'Close' }).innerText();
ok(!/Alt\+F4/i.test(closeText), 'no Alt+F4 hint on the window menu', `"${closeText.trim()}"`);
await dismiss();

await win.locator('.cde-menubar > .menu-anchor > button', { hasText: 'Window' }).click();
await p.waitForTimeout(350);
const winMenuClose = await win.locator('.cde-menubar .cde-dropdown button', { hasText: 'Close' }).innerText();
ok(!/Alt\+F4/i.test(winMenuClose), 'nor on the Window menu', `"${winMenuClose.trim()}"`);
await dismiss();
await p.screenshot({ path: `${shots}/login-mp.png` });

// The session must still be usable afterwards.
await win.locator('.cde-menubar button', { hasText: 'Send' }).click();
await p.waitForTimeout(300);
await p.locator('.cde-menubar .cde-dropdown button', { hasText: 'Return' }).click();
await p.waitForTimeout(1200);
ok(/MP>/.test(await termText()), 'the session still responds after logging in');

console.log('\n[serial console has no Login]');
await win.locator('.cw-menu-btn').click();
await p.waitForTimeout(300);
await p.locator('.window-layer .cde-dropdown button', { hasText: 'Close' }).click();
await p.waitForTimeout(600);

await p.locator('tr', { hasText: 'HP rp3440' }).locator('button', { hasText: /^Serial$/ }).first().click();
await p.waitForTimeout(3000);
const serialWin = p.locator('.window-layer .cde-window').first();
ok(await serialWin.locator('.cde-menubar > .menu-anchor > button', { hasText: /^Login$/ }).count() === 0,
   'no Login entry on a serial console',
   (await serialWin.locator('.cde-menubar > .menu-anchor > button').allInnerTexts()).map(t => t.trim()).join(' | '));
await serialWin.locator('.cw-menu-btn').click();
await p.waitForTimeout(300);
await p.locator('.window-layer .cde-dropdown button', { hasText: 'Close' }).click();
await p.waitForTimeout(600);

console.log('\n[ALOM uses its own login script]');
await p.locator('tr', { hasText: 'Sun Ultra 10' }).locator('button', { hasText: /^MP$/ }).first().click();
await p.waitForTimeout(3500);
const alom = p.locator('.window-layer .cde-window').first();
await alom.locator('.cde-menubar > .menu-anchor > button', { hasText: /^Login$/ }).click();
await p.waitForTimeout(4000);
const alomText = await termText();
ok(/sc>/.test(alomText), 'reaches the ALOM prompt too', alomText.slice(-70));
await p.screenshot({ path: `${shots}/login-alom.png` });

await b.close();
console.log(`\n${pass}/${pass + fail} checks passed`);
process.exit(fail ? 1 : 0);

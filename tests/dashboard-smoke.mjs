import { chromium } from 'playwright';
const shots = process.env.SHOTS || '.';
let pass = 0, fail = 0;
const ok = (c, l, x = '') => { console.log(`  ${c ? 'PASS' : 'FAIL'}  ${l}${x ? '   ' + x : ''}`); c ? pass++ : fail++; };
const api = async (path, init) => (await fetch('http://localhost:5080/api/v1' + path, init)).json();
const sleep = ms => new Promise(r => setTimeout(r, ms));
const ready = async p => { await p.waitForFunction(() => window.Blazor !== undefined); await p.waitForTimeout(1300); };

const b = await chromium.launch();
const ctx = await b.newContext({ viewport: { width: 1500, height: 1000 } });
const p = await ctx.newPage();
p.on('pageerror', e => console.log('  PAGE ERROR:', e.message));

// Start from everything off so outlet state is predictable.
for (const n of [1, 2, 3, 4, 5]) {
  await api(`/pdus/sim-pdu/outlets/${n}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"action":"off","force":true}' });
}
await sleep(3000);

await p.goto('http://localhost:5080/', { waitUntil: 'networkidle' });
await ready(p);

console.log('\n[hide unassigned outlets]');
ok(await p.locator('.outlet-cell').count() === 5, 'only assigned outlets shown by default',
   `${await p.locator('.outlet-cell').count()} of 8`);
ok(await p.locator('.outlet-cell', { hasText: 'unassigned' }).count() === 0, 'no unassigned outlets visible');
const note = await p.locator('text=/unassigned outlets are hidden/').first().innerText().catch(() => '');
ok(/3 unassigned/.test(note), 'says how many are hidden', note.trim());

const hideToggle = p.locator('.inline-toggle', { hasText: 'Hide unassigned' }).locator('input');
ok(await hideToggle.isChecked(), 'toggle is on by default');
await hideToggle.uncheck();
await sleep(700);
ok(await p.locator('.outlet-cell').count() === 8, 'unchecking reveals all eight');
await p.reload({ waitUntil: 'networkidle' });
await ready(p);
ok(!await p.locator('.inline-toggle', { hasText: 'Hide unassigned' }).locator('input').isChecked(),
   'the choice is remembered');
await p.locator('.inline-toggle', { hasText: 'Hide unassigned' }).locator('input').check();
await sleep(700);

console.log('\n[outlet click switches the outlet only]');
const clickedAt = Date.now();
const before = await api('/jobs?limit=5');
await p.locator('.outlet-cell', { hasText: 'HP rp3440' }).locator('.outlet').click();
await sleep(1500);
const after = await api('/jobs?limit=8');
const fresh = after.find(j => !before.some(x => x.id === j.id));
ok(!!fresh, 'clicking an off outlet started a job');
ok(/outlet only/i.test(fresh.title), 'it is an outlet-only job, not an MP power-on', fresh.title);
ok(fresh.kind === 'OutletControl', 'job kind is OutletControl', fresh.kind);

// Let it finish before reading the outlet back.
let done = fresh;
for (let i = 0; i < 20; i++) {
  done = await api('/jobs/' + fresh.id);
  if (['Succeeded', 'Failed', 'Cancelled'].includes(done.status)) break;
  await sleep(700);
}
ok(done.status === 'Succeeded', 'the outlet-only job succeeded', done.status + ' ' + (done.error || ''));
const st = (await api('/pdus/sim-pdu/outlets')).find(o => o.outlet === 1).state;
ok(st === 'On', 'the outlet is now on', st);

// The MP itself should not have been driven. Only look at events since the click: the log
// holds the whole session, including MP work from earlier checks.
const evts = await api('/events?limit=80&level=Debug&machine=rp3440');
const sinceClick = evts.filter(e => Date.parse(e.timestamp) >= clickedAt - 1000);
ok(!sinceClick.some(e => /poweron on HP rp3440/i.test(e.message)),
   'no MP power-on sequence was run',
   `${sinceClick.length} events since the click`);

console.log('\n[MP power-on is on the menu]');
await p.locator('.outlet-cell', { hasText: 'HP rp3440' }).locator('.outlet-menu-btn').click();
await sleep(500);
const items = await p.locator('.cde-contextmenu button').allInnerTexts();
ok(items.some(i => /Power on system via management processor/i.test(i)),
   'menu offers the MP power-on', items.map(i => i.trim()).filter(Boolean).join(' | ').slice(0, 120));
await p.screenshot({ path: `${shots}/new-menu.png` });
await p.keyboard.press('Escape').catch(() => {});
await p.locator('.cde-overlay').first().click({ position: { x: 5, y: 5 } }).catch(() => {});
await sleep(400);

console.log('\n[machines list]');
const heads = (await p.locator('.cde-table').last().locator('th').allInnerTexts()).map(h => h.trim());
const upper = heads.map(h => h.toUpperCase());
ok(!upper.includes('MP') && !upper.includes('OUTLET'), 'MP and Outlet columns removed', heads.join(' | '));
ok(upper.includes('POWER'), 'Power column added');

const row = p.locator('tr', { hasText: 'Sun Ultra 10' });
for (const label of ['On', 'Off', 'Reset']) {
  ok(await row.locator('td').nth(3).locator('button', { hasText: new RegExp(`^${label}$`) }).isVisible(),
     `${label} button present`);
}
const vaxReset = p.locator('tr', { hasText: 'VAXstation 3100' }).locator('td').nth(3).locator('button', { hasText: /^Reset$/ });
ok(await vaxReset.isDisabled(), 'Reset disabled for a machine with no management processor');

// The list's On button should run the full MP sequence.
const beforeM = await api('/jobs?limit=5');
await p.locator('tr', { hasText: 'HP rx2660' }).locator('td').nth(3).locator('button', { hasText: /^On$/ }).click();
await sleep(2500);
const afterM = await api('/jobs?limit=8');
const mJob = afterM.find(j => !beforeM.some(x => x.id === j.id));
ok(!!mJob && mJob.kind === 'MachinePower', 'machine list On runs the machine power flow',
   mJob ? `${mJob.kind}: ${mJob.title}` : 'no job');
await p.screenshot({ path: `${shots}/new-machines.png` });

console.log('\n[outlet buttons keep their size while busy]');
{
  // A narrow grid track plus a long progress line is what used to push a button out
  // over its neighbour.
  const narrow = await b.newContext({ viewport: { width: 1100, height: 1000 } });
  const np = await narrow.newPage();
  await np.goto('http://localhost:5080/', { waitUntil: 'networkidle' });
  await ready(np);

  await api('/pdus/sim-pdu/outlets/4', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"action":"off","force":true}' });
  await sleep(1200);
  await api('/machines/alpha1000/power', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"action":"on"}' });
  await sleep(2500);

  const cells = await np.$$eval('.outlet-cell', els => els.map(c => {
    const btn = c.querySelector('.outlet');
    const cb = c.getBoundingClientRect(), bb = btn.getBoundingClientRect();
    return {
      name: c.querySelector('.outlet-name').textContent.trim(),
      status: c.querySelector('.outlet-sub').textContent.trim(),
      cellW: Math.round(cb.width), btnW: Math.round(bb.width),
      cellH: Math.round(cb.height), overflow: Math.round(bb.right - cb.right)
    };
  }));

  const busy = cells.find(c => !/^(hp-mp|sun-alom|outlet only|unassigned)$/.test(c.status));
  ok(!!busy, 'a status message is showing', busy ? `"${busy.status}"` : 'none');
  ok(cells.every(c => c.overflow <= 0), 'no button spills past its cell',
     cells.map(c => c.overflow).join(','));
  ok(new Set(cells.map(c => c.btnW)).size === 1, 'every button is the same width',
     [...new Set(cells.map(c => c.btnW))].join(', ') + 'px');
  ok(new Set(cells.map(c => c.cellH)).size === 1, 'every button is the same height',
     [...new Set(cells.map(c => c.cellH))].join(', ') + 'px');
  await np.screenshot({ path: `${shots}/outlet-sizing.png` });
  await narrow.close();
}

console.log('\n[first address is labelled primary]');
await p.goto('http://localhost:5080/config/machines', { waitUntil: 'networkidle' });
await ready(p);
await p.locator('button', { hasText: 'New machine' }).click();
await sleep(700);
await p.locator('button', { hasText: 'Add address' }).click();
await sleep(500);
let labels = p.locator('label:has-text("Label") input');
ok(await labels.first().inputValue() === 'primary', 'first address is labelled primary',
   await labels.first().inputValue());
await p.locator('button', { hasText: 'Add address' }).click();
await sleep(500);
labels = p.locator('label:has-text("Label") input');
ok(await labels.nth(1).inputValue() === '', 'the second is left blank', `"${await labels.nth(1).inputValue()}"`);

await b.close();
console.log(`\n${pass}/${pass + fail} checks passed`);
process.exit(fail ? 1 : 0);

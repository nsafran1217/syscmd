import { chromium, devices } from 'playwright';
const shots = process.env.SHOTS || '.';
let pass = 0, fail = 0;
const ok = (c, l, x = '') => { console.log(`  ${c ? 'PASS' : 'FAIL'}  ${l}${x ? '   ' + x : ''}`); c ? pass++ : fail++; };
const api = async (path, init) => (await fetch('http://localhost:5080/api/v1' + path, init)).json();
const ready = async (p) => { await p.waitForFunction(() => window.Blazor !== undefined); await p.waitForTimeout(1300); };

const b = await chromium.launch();

console.log('\n[navigation]');
{
  const ctx = await b.newContext({ viewport: { width: 1500, height: 950 } });
  const p = await ctx.newPage();
  p.on('pageerror', e => console.log('  PAGE ERROR:', e.message));
  await p.goto('http://localhost:5080/', { waitUntil: 'networkidle' });
  await ready(p);

  const links = await p.locator('.app-nav a').allInnerTexts();
  ok(!links.some(l => /YAML Files/i.test(l)), 'YAML Files removed from the navigation');
  ok(!links.some(l => /Simulated Lab/i.test(l)), 'duplicate site button removed');
  ok(links.some(l => /Dashboard/.test(l)), 'Dashboard still there', links.map(l => l.trim()).join(' | '));

  // Hamburger pinned to the viewport's top-left corner.
  const h = await p.locator('.app-topbar .hamburger').boundingBox();
  ok(h.x < 20 && h.y < 20, 'hamburger pinned top-left', `x=${Math.round(h.x)} y=${Math.round(h.y)}`);
  // Measure the layout before scrolling; a scrolled page moves the nav out of view.
  const nav = await p.locator('.app-nav').boundingBox();
  const main = await p.locator('.app-main').boundingBox();

  await p.mouse.wheel(0, 800);
  await p.waitForTimeout(400);
  const h2 = await p.locator('.app-topbar .hamburger').boundingBox();
  ok(h2.x < 20 && h2.y < 20, 'stays pinned when the page scrolls', `y=${Math.round(h2.y)}`);
  await p.mouse.wheel(0, -800);
  await p.waitForTimeout(300);
  ok(nav.x + nav.width <= main.x + 2, 'desktop nav is still a column beside the content');
  ok(nav.y >= h.y + h.height - 2, 'nav sits below the pinned strip, not under it',
     `nav y=${Math.round(nav.y)}, strip ends ${Math.round(h.y + h.height)}`);
  await p.screenshot({ path: `${shots}/chg-desktop.png` });
  await ctx.close();
}

console.log('\n[mobile nav on top]');
{
  const ctx = await b.newContext({ ...devices['iPhone 13'] });
  const p = await ctx.newPage();
  await p.goto('http://localhost:5080/', { waitUntil: 'networkidle' });
  await ready(p);
  await p.locator('.app-topbar .hamburger').tap();
  await p.waitForTimeout(500);

  const navBox = await p.locator('.app-nav').boundingBox();
  const strip = await p.locator('.app-topbar').boundingBox();

  // The drawer starts below the pinned strip, so the toggle that opened it stays usable.
  ok(navBox.y >= strip.height - 2, 'drawer starts below the pinned strip',
     `nav y=${Math.round(navBox.y)}, strip ${Math.round(strip.height)}px`);

  const hitsHamburger = await p.evaluate(() => {
    const b = document.querySelector('.app-topbar .hamburger').getBoundingClientRect();
    const el = document.elementFromPoint(b.x + b.width / 2, b.y + b.height / 2);
    return !!el && !!el.closest('.hamburger');
  });
  ok(hitsHamburger, 'hamburger is not covered by the open drawer');

  // It still overlays the page content rather than pushing it aside.
  const overlays = await p.evaluate(() => {
    const el = document.elementFromPoint(20, 300);
    return !!el && !!el.closest('.app-nav');
  });
  ok(overlays, 'nav menu sits on top of the page content');

  // And the toggle really closes it again.
  await p.locator('.app-topbar .hamburger').tap();
  await p.waitForTimeout(500);
  ok(!await p.locator('.app-nav').isVisible(), 'the pinned toggle closes the drawer');
  await p.locator('.app-topbar .hamburger').tap();
  await p.waitForTimeout(400);
  await p.screenshot({ path: `${shots}/chg-mobile-nav.png` });
  await ctx.close();
}

console.log('\n[skip-confirmation toggle]');
{
  const ctx = await b.newContext({ viewport: { width: 1500, height: 950 } });
  const p = await ctx.newPage();
  await p.goto('http://localhost:5080/', { waitUntil: 'networkidle' });
  await ready(p);

  // Make sure outlet 1 is on so clicking it means "off".
  const j = await api('/pdus/sim-pdu/outlets/1', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"action":"on","force":true}' });
  for (let i = 0; i < 20; i++) { const s = await api('/jobs/' + j.jobId); if (['Succeeded','Failed'].includes(s.status)) break; await p.waitForTimeout(700); }
  await p.waitForTimeout(3500);

  const toggle = p.locator('.inline-toggle', { hasText: 'Skip confirmation' }).locator('input');
  ok(await toggle.isVisible(), 'toggle present in the PDU section');
  ok(!await toggle.isChecked(), 'confirmation is on by default');

  // With it off, a power-off click should raise the dialog.
  await p.locator('.outlet-cell', { hasText: 'HP rp3440' }).locator('.outlet').click();
  await p.waitForTimeout(600);
  ok(await p.locator('.cde-dialog').isVisible(), 'dialog appears by default');
  await p.locator('.cde-dialog button', { hasText: 'Cancel' }).click();
  await p.waitForTimeout(400);

  // Turn it on and try again.
  await toggle.check();
  await p.waitForTimeout(600);
  const label = p.locator('.inline-toggle', { hasText: 'Skip confirmation' }).locator('span').first();
  ok(((await label.getAttribute('class')) || '').includes('warn'),
     'the label itself is highlighted while active', await label.getAttribute('class'));
  await p.locator('.outlet-cell', { hasText: 'HP rp3440' }).locator('.outlet').click();
  await p.waitForTimeout(900);
  ok(!await p.locator('.cde-dialog').isVisible(), 'no dialog once the toggle is set');
  const recent = await api('/jobs?limit=20');
  const started = recent.find(x => /Power off HP rp3440/.test(x.title) &&
                                   Date.now() - Date.parse(x.createdAt) < 20000);
  ok(!!started, 'the power-off was queued straight away',
     started ? `${started.status}` : 'no matching job in the last 20s');
  await p.screenshot({ path: `${shots}/chg-skipconfirm.png` });

  // It should be remembered.
  await p.reload({ waitUntil: 'networkidle' });
  await ready(p);
  ok(await p.locator('.inline-toggle', { hasText: 'Skip confirmation' }).locator('input').isChecked(),
     'toggle is remembered across a reload');
  await ctx.close();
}

console.log('\n[killing a job]');
{
  const ctx = await b.newContext({ viewport: { width: 1400, height: 950 } });
  const p = await ctx.newPage();

  // A power-off on the stubborn machine runs for 90s, so there is something to kill.
  await api('/pdus/sim-pdu/outlets/4', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"action":"on","force":true}' });
  await p.waitForTimeout(12000);
  const on = await api('/machines/alpha1000/power', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"action":"on"}' });
  for (let i = 0; i < 30; i++) { const s = await api('/jobs/' + on.jobId); if (['Succeeded','Failed'].includes(s.status)) break; await p.waitForTimeout(1500); }
  const off = await api('/machines/alpha1000/power', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"action":"off"}' });

  await p.goto('http://localhost:5080/jobs', { waitUntil: 'networkidle' });
  await ready(p);
  await p.waitForTimeout(1500);

  const stop = p.locator('.cde-panel', { hasText: 'Power off AlphaServer 1000' }).locator('button', { hasText: 'Stop' }).first();
  ok(await stop.isVisible(), 'running job offers a Stop button');
  await stop.click();
  await p.waitForTimeout(500);
  ok(await p.locator('.cde-dialog').isVisible(), 'stopping asks first');
  await p.screenshot({ path: `${shots}/chg-killjob.png` });
  await p.locator('.cde-dialog button', { hasText: 'Stop it' }).click();

  let final = null;
  for (let i = 0; i < 20; i++) {
    await p.waitForTimeout(1000);
    final = await api('/jobs/' + off.jobId);
    if (final.status !== 'Running') break;
  }
  ok(final.status === 'Cancelled', 'job reports as Cancelled', final.status + ' - ' + (final.error || ''));

  const outlets = await api('/pdus/sim-pdu/outlets');
  ok(outlets.find(o => o.outlet === 4).state === 'On', 'outlet left ON after the kill, nothing was cut');
  await ctx.close();
}

await b.close();
console.log(`\n${pass}/${pass + fail} checks passed`);
process.exit(fail ? 1 : 0);

import { chromium, devices } from 'playwright';

const URL = 'http://localhost:5080/';
const shots = process.env.SHOTS || '.';
let pass = 0, fail = 0;
const ok = (c, label, extra = '') => {
  console.log(`  ${c ? 'PASS' : 'FAIL'}  ${label}${extra ? '   ' + extra : ''}`);
  c ? pass++ : fail++;
};

// Blazor interactive server needs its circuit up before any click does anything.
async function ready(page) {
  await page.waitForFunction(() => window.Blazor !== undefined, null, { timeout: 15000 });
  await page.waitForTimeout(1200);
}

const browser = await chromium.launch();

// ---------------------------------------------------------------- desktop
{
  console.log('\n[desktop 1600x1000]');
  const ctx = await browser.newContext({ viewport: { width: 1600, height: 1000 } });
  const page = await ctx.newPage();
  await page.goto(URL, { waitUntil: 'networkidle' });
  await ready(page);

  const main = await page.locator('.dashboard-main').boundingBox();
  const side = await page.locator('.dashboard-side').boundingBox();
  // Guard against a collapsed layout satisfying the position checks with zero widths.
  ok(main.width > 500 && side.width > 250, 'both columns have real width',
     `main ${Math.round(main.width)}px, log ${Math.round(side.width)}px`);
  ok(side.x >= main.x + main.width - 1, 'event log is to the RIGHT of the other windows',
     `main ends ${Math.round(main.x + main.width)}, log starts ${Math.round(side.x)}`);
  ok(Math.abs(side.y - main.y) < 60, 'event log is level with the top, not below',
     `main y=${Math.round(main.y)}, log y=${Math.round(side.y)}`);

  const nav = await page.locator('.app-nav').isVisible();
  ok(nav, 'navigation shown by default on desktop');
  ok(await page.locator('.outlet-menu-btn').first().isVisible(), 'outlet menu button is visible');
  ok(!await page.locator('body').evaluate(b => b.scrollWidth > b.clientWidth), 'no horizontal page scroll');

  await page.screenshot({ path: `${shots}/desktop.png`, fullPage: false });

  // Toggle off, then confirm it stays off across a reload.
  await page.locator('.hamburger').click();
  await page.waitForTimeout(400);
  ok(!await page.locator('.app-nav').isVisible(), 'hamburger hides the navigation');
  await page.reload({ waitUntil: 'networkidle' });
  await ready(page);
  ok(!await page.locator('.app-nav').isVisible(), 'stays hidden after a reload');

  await page.locator('.hamburger').click();
  await page.waitForTimeout(400);
  ok(await page.locator('.app-nav').isVisible(), 'hamburger shows it again');
  await page.reload({ waitUntil: 'networkidle' });
  await ready(page);
  ok(await page.locator('.app-nav').isVisible(), 'stays shown after a reload');

  await ctx.close();
}

// ------------------------------------------------------- narrow desktop
{
  console.log('\n[narrow window 1000x900]');
  const ctx = await browser.newContext({ viewport: { width: 1000, height: 900 } });
  const page = await ctx.newPage();
  await page.goto(URL, { waitUntil: 'networkidle' });
  await ready(page);

  const main = await page.locator('.dashboard-main').boundingBox();
  const side = await page.locator('.dashboard-side').boundingBox();
  ok(main.width > 500 && side.width > 500, 'both blocks span the full width',
     `main ${Math.round(main.width)}px, log ${Math.round(side.width)}px`);
  ok(side.y >= main.y + main.height - 1, 'event log drops BELOW when space runs out',
     `main ends y=${Math.round(main.y + main.height)}, log starts y=${Math.round(side.y)}`);
  ok(!await page.locator('body').evaluate(b => b.scrollWidth > b.clientWidth), 'no horizontal page scroll');
  await page.screenshot({ path: `${shots}/narrow.png` });
  await ctx.close();
}

// ----------------------------------------------------------------- phone
{
  console.log('\n[iPhone 13 - 390x844, touch]');
  const ctx = await browser.newContext({ ...devices['iPhone 13'] });
  const page = await ctx.newPage();
  await page.goto(URL, { waitUntil: 'networkidle' });
  await ready(page);

  ok(!await page.locator('.app-nav').isVisible(), 'navigation hidden by default on a phone');
  ok(!await page.locator('body').evaluate(b => b.scrollWidth > b.clientWidth), 'no horizontal page scroll');

  const main = await page.locator('.dashboard-main').boundingBox();
  const side = await page.locator('.dashboard-side').boundingBox();
  ok(side.y > main.y + main.height - 40, 'event log is stacked below');

  await page.screenshot({ path: `${shots}/phone.png` });

  // Hamburger drawer
  await page.locator('.hamburger').tap();
  await page.waitForTimeout(400);
  ok(await page.locator('.app-nav').isVisible(), 'hamburger opens the drawer');
  ok(await page.locator('.nav-backdrop').isVisible(), 'backdrop covers the content');
  await page.screenshot({ path: `${shots}/phone-nav.png` });

  // The drawer's own Close button is the primary way out on a small screen.
  ok(await page.locator('.app-nav .nav-close').isVisible(), 'drawer offers a Close button');
  await page.locator('.app-nav .nav-close').tap();
  await page.waitForTimeout(400);
  ok(!await page.locator('.app-nav').isVisible(), 'Close button shuts the drawer');

  // The exposed strip of backdrop beside the drawer should also dismiss it.
  await page.locator('.hamburger').tap();
  await page.waitForTimeout(400);
  const vpw = page.viewportSize().width;
  await page.locator('.nav-backdrop').tap({ position: { x: vpw - 25, y: 400 } });
  await page.waitForTimeout(400);
  ok(!await page.locator('.app-nav').isVisible(), 'tapping beside the drawer closes it');

  // The forced-action menu, by touch
  await page.locator('.outlet-menu-btn').first().tap();
  await page.waitForTimeout(400);
  const menu = page.locator('.cde-contextmenu');
  ok(await menu.isVisible(), 'outlet menu opens by touch');

  const box = await menu.boundingBox();
  const vp = page.viewportSize();
  ok(box.x >= 0 && box.x + box.width <= vp.width + 1, 'menu fits the screen width',
     `x=${Math.round(box.x)} w=${Math.round(box.width)} vp=${vp.width}`);
  ok(box.y + box.height <= vp.height + 1, 'menu fits on screen vertically');

  const labels = await menu.locator('button').allInnerTexts();
  ok(labels.some(l => /Force outlet off/i.test(l)), 'forced actions reachable on touch',
     labels.filter(Boolean).length + ' items');
  await page.screenshot({ path: `${shots}/phone-menu.png` });

  // Tapping the outlet body should toggle, not open the menu.
  await page.keyboard.press('Escape').catch(() => {});
  await page.locator('.cde-overlay').first().tap({ position: { x: 5, y: 5 } }).catch(() => {});
  await page.waitForTimeout(300);
  ok(!await menu.isVisible(), 'menu dismisses');

  await ctx.close();
}

// ------------------------------------------------------- console on phone
{
  console.log('\n[console window on a phone]');
  const ctx = await browser.newContext({ ...devices['iPhone 13'] });
  const page = await ctx.newPage();
  await page.goto(URL, { waitUntil: 'networkidle' });
  await ready(page);

  // Bring the machine up so its management processor answers.
  const api = async (path, init) =>
    (await fetch('http://localhost:5080/api/v1' + path, init)).json();
  const started = await api('/machines/rp3440/power', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ action: 'on' })
  });
  for (let i = 0; i < 40; i++) {
    const job = await api('/jobs/' + started.jobId);
    if (['Succeeded', 'Failed'].includes(job.status)) break;
    await page.waitForTimeout(2000);
  }

  await page.locator('tr', { hasText: 'HP rp3440' })
            .locator('button', { hasText: /^MP$/ }).first().tap();
  await page.waitForTimeout(3500);

  const text = await page.locator('.terminal-host').first().innerText().catch(() => '');
  ok(text.length > 0, 'console window opened and received output',
     text.slice(0, 60).replace(/\s+/g, ' '));

  // A floating window is no use on a phone, so it should fill the screen.
  const box = await page.locator('.window-layer .cde-window').boundingBox();
  const vp = page.viewportSize();
  ok(box.width > vp.width - 20, 'window fills the phone screen', `${Math.round(box.width)}/${vp.width}`);
  ok(!await page.locator('body').evaluate(b => b.scrollWidth > b.clientWidth), 'no horizontal page scroll');

  // The frame is the same 5px border strip a desktop window wears. It used to be widened to
  // 20/14/26 on a phone, which was spacing under the old chrome but is geometry under this one -
  // the bevels stayed put and the content moved, leaving a band of dead face all round.
  const frame = page.locator('.window-layer .cde-window');
  const bar = await frame.locator('.cde-titlebar').boundingBox();
  const border = Math.round(bar.x - box.x);
  ok(border <= 6, 'the title bar sits against the frame, with no dead space', `${border}px inset`);
  ok(Math.round((box.x + box.width) - (bar.x + bar.width)) === border, 'and the same on the right');

  // Closing from the window menu has to work under a finger. It did not: the menu hangs off the
  // title bar, so a tap on one of its entries reached the drag handler, whose preventDefault
  // cancels the click a browser synthesises from a tap. Harmless with a mouse, fatal with a
  // finger, which is why this needs a touch context to catch.
  await page.locator('.window-layer .cw-menu-btn').tap();
  await page.waitForTimeout(500);
  const windowMenu = page.locator('.window-layer .cde-titlebar .cde-dropdown');
  ok(await windowMenu.isVisible(), 'the window menu opens on a tap');
  await windowMenu.locator('button', { hasText: 'Close' }).tap();
  await page.waitForTimeout(900);
  ok(await page.locator('.window-layer .cde-window').count() === 0,
     'and Close in it actually closes the window');

  await page.screenshot({ path: `${shots}/phone-console.png` });
  await ctx.close();
}

await browser.close();
console.log(`\n${pass}/${pass + fail} checks passed`);
process.exit(fail ? 1 : 0);

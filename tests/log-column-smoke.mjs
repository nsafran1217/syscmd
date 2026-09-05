import { chromium } from 'playwright';
const shots = process.env.SHOTS || '.';
let pass = 0, fail = 0;
const ok = (c, l, x = '') => { console.log(`  ${c ? 'PASS' : 'FAIL'}  ${l}${x ? '   ' + x : ''}`); c ? pass++ : fail++; };
const sleep = ms => new Promise(r => setTimeout(r, ms));

const b = await chromium.launch();
const p = await (await b.newContext({ viewport: { width: 1500, height: 900 } })).newPage();
p.on('pageerror', e => console.log('  PAGE ERROR:', e.message));
await p.goto('http://localhost:5080/', { waitUntil: 'networkidle' });
await p.waitForFunction(() => window.Blazor !== undefined);
await p.waitForTimeout(1600);

const geom = async () => p.evaluate(() => {
  const m = document.querySelector('.dashboard-main').getBoundingClientRect();
  const s = document.querySelector('.dashboard-side').getBoundingClientRect();
  const w = document.querySelector('.dashboard-side > .cde-window').getBoundingClientRect();
  const last = [...document.querySelectorAll('.dashboard-main > .cde-window')].pop().getBoundingClientRect();
  return {
    mainTop: Math.round(m.top), sideTop: Math.round(s.top),
    lastBottom: Math.round(last.bottom), logBottom: Math.round(w.bottom),
    sideH: Math.round(s.height)
  };
});

const g = await geom();
console.log(`  main ${g.mainTop}..${g.lastBottom}   log ${g.sideTop}..${g.logBottom}`);
ok(g.sideTop === g.mainTop, 'tops line up', `${g.mainTop} vs ${g.sideTop}`);
ok(Math.abs(g.logBottom - g.lastBottom) <= 2, 'bottoms line up with the last centre window',
   `${g.logBottom} vs ${g.lastBottom}`);

// Scrolls with the page rather than being pinned.
const max = await p.evaluate(() => document.documentElement.scrollHeight - window.innerHeight);
await p.evaluate(sy => window.scrollTo(0, sy), max);
await p.waitForTimeout(300);
const s2 = await geom();
ok(s2.sideTop === s2.mainTop, 'still in step after scrolling', `${s2.mainTop} vs ${s2.sideTop}`);
ok(g.sideTop - s2.sideTop === max, 'moved the full scroll distance', `${g.sideTop - s2.sideTop} of ${max}`);

// An arriving entry must not resize the column.
await p.evaluate(() => window.scrollTo(0, 0));
await p.waitForTimeout(300);
const before = (await geom()).sideH;
await fetch('http://localhost:5080/api/v1/pdus/sim-pdu/outlets/6', {
  method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"action":"on","force":true}' });
await sleep(4000);
const after = (await geom()).sideH;
ok(before === after, 'height is unaffected by new log entries', `${before} -> ${after}`);
ok(await p.locator('.dashboard-side .event-log').evaluate(el => el.scrollHeight > el.clientHeight + 4),
   'the scrollback scrolls inside the window');

// Rolling it up should collapse it, not leave a tall empty frame.
await p.locator('.dashboard-side .cw-min').click();
await p.waitForTimeout(500);
const shaded = await p.locator('.dashboard-side > .cde-window').boundingBox();
ok(shaded.height < 80, 'rolls up to its title bar', `${Math.round(shaded.height)}px`);
await p.locator('.dashboard-side .cw-min').click();
await p.waitForTimeout(500);
const back = await geom();
ok(Math.abs(back.logBottom - back.lastBottom) <= 2, 'unrolls back to the full column',
   `${back.logBottom} vs ${back.lastBottom}`);

await p.screenshot({ path: `${shots}/log-aligned.png`, fullPage: true });
await b.close();
console.log(`\n${pass}/${pass + fail} checks passed`);
process.exit(fail ? 1 : 0);

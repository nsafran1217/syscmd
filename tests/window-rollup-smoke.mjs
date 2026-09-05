import { chromium } from 'playwright';
const shots = process.env.SHOTS || '.';
let pass = 0, fail = 0;
const ok = (c, l, x = '') => { console.log(`  ${c ? 'PASS' : 'FAIL'}  ${l}${x ? '   ' + x : ''}`); c ? pass++ : fail++; };
const api = async (path, init) => (await fetch('http://localhost:5080/api/v1' + path, init)).json();

const b = await chromium.launch();
const p = await (await b.newContext({ viewport: { width: 1500, height: 950 } })).newPage();
p.on('pageerror', e => console.log('  PAGE ERROR:', e.message));

const j = await api('/machines/rp3440/power', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"action":"on"}' });
for (let i = 0; i < 40; i++) { const s = await api('/jobs/' + j.jobId); if (['Succeeded','Failed'].includes(s.status)) break; await p.waitForTimeout(1500); }

await p.goto('http://localhost:5080/', { waitUntil: 'networkidle' });
await p.waitForFunction(() => window.Blazor !== undefined);
await p.waitForTimeout(1400);
await p.locator('tr', { hasText: 'HP rp3440' }).locator('button', { hasText: /^MP$/ }).first().click();
await p.waitForTimeout(3000);

const win = p.locator('.window-layer .cde-window').first();
const original = await win.boundingBox();
console.log(`  (opened at ${Math.round(original.width)}x${Math.round(original.height)})`);

console.log('\n[roll up and unroll with the title-bar box]');
await win.locator('.cw-min').click();
await p.waitForTimeout(400);
const shaded = await win.boundingBox();
ok(shaded.height < original.height / 2, 'rolls up to the title bar', `${Math.round(shaded.height)}px`);

await win.locator('.cw-min').click();
await p.waitForTimeout(500);
const back = await win.boundingBox();
ok(Math.abs(back.height - original.height) < 8 && Math.abs(back.width - original.width) < 8,
   'unrolls to the same size', `${Math.round(back.width)}x${Math.round(back.height)}`);

console.log('\n[window menu no longer duplicates Restore]');
await win.locator('.cw-min').click();      // roll up again
await p.waitForTimeout(400);
await win.locator('.cw-menu-btn').click();
await p.waitForTimeout(300);
const menu = win.locator('.cde-dropdown').first();
const labels = (await menu.locator('button').allInnerTexts()).map(t => t.trim());
ok(!labels.includes('Unroll'), 'no duplicate Unroll entry', labels.join(' | '));
ok(labels.includes('Restore'), 'Restore present');
const minDisabled = await menu.locator('button', { hasText: 'Minimise' }).isDisabled();
ok(minDisabled, 'Minimise greys out while already rolled up');

await menu.locator('button', { hasText: 'Restore' }).click();
await p.waitForTimeout(600);
const restored = await win.boundingBox();
ok(Math.abs(restored.height - original.height) < 8,
   'Restore brings back the full size', `${Math.round(restored.width)}x${Math.round(restored.height)}`);

console.log('\n[resizing still works after a roll-up]');
const grip = await win.locator('.cw-grip').boundingBox();
ok(!!grip, 'resize grip is present again');
// The corner handle is an L, so the grab has to be on an arm: the middle of its bounding box is
// clipped away and belongs to the client area underneath.
const gx = grip.x + grip.width - 3, gy = grip.y + grip.height - 3;
await p.mouse.move(gx, gy);
await p.mouse.down();
await p.mouse.move(gx + 140, gy + 100, { steps: 10 });
await p.mouse.up();
await p.waitForTimeout(500);
const resized = await win.boundingBox();
ok(resized.width > restored.width + 110 && resized.height > restored.height + 70,
   'grip resizes after the roll-up cycle', `${Math.round(resized.width)}x${Math.round(resized.height)}`);

console.log('\n[dragging while rolled up keeps the restored size]');
await win.locator('.cw-min').click();
await p.waitForTimeout(400);
const bar = await win.boundingBox();
await p.mouse.move(bar.x + bar.width / 2, bar.y + 10);
await p.mouse.down();
await p.mouse.move(bar.x + bar.width / 2 - 120, bar.y + 90, { steps: 10 });
await p.mouse.up();
await p.waitForTimeout(400);
await win.locator('.cw-min').click();
await p.waitForTimeout(500);
const afterDrag = await win.boundingBox();
ok(Math.abs(afterDrag.height - resized.height) < 10,
   'size survives being dragged while rolled up', `${Math.round(afterDrag.width)}x${Math.round(afterDrag.height)}`);

await p.screenshot({ path: `${shots}/rollup.png` });
await b.close();
console.log(`\n${pass}/${pass + fail} checks passed`);
process.exit(fail ? 1 : 0);

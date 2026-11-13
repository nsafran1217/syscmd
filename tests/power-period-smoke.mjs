import { chromium } from 'playwright';
import { readFileSync, existsSync } from 'node:fs';
const shots = process.env.SHOTS || '.';
let pass = 0, fail = 0;
const ok = (c, l, x = '') => { console.log(`  ${c ? 'PASS' : 'FAIL'}  ${l}${x ? '   ' + x : ''}`); c ? pass++ : fail++; };

// The expected figures are worked out here, from the CSV itself, rather than taken from anything
// the app computes: trapezoids per PDU, not bridging a gap over ten minutes, the same rule the
// running totals use. Needs the server's data.sim/, so run it from the same checkout.
const month = '2026-09';
const csv = `data.sim/power/${month}.csv`;
if (!existsSync(csv)) {
  console.log(`  SKIP  no ${csv} to check against`);
  process.exit(0);
}

const rows = readFileSync(csv, 'utf8').trim().split('\n').slice(1).map(l => l.split(','));
const last = new Map();
let wattSeconds = 0, peak = 0, sum = 0;
for (const [ts, pdu, watts] of rows) {
  const t = Date.parse(ts), w = Number(watts);
  if (last.has(pdu)) {
    const [pt, pw] = last.get(pdu);
    const span = (t - pt) / 1000;
    if (span > 0 && span <= 600) wattSeconds += (w + pw) / 2 * span;
  }
  last.set(pdu, [t, w]);
  peak = Math.max(peak, w);
  sum += w;
}
const appYaml = readFileSync('config.sim/app.yaml', 'utf8');
const rate = Number(/costPerKwh:\s*([\d.]+)/.exec(appYaml)[1]);
const kwh = wattSeconds / 3.6e6;
const expected = {
  kwh: kwh.toFixed(2),
  cost: (Math.round(kwh * rate * 100) / 100).toFixed(2),
  mean: Math.round(sum / rows.length),
  peak: Math.round(peak),
  readings: rows.length,
};
console.log(`  (expecting ${JSON.stringify(expected)} from ${csv})`);

const b = await chromium.launch();
const p = await (await b.newContext({ viewport: { width: 1400, height: 1000 } })).newPage();
p.on('pageerror', e => console.log('  PAGE ERROR:', e.message));
await p.goto('http://localhost:5080/power', { waitUntil: 'networkidle' });
await p.waitForFunction(() => window.Blazor !== undefined);
await p.waitForTimeout(1400);

const picker = p.locator('select[aria-label="Period to show"]');
const figures = async () => {
  const stats = (await p.locator('.cde-window').first().locator('.stat').allInnerTexts()).slice(5);
  return stats.map(s => s.replace(/\s+/g, ' '));
};
const setDate = async (label, value) => {
  const input = p.locator(`input[aria-label="${label}"]`);
  await input.fill(value);
  await input.dispatchEvent('change');
  await p.waitForTimeout(600);
};

console.log('\n[a custom range adds up to what the CSV holds]');
{
  await picker.selectOption('custom');
  await p.waitForTimeout(600);
  ok(await p.locator('input[type=date]').count() === 2, 'Custom range offers a From and a To date');
  await setDate('From', `${month}-01`);
  await setDate('To', `${month}-30`);

  const [energy, cost, average, peakText] = await figures();
  ok(energy.includes(`${expected.kwh}kWh`), 'energy matches the integrated CSV', energy);
  ok(cost.includes(`$${expected.cost}`), 'cost is energy at the configured rate', cost);
  ok(average.includes(`${expected.mean}W`) && average.includes(expected.readings.toLocaleString('en-US')),
     'average draw and reading count match', average);
  ok(peakText.includes(`${expected.peak}W`), 'peak matches the highest reading', peakText);
  ok(energy.includes('01 Sep 2026 - 30 Sep 2026'), 'the range is shown with its last day, not the midnight after', energy);
  ok((await p.locator('.cde-window').nth(1).locator('.cw-title').innerText()).includes('01 Sep 2026 to 30 Sep 2026'),
     'the chart is titled with the range');
  ok(await p.locator('svg polyline').count() > 1, 'gaps in the history break the line rather than bridging it',
     `${await p.locator('svg polyline').count()} segments`);
  await p.screenshot({ path: `${shots}/power-custom.png`, fullPage: true });
}

console.log('\n[a range entered backwards is still the same range]');
{
  await setDate('From', `${month}-30`);
  await setDate('To', `${month}-01`);
  ok((await figures())[0].includes(`${expected.kwh}kWh`), 'From and To swapped give the same energy');
}

console.log('\n[every preset resolves and draws]');
{
  const keys = await picker.locator('option').evaluateAll(os => os.map(o => o.value).filter(v => v !== 'custom'));
  let bad = [];
  for (const key of keys) {
    await picker.selectOption(key);
    await p.waitForTimeout(500);
    const heading = await p.locator('.cde-window').first().locator('h2').innerText();
    if (!heading || (await figures()).length !== 4) bad.push(key);
  }
  ok(bad.length === 0, `all ${keys.length} presets show a heading and four figures`, bad.join(', '));

  await picker.selectOption('month');
  await p.waitForTimeout(500);
  const now = new Date();
  const name = now.toLocaleString('en-US', { month: 'long', year: 'numeric' });
  ok((await p.locator('.cde-window').first().locator('h2').innerText()) === name, 'This month is named for the month', name);
}

console.log('\n[the choice is remembered per browser]');
{
  await picker.selectOption('lastmonth');
  await p.waitForTimeout(500);
  await p.reload({ waitUntil: 'networkidle' });
  await p.waitForTimeout(1500);
  ok(await picker.inputValue() === 'lastmonth', 'the period survives a reload');

  await picker.selectOption('custom');
  await p.waitForTimeout(400);
  await setDate('From', `${month}-02`);
  await setDate('To', `${month}-03`);
  await p.reload({ waitUntil: 'networkidle' });
  await p.waitForTimeout(1500);
  ok(await picker.inputValue() === 'custom'
     && await p.locator('input[aria-label="From"]').inputValue() === `${month}-02`
     && await p.locator('input[aria-label="To"]').inputValue() === `${month}-03`,
     'a custom range comes back with its dates');

  // Leave the page as a fresh browser would find it.
  await picker.selectOption('24h');
  await p.waitForTimeout(400);
}

await b.close();
console.log(`\n${pass}/${pass + fail} checks passed`);
process.exit(fail ? 1 : 0);

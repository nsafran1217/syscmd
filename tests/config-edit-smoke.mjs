import { chromium } from 'playwright';
const shots = process.env.SHOTS || '.';
let pass = 0, fail = 0;
const ok = (c, l, x = '') => { console.log(`  ${c ? 'PASS' : 'FAIL'}  ${l}${x ? '   ' + x : ''}`); c ? pass++ : fail++; };

const b = await chromium.launch();
const p = await (await b.newContext({ viewport: { width: 1500, height: 1000 } })).newPage();
p.on('pageerror', e => console.log('  PAGE ERROR:', e.message));

await p.goto('http://localhost:5080/config/machines', { waitUntil: 'networkidle' });
await p.waitForFunction(() => window.Blazor !== undefined);
await p.waitForTimeout(1200);

await p.locator('tr', { hasText: 'rp3440' }).locator('button', { hasText: 'Edit' }).first().click();
await p.waitForTimeout(800);

console.log('\n[password]');
const pw = p.locator('label', { hasText: 'Password' }).locator('input');
ok(await pw.getAttribute('type') === 'text', 'password is a plain text field');
ok((await pw.inputValue()).length > 0, 'its value is readable', await pw.inputValue());

console.log('\n[hostname lookup]');
const row = p.locator('div', { has: p.locator('label:has-text("Hostname")') }).last();
const hostField = row.locator('label', { hasText: 'Hostname' }).locator('input');
const ipField = row.locator('label', { hasText: /^IP$/ }).locator('input');
const lookup = row.locator('button', { hasText: 'Look up' });

ok(await lookup.isVisible(), 'a Look up button sits next to the hostname');

// A name that resolves everywhere.
await hostField.fill('localhost');
await ipField.fill('');
await lookup.click();
await p.waitForTimeout(2500);
const resolved = await ipField.inputValue();
ok(resolved === '127.0.0.1', 'resolves the hostname into the IP field', resolved);
const note = await row.locator('xpath=following-sibling::div[1]').innerText().catch(() => '');
ok(/Resolved to/.test(note), 'reports what it found', note.trim());

// A name that does not resolve should not clobber the field.
await hostField.fill('definitely-not-a-real-host.invalid');
await lookup.click();
await p.waitForTimeout(6000);
ok(await ipField.inputValue() === '127.0.0.1', 'a failed lookup leaves the IP alone');
const bad = await row.locator('xpath=following-sibling::div[1]').innerText().catch(() => '');
ok(/Could not resolve|Timed out|resolved to nothing/i.test(bad), 'explains the failure', bad.trim().slice(0, 70));

// Empty hostname is handled rather than throwing.
await hostField.fill('');
await lookup.click();
await p.waitForTimeout(800);
const empty = await row.locator('xpath=following-sibling::div[1]').innerText().catch(() => '');
ok(/Enter a hostname/i.test(empty), 'prompts when there is no hostname', empty.trim());

await p.screenshot({ path: `${shots}/machine-edit.png` });
await b.close();
console.log(`\n${pass}/${pass + fail} checks passed`);
process.exit(fail ? 1 : 0);

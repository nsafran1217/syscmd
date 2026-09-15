// Console logs: every console session gets a file of its own, the file reads as plain text, the
// log page lists, shows and downloads it, and retention removes old logs while leaving the rest.
//
// Saving from the page rewrites config.sim/app.yaml, which drops its comments. The file is
// tracked, so it is put back exactly as it was however the run ends. The retention check plants a
// log with an old timestamp under data.sim/, so this has to run from the same checkout as the
// server.

import { chromium } from 'playwright';
import { readFileSync, writeFileSync, mkdirSync, utimesSync, existsSync } from 'node:fs';

const base = 'http://localhost:5080';
const shots = process.env.SHOTS || '.';
const repo = new URL('..', import.meta.url);
const appYaml = new URL('config.sim/app.yaml', repo);
const plantedDir = new URL('data.sim/console-logs/zz-retention-check/', repo);
const planted = new URL('2020-01-01_00-00-00_serial.log', plantedDir);

let pass = 0, fail = 0;
const ok = (c, l, x = '') => { console.log(`  ${c ? 'PASS' : 'FAIL'}  ${l}${x ? '   ' + x : ''}`); c ? pass++ : fail++; };
const api = async (path, init) => (await fetch(base + '/api/v1' + path, init)).json();
const logsFor = machine => api('/console-logs?machine=' + machine);
const logText = async name => (await fetch(`${base}/api/v1/console-logs/rp3440/${name}`)).text();

const originalYaml = readFileSync(appYaml, 'utf8');
const b = await chromium.launch();

try {
  const p = await (await b.newContext({ viewport: { width: 1500, height: 950 }, acceptDownloads: true })).newPage();
  p.on('pageerror', e => console.log('  PAGE ERROR:', e.message));

  // The MP has to be up to answer. A console that never connects is not logged - there is
  // nothing to record - so a service processor still booting would look like a missing log.
  const started = await api('/machines/rp3440/power', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ action: 'on' }),
  });
  for (let i = 0; i < 60; i++) {
    const job = await api('/jobs/' + started.jobId);
    if (['Succeeded', 'Failed'].includes(job.status)) { console.log('  (rp3440 power-on: ' + job.status + ')'); break; }
    await p.waitForTimeout(2000);
  }

  const openConsole = async () => {
    await p.goto(base + '/', { waitUntil: 'networkidle' });
    await p.waitForFunction(() => window.Blazor !== undefined);
    await p.waitForTimeout(1500);
    await p.locator('tr', { hasText: 'HP rp3440' }).locator('button', { hasText: /^MP$/ }).first().click();
    await p.waitForTimeout(3000);
    return p.locator('.window-layer .cde-window').first();
  };
  const closeConsole = async win => {
    await win.locator('.cw-menu-btn').dblclick();
    await p.waitForTimeout(1500);
  };

  console.log('\n[a console session writes a log of its own]');

  const before = (await logsFor('rp3440')).length;
  let win = await openConsole();
  let logs = await logsFor('rp3440');
  ok(logs.length === before + 1, 'opening a console starts a new log', `${before} -> ${logs.length}`);
  const first = logs[0];
  ok(/^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_mp(-\d+)?\.log$/.test(first?.name || ''),
     'named for when it opened and which console', first?.name);
  ok(first?.active === true, 'and marked open while the console is');

  // The simulated MP takes any login; what matters is that its answers land in the file.
  await win.locator('.terminal-host').click();
  for (const line of ['Admin', 'Admin', 'zzmarker']) {
    await p.keyboard.type(line);
    await p.keyboard.press('Enter');
    await p.waitForTimeout(500);
  }
  await p.waitForTimeout(800);

  let log = await logText(first.name);
  ok(/HP rp3440 management processor at /.test(log), 'it opens with a line saying which console it is');
  ok(/login:/.test(log) && /MP>/.test(log), 'it holds what the device sent');
  ok(/Unrecognized command: zzmarker/.test(log), 'including the reply to what was typed');
  ok(!/[\x1b\r]/.test(log), 'with no escape sequences or carriage returns left in it');

  await closeConsole(win);
  const closed = (await logsFor('rp3440')).find(f => f.name === first.name);
  ok(closed && !closed.active, 'closing the console closes the log');
  log = await logText(first.name);
  ok(/\*\*\* Closed .* - the browser disconnected \*\*\*\s*$/.test(log), 'and it ends by saying why',
     log.trim().split('\n').pop());

  win = await openConsole();
  ok((await logsFor('rp3440')).length === before + 2, 'opening it again starts another log rather than appending');
  await closeConsole(win);

  console.log('\n[the log page]');

  await p.goto(base + '/console-logs', { waitUntil: 'networkidle' });
  await p.waitForTimeout(1500);
  const row = p.locator('tr', { hasText: 'HP rp3440' }).first();
  ok(await row.isVisible(), 'lists the sessions by machine');

  await row.locator('button', { hasText: 'View' }).click();
  await p.waitForTimeout(800);
  ok(/HP rp3440 management processor at /.test(await p.locator('.log-view').textContent() || ''),
     'View shows the log on the page');
  await p.screenshot({ path: `${shots}/console-logs.png` });

  const [download] = await Promise.all([
    p.waitForEvent('download', { timeout: 10000 }),
    row.locator('a', { hasText: 'Download' }).click(),
  ]);
  ok(/^rp3440_.*\.log$/.test(download.suggestedFilename()), 'Download saves it as a file named for the session',
     download.suggestedFilename());
  ok(/HP rp3440 management processor at /.test(readFileSync(await download.path(), 'utf8')), 'holding the same text');

  ok((await fetch(`${base}/api/v1/console-logs/rp3440/..%2F..%2F..%2Fconfig.sim%2Fapp.yaml`)).status === 404,
     'a download cannot reach outside the log directory');

  console.log('\n[retention]');

  // A log last written ten days ago, beside the sessions above from just now.
  mkdirSync(plantedDir, { recursive: true });
  writeFileSync(planted, '*** an old session ***\n');
  const tenDaysAgo = new Date(Date.now() - 10 * 86400000);
  utimesSync(planted, tenDaysAgo, tenDaysAgo);

  const days = p.locator('#console-log-days');
  const save = p.locator('button', { hasText: 'Save settings' });

  await days.fill('-1');
  await save.click();
  await p.waitForTimeout(800);
  ok((await p.textContent('body')).includes('cannot be negative'), 'a negative retention is refused');
  ok(existsSync(planted), 'and nothing was removed');

  await days.fill('5');
  await save.click();
  await p.waitForTimeout(1500);
  ok(!existsSync(planted), 'saving 5 days removes a log last written 10 days ago');
  ok((await logsFor('rp3440')).length === before + 2, 'and keeps the recent ones');
  ok((await api('/config/app')).consoleLogs?.retentionDays === 5, 'the setting is saved to app.yaml');

  console.log('\n[turned off]');

  await p.locator('#console-log-enabled').uncheck();
  await save.click();
  await p.waitForTimeout(1000);
  ok((await api('/config/app')).consoleLogs?.enabled === false, 'recording can be turned off');
  win = await openConsole();
  ok((await logsFor('rp3440')).length === before + 2, 'and a console opened then writes no log');
  await closeConsole(win);
} finally {
  writeFileSync(appYaml, originalYaml);
  await b.close();
}

console.log(`\n${pass}/${pass + fail} checks passed`);
process.exit(fail ? 1 : 0);

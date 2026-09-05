// The checks the whole design exists for: nothing cuts power to a running machine.
// Drives the REST API only - no browser needed. Takes a few minutes, because it waits out a
// real shutdown-confirmation timeout.
const API = 'http://localhost:5080/api/v1';

let pass = 0, fail = 0;
const ok = (c, l, x = '') => { console.log(`  ${c ? 'PASS' : 'FAIL'}  ${l}${x ? '   ' + x : ''}`); c ? pass++ : fail++; };
const sleep = ms => new Promise(r => setTimeout(r, ms));

const get = async path => (await fetch(API + path)).json();
const post = async (path, body) =>
  (await fetch(API + path, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
  })).json();

const wait = async (jobId, limitMs = 200000) => {
  const end = Date.now() + limitMs;
  let job;
  while (Date.now() < end) {
    await sleep(2000);
    job = await get(`/jobs/${jobId}`);
    if (['Succeeded', 'Failed', 'Cancelled'].includes(job.status)) return job;
  }
  return job;
};

const outlet = async n => (await get('/pdus/sim-pdu/outlets')).find(o => o.outlet === n).state;

console.log('\n[1] safe power-on of an HP MP machine from cold');
await post('/pdus/sim-pdu/outlets/1', { action: 'off', force: true });
await sleep(3000);
let job = await wait((await post('/machines/rp3440/power', { action: 'on' })).jobId);
ok(job.status === 'Succeeded', 'power-on succeeded', job.error || '');
ok(job.progress.some(p => /Confirmed: the system is powered on/.test(p)),
   'the MP confirmed the system is running');
ok(await outlet(1) === 'On', 'outlet 1 is on');

console.log('\n[2] power-off leaves the outlet live until the MP confirms');
{
  const id = (await post('/machines/rp3440/power', { action: 'off' })).jobId;
  let sawLiveWhileShuttingDown = false;
  const end = Date.now() + 120000;
  let d;
  while (Date.now() < end) {
    await sleep(2000);
    d = await get(`/jobs/${id}`);
    if (d.progress.some(p => /Still shutting down/.test(p)) && await outlet(1) === 'On') {
      sawLiveWhileShuttingDown = true;
    }
    if (['Succeeded', 'Failed'].includes(d.status)) break;
  }
  ok(sawLiveWhileShuttingDown, 'outlet stayed ON while the machine was shutting down');
  ok(d.status === 'Succeeded', 'power-off succeeded', d.error || '');
  ok(await outlet(1) === 'Off', 'outlet 1 cut only after confirmation');
}

console.log('\n[3] a machine that never confirms shutdown keeps its outlet');
job = await wait((await post('/machines/alpha1000/power', { action: 'on' })).jobId);
ok(job.status === 'Succeeded', 'stubborn machine powered on', job.error || '');
job = await wait((await post('/machines/alpha1000/power', { action: 'off' })).jobId);
ok(job.status === 'Failed', 'safe power-off failed rather than cutting power');
ok(await outlet(4) === 'On', 'outlet 4 LEFT ON after the failure', job.error || '');

console.log('\n[4] force off overrides it');
job = await wait((await post('/pdus/sim-pdu/outlets/4', { action: 'off', force: true })).jobId);
ok(job.status === 'Succeeded' && await outlet(4) === 'Off', 'forced outlet off');
{
  const events = await get('/events?limit=100&level=Warning');
  ok(events.some(e => /without asking its management processor/.test(e.message)),
     'the override was logged as a warning');
}

console.log('\n[5] machine with no management processor');
job = await wait((await post('/machines/vax3100/power', { action: 'on' })).jobId);
ok(job.status === 'Succeeded' && await outlet(5) === 'On', 'outlet-only power-on');
{
  const res = await fetch(`${API}/machines/vax3100/power`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ action: 'reset' })
  });
  ok(res.status === 400, 'reset without an MP is rejected up front', `HTTP ${res.status}`);
}

console.log('\n[6] stopping a job leaves untouched outlets alone');
{
  await post('/machines/alpha1000/power', { action: 'on' });
  await sleep(1000);
  const on = await wait((await post('/machines/alpha1000/power', { action: 'on' })).jobId);
  ok(on.status === 'Succeeded', 'stubborn machine back on', on.error || '');

  const id = (await post('/machines/alpha1000/power', { action: 'off' })).jobId;
  await sleep(6000);
  const stop = await fetch(`${API}/jobs/${id}/cancel`, { method: 'POST' });
  ok(stop.status === 200, 'the job accepted a stop', `HTTP ${stop.status}`);

  const stopped = await wait(id, 40000);
  ok(stopped.status === 'Cancelled', 'job reports as Cancelled', stopped.status);
  ok(await outlet(4) === 'On', 'outlet left ON after the kill, nothing was cut');
  await post('/pdus/sim-pdu/outlets/4', { action: 'off', force: true });
}

console.log('\n[7] group power-on staggers its members');
{
  const parent = await wait((await post('/groups/hp-all/power', { action: 'on' })).jobId);
  const children = (await get('/jobs?limit=100')).filter(j => j.parentJobId === parent.id);
  const starts = children.map(c => c.startedAt).filter(Boolean).sort();
  const gap = starts.length >= 2 ? (Date.parse(starts[1]) - Date.parse(starts[0])) / 1000 : 0;
  ok(parent.status === 'Succeeded', 'group power-on succeeded', parent.error || '');
  ok(gap >= 7, `members started ${gap.toFixed(0)}s apart (configured 8s)`);
}

console.log('\n[8] power, cost and config');
{
  const s = await get('/status');
  ok(s.power.currentWatts > 0, `live draw ${s.power.currentWatts.toFixed(0)} W`);
  ok(s.power.costPerHour > 0, `cost per hour ${s.power.costPerHour}`);
  ok((await get('/power/history')).length > 0, 'power history is being recorded');
  ok((await get('/config')).issues.length === 0, 'no configuration errors');
}

console.log(`\n${pass}/${pass + fail} checks passed`);
process.exit(fail ? 1 : 0);

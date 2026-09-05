// Checks that a PDU reporting whole watts is read correctly, by pointing the same
// simulated unit at a watts OID instead of a current one.
let pass = 0, fail = 0;
const ok = (c, l, x = '') => { console.log(`  ${c ? 'PASS' : 'FAIL'}  ${l}${x ? '   ' + x : ''}`); c ? pass++ : fail++; };
const api = async (path, init) => (await fetch('http://localhost:5080/api/v1' + path, init)).json();
const sleep = ms => new Promise(r => setTimeout(r, ms));

// Put some load on: bring a couple of machines up.
for (const id of ['rp3440', 'rx2660']) {
  const j = await api(`/machines/${id}/power`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"action":"on"}' });
  for (let i = 0; i < 40; i++) { const s = await api('/jobs/' + j.jobId); if (['Succeeded', 'Failed'].includes(s.status)) break; await sleep(1500); }
}
await sleep(2000);

const readPdu = async () => (await api('/pdus')).find(p => p.pduId === 'sim-pdu');

const asAmps = await readPdu();
ok(asAmps.reachable, 'PDU readable through the deciamps OID');
console.log(`  (deciamps config: ${asAmps.watts.toFixed(0)} W, ${asAmps.amps.toFixed(2)} A at ${asAmps.volts} V)`);

// Switch the same unit to the watts driver.
const pduCfg = (await api('/config/pdus')).find(p => p.id === 'sim-pdu');
await fetch('http://localhost:5080/api/v1/config/pdus/sim-pdu', {
  method: 'PUT', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ ...pduCfg, type: 'apc-watts' })
});
await sleep(4000);

const asWatts = await readPdu();
ok(asWatts.reachable, 'PDU readable through the watts OID');
console.log(`  (watts config:    ${asWatts.watts.toFixed(0)} W, ${asWatts.amps.toFixed(2)} A at ${asWatts.volts} V)`);

// The same load read two ways should agree; the current OID quantises to 0.1 A (12 W at 120 V).
ok(Math.abs(asWatts.watts - asAmps.watts) < 25,
   'watts reading matches the current-derived reading',
   `${asWatts.watts.toFixed(0)} vs ${asAmps.watts.toFixed(0)} W`);

// Watts must be taken verbatim, not scaled by voltage.
ok(asWatts.watts > 300 && asWatts.watts < 1200,
   'watts are used as-is, not multiplied by volts', `${asWatts.watts.toFixed(0)} W`);

// Amps are derived from watts and the nominal voltage.
ok(Math.abs(asWatts.amps - asWatts.watts / 240) < 0.05,
   'amps derived as watts / nominalVolts', `${asWatts.amps.toFixed(2)} A = ${asWatts.watts.toFixed(0)}/240`);
ok(asWatts.volts === 240, 'reports the configured voltage', `${asWatts.volts} V`);

// And the energy tally keeps advancing on the watts path.
const before = (await api('/power/summary')).todayKwh;
await sleep(20000);
const after = (await api('/power/summary')).todayKwh;
ok(after > before, 'energy accrues while reading watts', `${before.toFixed(4)} -> ${after.toFixed(4)} kWh`);

// Put it back.
await fetch('http://localhost:5080/api/v1/config/pdus/sim-pdu', {
  method: 'PUT', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ ...pduCfg, type: 'apc-ap7900' })
});
await sleep(3000);
ok((await readPdu()).reachable, 'restored to the original driver');

console.log(`\n${pass}/${pass + fail} checks passed`);
process.exit(fail ? 1 : 0);

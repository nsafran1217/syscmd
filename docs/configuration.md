# Configuration reference

Everything syscmd knows about the lab is YAML under `config/`. There is no database. The GUI edits
the same files, and hand edits are picked up without a restart by a debounced file watcher.

`config.example/` holds documented templates to copy from. `config.sim/` describes the fake lab
`--simulate` boots, and is worth reading as a worked example — it covers a machine on an MP, one
reached through a terminal server, one that refuses to shut down, and one with no service
processor at all.

For the expect/send scripts that drive management processors, see **[mp-types.md](mp-types.md)**.

## Layout

```
config/
  app.yaml                  global settings
  groups.yaml               named sets of machines for bulk power
  pdu-types/*.yaml          OID maps, one per PDU model
  pdus/*.yaml               the actual PDUs on the network
  mp-types/*.yaml           expect scripts, one per service-processor model
  console-servers/*.yaml    terminal servers exposing serial ports as TCP ports
  machines/*.yaml           the machines themselves
  palettes/*.dp             optional extra CDE palettes
  backdrops/*.pm, *.bm      optional extra CDE backdrops
```

Two rules run through all of it:

- **Device *types* are separate from device *instances*.** A `pdu-type` or `mp-type` describes a
  model; a `pdu` or `machine` is one box. Supporting new hardware should be a new type file, never
  a code change.
- **The file stem is the id.** `machines/rp3440.yaml` is machine `rp3440`, and every reference —
  `pdu.id`, `mp.type`, a group's member list — resolves by filename. See the note below on the
  `id:` key, which is not quite the same thing.

Keys are camelCase, and **unknown keys are silently ignored** — a misspelling does not error, it
just does nothing.

### The `id:` key

Everything is looked up by filename, but the `id:` key inside a file is not simply decorative, and
the two are not treated the same everywhere:

- In `pdu-types/` and `mp-types/`, the filename always wins and any `id:` in the file is
  overwritten on load.
- In `pdus/`, `console-servers/` and `machines/`, an `id:` in the file is **kept**, and the
  filename only fills it in when the key is absent.

That second case has a sharp edge. The object is still *found* under its filename, so everything
appears to work — but the GUI saves a machine to the file named after its **internal** id. Give
`machines/sunv100.yaml` an `id: sunfire`, edit it in the GUI, and you get a second file,
`machines/sunfire.yaml`, while the original stays behind. Keep the two the same, or leave `id:`
out and let the filename supply it.

### Validation

Problems are reported, not thrown. One bad file marks that object broken and everything else still
loads, so a typo cannot stop the app booting. Issues appear on the Configuration page and in the
event log at startup, each as an **error** (the object is unusable) or a **warning** (it works,
but something is worth knowing). Every rule is listed under its file type below.

### Secrets

`config/` holds SNMP community strings and MP passwords in clear text, and is gitignored for that
reason. There is no authentication in front of the app either — see the security note in the
README. Both are deliberate for a trusted lab network; neither is a good idea anywhere else.

---

## app.yaml

```yaml
site:
  name: Home Lab
  theme: Default            # a palette name from wwwroot/cde/palettes or config/palettes
  backdrop: Toronto
  backdropColorSet: 3       # 3, 5, 6 or 7
  randomThemes: []          # palettes a browser set to "random" may draw from

power:
  pollIntervalSeconds: 30
  costPerKwh: 0.14
  currency: USD

orchestration:
  outletSettleSeconds: 5
  powerOnMpTimeoutSeconds: 180
  powerOffConfirmTimeoutSeconds: 300
  powerOffPollIntervalSeconds: 10
```

| Field | Default | Notes |
|---|---|---|
| `site.name` | `Home Lab` | Shown in the top strip and window titles. |
| `site.theme` | `Default` | The lab's default palette. A browser that picks its own on the Appearance page overrides it. |
| `site.backdrop` | `Toronto` | Default backdrop. `NoBackdrop` leaves the desktop plain. |
| `site.backdropColorSet` | `3` | Which CDE colour set tints the backdrop. Only 3, 5, 6 and 7 are backdrop colours; anything else falls back to 3. |
| `site.randomThemes` | empty | Pool for a browser set to random. Empty means any palette. |
| `power.pollIntervalSeconds` | `30` | How often every PDU is polled. |
| `power.costPerKwh` | `0.14` | Turns kWh into money on the overview. |
| `power.currency` | `USD` | Label only. |
| `orchestration.outletSettleSeconds` | `5` | Pause after switching an outlet on before probing the MP. |
| `orchestration.powerOnMpTimeoutSeconds` | `180` | How long to wait for an MP to answer after power is applied. Vintage processors take a while. |
| `orchestration.powerOffConfirmTimeoutSeconds` | `300` | How long to wait for a machine to confirm it is down. **On expiry the outlet is deliberately left on and the job fails.** |
| `orchestration.powerOffPollIntervalSeconds` | `10` | How often `status` is re-run while waiting. |

**Validation:** `power.costPerKwh` below zero is an error. `power.pollIntervalSeconds` below 5 is a
warning — it will hammer the PDUs.

---

## pdu-types/*.yaml

The OID map for one model of PDU. `{outlet}` is substituted with the outlet number at request time.

```yaml
name: APC AP7900
snmp:
  version: v2c                       # v1 or v2c; v3 is not implemented

outlets:
  stateOid:   .1.3.6.1.4.1.318.1.1.4.4.2.1.3.{outlet}
  controlOid: .1.3.6.1.4.1.318.1.1.4.4.2.1.3.{outlet}
  nameOid:    .1.3.6.1.4.1.318.1.1.4.4.2.1.4.{outlet}
  commands:                          # integer written to controlOid
    on: 1
    off: 2
    reboot: 3
  stateMap:                          # integer read from stateOid
    1: on
    2: off

power:                               # optional, for metered units
  loadOid: .1.3.6.1.4.1.318.1.1.12.2.3.1.1.2.1
  loadUnit: deciAmps                 # watts | deciWatts | amps | deciAmps
  nominalVolts: 120
  perOutletWattsOid: .1.3.6.1.4.1.318.1.1.12.3.5.1.1.5.{outlet}
  perOutletUnit: deciWatts
```

| Field | Default | Notes |
|---|---|---|
| `snmp.version` | `v2c` | `v1` or `v2c`. |
| `outlets.stateOid` | — | **Required.** Read to learn an outlet's state. |
| `outlets.controlOid` | — | **Required.** Written to control it. Often the same OID. |
| `outlets.nameOid` | — | Optional. The outlet's name as configured on the PDU itself. |
| `outlets.commands` | — | **Must contain `on` and `off`**; `reboot` is optional. |
| `outlets.stateMap` | — | **Required.** Maps the integer read back to `on` / `off`. |
| `power.loadOid` | — | Whole-PDU load. No `{outlet}` substitution. |
| `power.loadUnit` | `watts` | Everything is normalised to watts from this. |
| `power.nominalVolts` | `120` | Assumed line voltage when the PDU reports current rather than power. |
| `power.perOutletWattsOid` | — | Per-outlet metering, if the unit has it. Supports `{outlet}`. |
| `power.perOutletUnit` | falls back to `loadUnit` | Often genuinely different: APC metered rPDUs report phase load in deciamps and per-outlet power in tenths of a watt, so reusing one unit for both mis-scales it. |

**Validation (all errors):** missing `stateOid`, missing `controlOid`, missing `on` or `off` in
`commands`, empty `stateMap`, or an `snmp.version` other than v1/v2c.

---

## pdus/*.yaml

One physical PDU.

```yaml
name: Compaq Rack PDU
type: apc-ap7900          # a file stem in pdu-types/
host: 10.40.0.210
port: 161
community:
  read: public
  write: private
outletCount: 24
```

| Field | Default | Notes |
|---|---|---|
| `name` | `""` | Display name. |
| `type` | — | Must match a file in `pdu-types/`. |
| `host` | — | **Required.** |
| `port` | `161` | SNMP port. |
| `community.read` / `.write` | `public` / `private` | SNMP community strings. |
| `outletCount` | — | **Must be greater than zero.** Also bounds the outlet numbers machines may claim. |

**Validation (all errors):** empty `host`, `outletCount` of zero or less, or a `type` with no
matching file.

---

## console-servers/*.yaml

A terminal server that exposes serial ports as TCP ports.

```yaml
name: APC Terminal Server
host: 10.40.0.209
# Physical serial port number -> TCP port that reaches it.
ports:
  1: 7001
  2: 7002
  16: 7016
```

The mapping is the point: machines refer to the **physical port number**, so re-cabling a machine
to a different port is a one-line change on the machine and nothing else moves.

Referencing a port that is not in `ports` is an error on the *machine*, not here.

---

## machines/*.yaml

```yaml
name: SunFire V100
description: 1U SPARC box
tags: [sun, solaris]

pdu:
  id: apcterm-pdu
  outlet: 7

mp:
  type: sun-lom
  via:                    # either this...
    server: apcterm
    port: 16
  # host: 10.40.0.50      # ...or this. Never both.
  # port: 23
  username: admin
  password: secret

serial:
  server: apcterm
  port: 16

addresses:
  - label: primary
    ip: 10.40.0.137
    hostname: sunv100
```

| Field | Notes |
|---|---|
| `name` | Empty is a warning; the id is shown instead. |
| `description`, `tags` | Display only. |
| `pdu.id` / `pdu.outlet` | Which outlet feeds this machine. Optional — a machine with no PDU simply has no outlet control. |
| `mp.type` | A file stem in `mp-types/`. |
| `mp.host` / `mp.port` | A direct network address for the MP. `port` defaults to the mp-type's `defaultPort`. |
| `mp.via.server` / `.port` | Reach the MP through a console server instead. `port` is the **physical** port, looked up in the console server's map. |
| `mp.username` / `.password` | Substituted into the mp-type's script as `{username}` / `{password}`. |
| `serial.server` / `.port` | A plain serial console, separate from the MP. Gives the machine a **Serial** console button. |
| `addresses` | Informational. Each entry has an optional `label`, `ip` and `hostname`. |

Three combinations are all valid and all mean something:

- **`mp` only** — an MP on the network. Power control and an MP console.
- **`mp.via` and `serial` on the same port** — one wire carrying both. syscmd makes them contend
  for it rather than collide, so opening the console really does reserve it and a power job reports
  *"already in use"* within seconds instead of hanging.
- **`serial` only, no `mp`** — reached over a terminal server with no service processor anywhere.
  The console opens; everything on the console's Power menu greys out, because it all goes through
  an MP.

**Validation:**

| Severity | Condition |
|---|---|
| Warning | `name` is empty |
| Error | `pdu.id` has no matching file in `pdus/` |
| Error | `pdu.outlet` is outside `1..outletCount` for that PDU |
| Error | another machine already claims that PDU and outlet |
| Error | `mp.type` has no matching file in `mp-types/` |
| Error | `mp` sets neither `host` nor `via` |
| Error | `mp` sets **both** `host` and `via` |
| Error | `mp.via.server` or `serial.server` has no matching file in `console-servers/` |
| Error | `mp.via.port` or `serial.port` is not mapped on that console server |

The duplicate-outlet check is worth the trouble it saves: two machines claiming one outlet means a
power-off aimed at one cuts the other.

---

## groups.yaml

Named sets of machines, for powering several at once.

```yaml
groups:
  - id: all-hp
    name: All HP
    machines: [rp3440, rx2660, rx2800i2]
    staggerSeconds: 8
```

| Field | Default | Notes |
|---|---|---|
| `id` | — | **Required.** A group with no id is an error. |
| `name` | `""` | Display name. |
| `machines` | empty | Machine ids, **in power-on order**. |
| `staggerSeconds` | `10` | Delay between starting each member, to spread inrush current. |

Referencing an unknown machine is an error. Group power-offs always confirm before running, even
when the per-browser "skip confirmation" preference is on, because they move several machines at
once.

---

## palettes/ and backdrops/

Optional. A `.dp` palette or `.pm`/`.bm` backdrop dropped in `config/palettes/` or
`config/backdrops/` joins the ones shipped with the app, and wins over a shipped file of the same
name. The formats and how they are coloured are described in
[`src/SysCmd.Server/wwwroot/cde/README.md`](../src/SysCmd.Server/wwwroot/cde/README.md).

---

## Editing

Three ways, all equivalent:

- **The GUI**, under Configuration. It writes the same YAML.
- **By hand.** A debounced file watcher reloads on save, so changes are live without a restart.
- **The REST API**, `/api/v1/config`, for scripting.

Saves are validate → temp file → atomic rename → reload, so an interrupted write cannot leave a
half-file behind. Note that saving through the GUI or API **round-trips the YAML and drops any
comments** in the file it saved.

# syscmd

A control plane for a home lab of vintage UNIX machines: SNMP PDU outlet control, management
processor automation, power and cost tracking, and browser consoles — in a CDE-styled web UI over
a REST API.

The point of the design is that **nothing cuts power to a running machine**. Switching an outlet
off asks the machine's management processor to shut down, polls until the MP confirms the system
is really off, and only then switches the outlet. If it never confirms, the job fails and the
outlet is deliberately left on. Forcing is always available, always explicit, and always logged.

The other direction is not symmetrical, because applying mains power harms nothing. Clicking an
outlet **on** just energises the outlet. Bringing the system itself up through its management
processor is a separate, deliberate choice — the outlet's `…` menu, or the machine list's On
button.

## Running it

Requires the .NET 10 SDK.

```bash
# The fake lab: an SNMP PDU and four management processors, no hardware needed.
dotnet run --project src/SysCmd.Server -- --simulate

# Real hardware, reading config/
dotnet run --project src/SysCmd.Server
```

Then open <http://localhost:5080>.

The simulator is worth using first — it exercises every code path, including a machine
(`alpha1000`) that deliberately ignores shutdown requests so you can watch the confirmation
timeout leave its outlet on.

## How it is put together

```
src/SysCmd.Core/        all the logic, no ASP.NET dependency
  Configuration/        YAML models, loading, validation, atomic saves, file watching
  Pdu/                  SNMP client and outlet/load translation
  Mp/                   expect-script engine, telnet transport, endpoint leasing
  Machines/             power orchestration - the safety logic lives here
  Jobs/                 channel-backed queue and background worker pool
  Power/                polling, CSV history, energy and cost maths
  Events/               activity log
src/SysCmd.Server/      ASP.NET Core host: REST API + Blazor UI + console WebSocket bridge
src/SysCmd.Simulator/   fake SNMP agent and fake management processors
config.example/         documented templates for real hardware
config.sim/             configuration pointing at the simulator
```

One process serves both the API and the UI. The Blazor components call the same services the API
wraps, in-process — the API exists so a CLI or TUI can drive the same lab later.

## Configuration

Plain YAML files, no database. Edit them by hand or through the GUI; either way the other side
picks the change up, because the app watches the config directory.

```
config/
  app.yaml              site name, poll interval, cost per kWh, power sequencing timeouts
  pdu-types/*.yaml      OIDs per PDU model      <- adding a PDU model is a new file
  pdus/*.yaml           each physical PDU
  mp-types/*.yaml       expect scripts per MP   <- adding an MP type is a new file
  console-servers/*.yaml serial port -> TCP port maps
  machines/*.yaml       outlet, MP, serial console, addresses
  groups.yaml           bulk power groups
```

`config/` is gitignored because it holds SNMP community strings and MP passwords in plain text.
Keep it readable only by the account running syscmd (`chmod 700`).

### Adding a PDU model

Write a file in `pdu-types/` naming the OIDs. `{outlet}` is substituted with the outlet number:

```yaml
name: APC AP7900
snmp: { version: v1 }
outlets:
  stateOid:   "1.3.6.1.4.1.318.1.1.4.4.2.1.3.{outlet}"
  controlOid: "1.3.6.1.4.1.318.1.1.4.4.2.1.3.{outlet}"
  commands: { on: 1, off: 2, reboot: 3 }   # value written to control the outlet
  stateMap: { 1: on, 2: off }              # value read back, mapped to a state
power:
  loadOid: "1.3.6.1.4.1.318.1.1.12.2.3.1.1.2.1"
  loadUnit: deciamps                       # watts | deciwatts | amps | deciamps
  nominalVolts: 120                        # only used to convert between watts and amps
  # perOutletWattsOid: "1.3.6...{outlet}"  # metered models only
  # perOutletUnit: deciwatts               # defaults to loadUnit; set when they differ
```

Readings are normalised to watts on the way in. A unit of `watts` is taken verbatim and amps are
derived as `watts / nominalVolts`; a current unit is multiplied by `nominalVolts` to get watts. So
`nominalVolts` only ever affects the figure the PDU does *not* report — a watts-reading 240 V unit
gets its watts used exactly as read.

### Adding a management processor type

Write a file in `mp-types/` describing the conversation. `expect` waits for a substring, or a
regex when written as `/pattern/i`. `send` transmits, appending a carriage return, and supports
`{username}`, `{password}`, `\r`, `\n`, `\e`, `\xNN` and `^X` control escapes.

```yaml
name: HP Integrity MP
transport: telnet
allowsConcurrentSessions: true   # this MP tolerates several logins at once
login:
  - { expect: "login:",    send: "{username}" }
  - { expect: "password:", send: "{password}" }
tasks:
  poweron:
    - { expect: "MP>",   send: "cm" }
    - { expect: "CM:",   send: "pc" }
    - { expect: "Quit:", send: "on" }
    - { expect: "(Y/",   send: "y" }
  status:
    - { expect: "MP>", send: "cm" }
    - { expect: "CM:", send: "ps" }
    # A step's match tests the output that arrived while waiting for its expect,
    # so this reads what "ps" printed on the way to the next prompt.
    - expect: "CM:"
      match:
        on:  "/System Power[^:]*:\\s*on/i"
        off: "/System Power[^:]*:\\s*off/i"
```

Only `status` needs a `match`; it is what makes confirm-before-cut possible, so a machine without
one can only be forced off.

## Concurrency and the single-wire problem

Whether a session has to be held exclusively depends on the route, not on a blanket rule:

- **Through a console server** — always exclusive. It is one physical serial wire, whatever is on
  the end of it.
- **Direct to an MP** — depends on the device. An HP Integrity MP and an iLO serve several logins
  at once; an ALOM does not. Declare it per type with `allowsConcurrentSessions: true` in the
  mp-type file. The default is `false`, which is the safe assumption.

Where a session is exclusive, every consumer — the MP driver and the browser terminal alike —
takes a lease first, so opening a console really does reserve the wire and a power job needing it
reports *"already in use"* within seconds instead of hanging. Where it is not, a console window
and a power job happily coexist.

A task that fails during connect or login is retried a few times with backoff: a service processor
that has just dropped a session often refuses the next one for a second or two. A task that fails
*part-way through* is never retried, because the power command may already have been accepted.

Separately, a per-machine lock stops two power jobs racing the same box.

## REST API

```
GET  /api/v1/status                     machines on, draw, energy, cost
GET  /api/v1/machines[/{id}]
POST /api/v1/machines/{id}/power        {"action":"on|off|reset","force":false}
GET  /api/v1/pdus[/{id}[/outlets]]
POST /api/v1/pdus/{id}/outlets/{n}      {"action":"on|off|reboot","force":false}
GET  /api/v1/groups
POST /api/v1/groups/{id}/power          {"action":"on|off"}
GET  /api/v1/jobs[/{id}]                job status and progress
GET  /api/v1/power/summary
GET  /api/v1/power/history?from=&to=&pdu=&maxPoints=
GET  /api/v1/events?limit=&level=&machine=
GET/PUT/DELETE /api/v1/config/{app|machines|pdus|groups|console-servers}[/{id}]
GET  /api/v1/config/types/{pdu|mp}      available driver definitions
WS   /ws/console/{machineId}?target=mp|serial
```

Power endpoints return `202 Accepted` with a job id; poll `/api/v1/jobs/{id}` to follow it, and
`POST /api/v1/jobs/{id}/cancel` to stop it. Stopping is safe at any point: whatever the job has
already done stands, and an outlet it had not yet switched is left alone — so a power-off killed
mid-shutdown leaves the machine's outlet on rather than cutting it.

```bash
curl -X POST localhost:5080/api/v1/machines/rp3440/power \
     -H 'Content-Type: application/json' -d '{"action":"on"}'
```

## Data on disk

```
data/power/YYYY-MM.csv        timestamp,pduId,watts,amps,volts
data/events/YYYY-MM-DD.jsonl  one event per line
```

Energy is the trapezoidal integral of the wattage samples, skipping gaps longer than ten minutes
so downtime is not billed as consumption.

## Security

There is no authentication. This is built for a trusted lab network, and the config files hold
SNMP community strings and MP passwords in the clear regardless. `LabAccess.RequireLabAccess()`
in `src/SysCmd.Server/Api/LabAccess.cs` is the seam where an API key or login goes; it is already
applied to every API route and the console WebSocket. Telnet is unencrypted, but that is a
property of the hardware, not a choice made here.

## Layout

The dashboard puts the event log beside the other windows when there is room and drops it below
when there is not. It is an ordinary window either way, scrolling with the rest of
the page; beside the content it stretches so its top and bottom finish level with the centre
stack. Its height therefore comes from the column next to it rather than from its own contents,
so an arriving entry cannot resize it. That decision uses a CSS container query on the content area rather than a
viewport media query, because showing or hiding the navigation changes the available width by
250px and a viewport query cannot see that.

The navigation toggle is pinned to the top-left corner of the viewport in a fixed strip, so it is
always in the same place. Navigation itself is a hamburger panel at every width. Its state is remembered in `localStorage`: shown
on a desktop it stays shown until hidden again, and below 900px it becomes a drawer over the
content, painted above the pinned strip. Until the user chooses, it follows the viewport, so the
server-rendered first paint is correct at any size with no JavaScript.

The PDU section carries two toggles, both remembered per browser. **Hide unassigned** is on by
default and drops outlets with no machine bound to them, saying how many it hid. **Skip
confirmation** drops the are-you-sure dialog before anything that takes power away, from the
outlet grid and the machine list alike; while it is on, its own label is highlighted as a warning. Group
power-offs still confirm regardless, since those move several machines at once.

Outlet buttons clip their status line rather than growing: the full text is on the tooltip. A
long progress message used to widen one button over its neighbour, because grid and flex items
default to `min-width: auto` and will not shrink below their widest unbreakable text.

Every forced action and both consoles are on the outlet's `…` button as well as its right-click
menu, so nothing is mouse-only. On a phone that menu becomes a bottom sheet. `tests/ui-smoke.mjs`
checks all of this in a real browser at desktop, narrow and iPhone viewports.

## Windows and theming

The UI is styled after CDE. The window decorations are **drawn in CSS**, not sliced from images:
a Motif frame is a stack of bevels, and every part of it — the title bar, its boxes, the menu bar,
the sunken client area — is a real element. That is what lets the decorations carry behaviour
rather than being painted on:

- The left title-bar box is the **window menu** (Restore / Minimise / Maximise / Close), exactly
  as Motif places it. Double-clicking it closes the window.
- The right boxes **roll the window up** and **maximise** it. Every window has a working minimise
  box; `Collapsible` is on by default.
- The **menu bar does something**. A console window's Window / Edit / Send / Options / Help
  pull-downs close it, copy and paste the buffer, send control keys (`^C`, `^D`, `^B`, Break),
  change the text size and reconnect the session.

`CdeWindow` is the single component behind all of this, and `CdeMenu` / `CdeMenuItem` describe a
pull-down. No colour is written down in `cde.css` at all: the stylesheet is generated from a real
CDE palette file, so a second theme is eight numbers in a `.dp` file with no changes to `cde.css` or
any component — which the image-based approach could not offer, since each theme needed its own
slice set. All 41 of CDE's palettes and 28 of its backdrops ship, and both are pickable at
`/config/appearance`.

### Console windows

A management-processor console carries a **Login** entry on its menu bar that replays the login steps from that
machine's mp-type file, so the session lands at the MP prompt without typing. The exchange runs on
the server, watching the same byte stream the browser is being shown rather than opening a second
reader on the session — which means it reuses the expect script the power jobs already use, and
works for any MP type. Serial consoles do not offer it: what is on the far end of a terminal
server port is anyone's guess.


Consoles open as real windows on the page, in a floating layer above it. They drag by the title
bar and resize from the corner grip, both handled in JavaScript so a pointer move does not make a
server round trip. `ConsoleWindowManager` is scoped to the browser circuit and lives above the
router, so a session survives navigating to another page — closing a console should be deliberate,
not a side effect of clicking Configuration. Opening the same target twice raises the existing
window instead of fighting over the endpoint lease. Below 720px a floating window fills the screen,
which is why there is no separate full-page console route to keep in step with this one.

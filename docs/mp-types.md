# Writing an mp-type

An **mp-type** teaches syscmd how to talk to one model of management processor — an HP MP, a Sun
ALOM or LOM, an iLO. It is a file, not code: supporting a new service processor should never mean
touching C#.

One file per model in `config/mp-types/`, named after the type. **The file stem is the id**, so
`config/mp-types/sun-lom.yaml` is referred to as `type: sun-lom` from a machine. `config.example/`
holds working files for the models already covered.

Everything below is what the code actually reads. Unknown keys are silently ignored — YAML is
parsed with `IgnoreUnmatchedProperties`, so a misspelled field does not error, it just does
nothing. If a setting appears to have no effect, check the spelling first.

## The shape of a file

```yaml
name: Sun LOM                     # shown in the GUI
transport: telnet                 # only telnet is supported
defaultPort: 23                   # used for a direct MP address; ignored when reached via a console server
allowsConcurrentSessions: false

timeouts:
  expectSeconds: 20               # how long any one step waits for its pattern
  connectSeconds: 10              # how long to wait for the TCP connection

login:                            # optional; runs once per connection
  - expect: "login:"
    send: "{username}"

tasks:                            # what syscmd can ask the machine to do
  poweron:  [ ... ]
  poweroff: [ ... ]
  reset:    [ ... ]
  status:   [ ... ]

logout:                           # optional; best-effort on the way out
  - send: "logout"
```

### Top-level fields

| Field | Type | Default | Notes |
|---|---|---|---|
| `name` | string | `""` | Display name. |
| `transport` | string | `telnet` | **Only `telnet`.** Anything else is a config *error*, and every power job for a machine of that type fails when it runs. This is the wire protocol, not the route — reaching an MP through a terminal server is still telnet, to the terminal server's TCP port. |
| `defaultPort` | int | `23` | Used when a machine gives `mp.host` without `mp.port`. Not used on the `via` path, where the port comes from the console server's map. |
| `allowsConcurrentSessions` | bool | `false` | Whether the MP tolerates two logins at once.  **Ignored when the MP is reached through a console server** — that is one physical wire whatever is on the end of it. |
| `timeouts.expectSeconds` | int | `20` | Default wait for each step's `expect`. |
| `timeouts.connectSeconds` | int | `10` | TCP connect timeout. |
| `prompt` | string | — | **Currently inert.** It is parsed and stored, and nothing reads it. Harmless to set; it will not resynchronise anything. |
| `blindShutdownSeconds` | int | — | Only consulted when this type has no working `status` task. How long to allow for a shutdown that nothing can confirm, after which the outlet is switched off anyway. Left out, the outlet stays on instead. See [Tasks](#tasks). |
| `login` | step list | empty | Optional. See below. |
| `logout` | step list | empty | Optional, best-effort — failures here never fail a job. |
| `tasks` | map of name → step list | empty | See below. |

## Steps

A step optionally waits for something, then optionally sends something.

| Field | Type | Notes |
|---|---|---|
| `expect` | string | Wait for this before going on. Omit to send immediately. |
| `send` | string | Text to transmit. **A carriage return is appended** unless `noNewline` is set. |
| `noNewline` | bool | Suppress that automatic carriage return. |
| `timeoutSeconds` | int | Overrides `timeouts.expectSeconds` for this step alone. |
| `delayMs` | int | Fixed pause *after* the step, for an MP that needs a moment to settle. |
| `optional` | bool | A step that never sees its `expect` is skipped instead of failing the task. |
| `match` | map | Turns the step into a state probe. See [status](#status). |

### Patterns

`expect` and the patterns inside `match` share one syntax:

- **Plain text** is a case-insensitive substring: `expect: "lom>"` matches `LOM>` too.
- **`/pattern/`** or **`/pattern/i`** is a regular expression, for when a substring is not enough:
  `expect: "/^MP>/"`.

### Sending text

`send` supports:

| Written | Sends |
|---|---|
| `{username}`, `{password}` | The credentials from the *machine's* `mp:` block, not from here |
| `\r` `\n` `\t` `\0` | carriage return, newline, tab, NUL |
| `\e` | escape (0x1b) |
| `\xNN` | one byte, in hex — `\x03` |
| `^A` … `^_` | a control character — `^C` is 0x03, `^]` is the telnet escape |
| `\\` `\^` | a literal backslash or caret |

Credentials live on the machine because two machines of the same model rarely share a password;
the mp-type only says *where* they go.

## Tasks

syscmd runs exactly four task names. Anything else in `tasks:` is never called.

| Task | When it runs |
|---|---|
| `poweron` | Bringing the system up through its MP |
| `poweroff` | The shutdown half of switching a machine off |
| `reset` | Reset through the MP |
| `status` | Whenever the power state is needed — after a power-on, and repeatedly while waiting for a shutdown to confirm |

### Defining only what the hardware has

**Leave out what this model cannot do.** Not every service processor has all four; plenty will
start a machine and offer nothing else. A missing task is a statement about the hardware, not an
unfinished file — syscmd greys that action out in the GUI and reports it over the API rather than
letting somebody start a job that could only fail. Each one is noted as a warning on the
Configuration page so the limitation is visible, not silent.

| Missing | What syscmd does instead |
|---|---|
| `poweron` | Powering the machine on applies outlet power and the job reports that is all it controls. Fine for a machine that starts itself when mains power returns. |
| `status` | There is nothing to probe with, so the power-on sequence becomes its own probe: it is re-offered until the MP accepts it, then reported as **sent but unconfirmed**. |
| `poweroff` | There is no shutdown to ask for, so powering the machine off **switches its outlet off directly** — the same thing that happens for a machine with no MP at all. The confirmation dialog says nothing will be shut down first, and the cut is logged as a warning. |
| `reset` | The reset action is refused up front, in the GUI and over the API. |

A task key written with **nothing under it** is a different thing and is reported as an *error*:

```yaml
tasks:
  poweroff:          # <- an error, not "unsupported"
  poweron:
    - expect: ">"
      send: "power on"
```

It reads as an edit somebody stopped half way through rather than a decision, so it is worth
hearing about. The operation is treated as unavailable either way — an empty script is never sent
at the hardware.

### Shutting down without confirmation

Note the asymmetry between the last two. With no `poweroff` there is nothing to wait for, so the
outlet goes off; with a `poweroff` but no `status`, the machine *was* told to stop and whether it
did is a question worth asking, so the outlet is held until it can be answered.

An MP that can be told to shut down but cannot report state is the one case with a choice in it.
By default the command goes out, nothing confirms it, and the outlet is **left on** with the job
failing — the same answer a confirmation timeout gives, for the same reason: nothing has said the
machine is down.

`blindShutdownSeconds` trades that for a fixed wait:

```yaml
# This model takes about two minutes to shut down and never says so.
blindShutdownSeconds: 120
```

The outlet is then switched off that long after the power-off sequence is sent. It is opt-in per
hardware type, so the decision is recorded in the file for the hardware it applies to, and every
cut made this way is logged at Warning. With a working `status` task it is ignored, and the machine
is asked rather than timed.

### status

`status` is the one task with a hard requirement: **at least one step must carry a `match` block**,
or it is reported as an error and treated as if there were no `status` task at all — a status task
that cannot report a state is not one.

```yaml
  status:
    - expect: "lom>"
      send: "environment"
    - expect: "lom>"
      match:
        on: "/CPU OK/i"
        off: "/CPU standby/i"
```

The subtlety worth knowing: **`match` runs against the text that step waited through**, so the
probe belongs on the step *after* the one that sends the command — it reads the output the command
produced. The first pattern that matches decides the answer; nothing matching leaves the state
`Unknown`, which is not the same as `off`.

This matters more than it looks. `status` is what the shutdown-confirmation guarantee rests on: an
outlet is only cut once `status` has said `off`. A `status` that cannot tell the difference will
make syscmd wait out the full confirmation timeout and then leave the outlet on.

## Login, and doing without one

`login:` runs once per connection, before any task. It is **optional** — plenty of service
processors drop straight to a prompt:

```yaml
login:
  - expect: "login:"
    send: "{username}"
  - expect: "password:"
    send: "{password}"
```

With no `login:` block, syscmd sends nothing and goes straight to the task. The console window's
**Login** entry greys out, since there is nothing for it to replay.

### Getting to a prompt on a shared serial line

Sun LOM and ALOM share the serial port between the system console and the service processor, and
`#.` is the escape that switches from one to the other. Since that is "get me to a usable prompt"
rather than a login, `login:` is where it belongs — and it makes the console's Login button do
something useful:

```yaml
login:
  - send: "#."
    noNewline: true       # the escape is the two characters, with no return after
    delayMs: 500          # give it a moment to switch
  - send: ""              # an empty send is a bare carriage return, to draw the prompt
```

`send: ""` is not a no-op: the payload is empty but the automatic carriage return still goes, so
the step sends exactly `CR`.

### The console-server nudge

When a machine reaches its MP through a console server, syscmd listens for two seconds before
doing anything, and only sends a bare carriage return if the far end said nothing at all. A
console-server port often lands mid-session with no banner until something is typed, but sending a
stray return to a device that greets immediately would be swallowed as the username. This happens
before `login:` runs, so scripts do not need their own wake-up step.

## Retries, and why a task is never retried

A failure **before any command has been sent** — a refused connection, a session dropped during
login — is marked transient and retried with backoff, because a service processor that just
dropped a session often refuses the next one for a second or two.

A failure part-way through a task is **never** retried. The power command may already have been
accepted, and running it twice is worse than reporting a failure.

## What the validator checks

Problems are reported, not thrown: one bad file marks that type broken and everything else still
loads. They show up on the Configuration page and in the event log at startup.

| Severity | Condition |
|---|---|
| **Error** | `transport` is anything but `telnet` |
| **Error** | `status` exists but no step in it has a `match` block |
| **Error** | a task key is present with no steps under it |
| **Error** | `blindShutdownSeconds` is zero or negative |
| **Warning** | no `poweron`, `poweroff`, `reset` or `status` task — repeated against each machine using the type |
| **Warning** | a task name syscmd never runs |
| **Warning** | `blindShutdownSeconds` set on a type that has `status`, or that has no `poweroff` |

A validation error does not remove the type: only a file that fails to *parse* disappears, and
machines still resolve their `type:` to it. So a bad `transport` is reported on the Configuration
page but the machines referencing it look fine, and the failure surfaces when a power job actually
runs:

```
No driver can handle MP transport 'apcterm'.
```

Worth knowing when a machine looks correctly configured and every power job fails the moment it
starts.

## A complete example

`config/mp-types/sun-lom.yaml`, for a LOM on a shared serial line reached through a terminal
server:

```yaml
# Sun Lights Out Manager - a flat "lom>" shell rather than nested menus. The serial line is
# shared with the system console, so every session starts by escaping to the LOM with "#.".
name: Sun LOM
transport: telnet
allowsConcurrentSessions: false

timeouts:
  expectSeconds: 20
  connectSeconds: 10

login:
  - send: "#."
    noNewline: true
    delayMs: 500
  - send: ""

tasks:
  poweron:
    - expect: "lom>"
      send: "poweron"

  poweroff:
    - expect: "lom>"
      send: "poweroff"
      timeoutSeconds: 60

  reset:
    - expect: "lom>"
      send: "reset"
      timeoutSeconds: 60

  status:
    - expect: "lom>"
      send: "environment"
    - expect: "lom>"
      match:
        on: "/CPU OK/i"
        off: "/CPU standby/i"

logout:
  - send: "logout"
```

and the machine that uses it:

```yaml
id: sunv100
name: SunFire V100
pdu:
  id: apcterm-pdu
  outlet: 7
mp:
  type: sun-lom
  via:
    server: apcterm     # console server id
    port: 16            # physical port on it, not the TCP port
  username: admin
  password: secret
serial:
  server: apcterm
  port: 16
```

## When something does not work

- **Every machine of one model is broken.** Its mp-type failed to load. Check the Configuration
  page for the reason; a bad `transport` is the usual one.
- **A task times out on its first step.** The MP is not showing the prompt the script waits for.
  Open the console and look: the transcript of a failed job is on the event log entry, under the
  job's detail, showing exactly what was sent and what came back.
- **A power-off waits the full confirmation window and leaves the outlet on.** `status` is not
  recognising the powered-down state. That is the safety behaviour working as intended, not a bug
  — fix the `match` patterns.
- **A power-off fails immediately saying the outlet was left on.** The type has a `poweroff` task
  but no `status` to confirm it with. Working as intended: add a `status` task, or set
  `blindShutdownSeconds`, or shut the machine down yourself and use Force Off. (A type with *no*
  `poweroff` at all does not fail — it switches the outlet off.)
- **A setting seems to do nothing.** Unmatched keys are ignored without complaint. Check the
  spelling against the tables above; `noNewline` and `delayMs` are camelCase, like every other key.

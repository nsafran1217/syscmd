# Smoke tests

Scripts that drive the running app against the simulated lab. `safety-smoke.mjs` uses the REST
API only; the rest drive a real browser.

`ui-smoke.mjs` checks the responsive layout and the touch paths: that the event log sits beside
the other windows on a wide screen and drops below on a narrow one, that the navigation toggle
sticks across reloads, and that the forced-power actions and consoles are reachable without a
right-click.

`window-smoke.mjs` covers the CDE window decorations: that the title-bar boxes roll a window up
and maximise it, that the pull-down menus act rather than decorate, that a console window drags
and resizes, that it survives navigating to another page, and that closing it releases the
endpoint lease.

`behaviour-smoke.mjs` covers the navigation layout, the PDU skip-confirmation toggle, and stopping
a running job — including that an outlet the killed job had not reached is left on.

`window-rollup-smoke.mjs` rolls a console window up and back, checking it returns to its original
size and can still be resized afterwards.

`config-edit-smoke.mjs` checks the machine editor shows credentials in the clear and that the
hostname Look up button fills in the IP, leaves it alone when the name does not resolve, and says
why.

`dashboard-smoke.mjs` covers the unassigned-outlet filter, that clicking an outlet on switches only
the outlet, the machine list's power controls, and that a long status message does not make an
outlet button outgrow its neighbours.

`log-column-smoke.mjs` checks the event log window's top and bottom finish level with the centre
stack, that it scrolls with the page rather than being pinned, that it does not change height as
new entries arrive, and that it still rolls up to its title bar.

`console-login-smoke.mjs` opens a console and checks the Login button reaches the MP prompt on
both an HP MP and an ALOM, that it reports completion rather than only appearing to work, and
that serial consoles do not offer it.

`window-menu-smoke.mjs` covers the layering inside a window while a pull-down is open: that a
double-click on the window-menu box closes the window, that the title bar and menu bar stay live,
and that clicking away still dismisses.

`safety-smoke.mjs` is the one to run if you only run one. It exercises the guarantees the design
exists for, over the REST API with no browser: that an outlet stays live until its machine
confirms it is powered down, that a machine which never confirms keeps its outlet, that forcing
overrides and is logged, and that killing a job leaves untouched outlets alone. It takes a few
minutes, because it waits out a real confirmation timeout.

`theme-smoke.mjs` covers the palettes and backdrops. It is the only suite that checks colour, and
it does so against numbers with an independent source rather than against whatever the code emits:
colours sampled from the real CDE screenshots in `docs/website_reference`. It checks that Motif's
colour derivation still turns Crimson's stored backgrounds into the shadows those screenshots show,
that a backdrop is stencilled in the colour set's background over its bottom shadow, that picking a
palette repaints without a reload and survives one, that random mode stays inside the chosen pool,
and that the frame still draws its eight corner joins. Worth knowing: every other browser suite
*captures* screenshots but never compares them, so without this one a colour regression is
invisible.

`power-smoke.mjs` points the simulated PDU at a watts load OID instead of a current one and checks
the two readings agree, that watts are used verbatim rather than scaled by voltage, and that
energy keeps accruing. It needs no browser.

They are plain scripts rather than a test project, so they need nothing but Node and Playwright's
Chromium. The `install-deps` step is not required on Ubuntu 24.04.

```bash
npm install                 # playwright, declared in the repo's package.json
npx playwright install chromium

# with the app running in another shell:
#   dotnet run --project src/SysCmd.Server -- --simulate
SHOTS=./shots node tests/ui-smoke.mjs         # responsive layout and touch paths
SHOTS=./shots node tests/window-smoke.mjs     # window decorations, menus, drag, resize
SHOTS=./shots node tests/behaviour-smoke.mjs  # navigation, confirmation toggle, killing a job
SHOTS=./shots node tests/window-rollup-smoke.mjs  # roll up, restore and resize a console window
node tests/power-smoke.mjs                    # watts vs current readings and energy accrual
SHOTS=./shots node tests/theme-smoke.mjs      # CDE palettes, backdrops and the Motif colour maths
SHOTS=./shots node tests/config-edit-smoke.mjs    # machine editor: credentials and DNS lookup
SHOTS=./shots node tests/dashboard-smoke.mjs      # outlet filter, outlet-only power-on, machine controls
SHOTS=./shots node tests/log-column-smoke.mjs      # the event log lines up and scrolls with the other windows
SHOTS=./shots node tests/console-login-smoke.mjs  # the console Login button, on an HP MP and an ALOM
SHOTS=./shots node tests/window-menu-smoke.mjs    # double-click to close, and menu layering
node tests/safety-smoke.mjs                       # the power-safety guarantees, over the API
```

Run one at a time. They drive the same simulated lab as each other and as the functional checks,
so running them concurrently makes each look intermittently broken.

`SHOTS` names a directory for the screenshots it captures at each viewport; it defaults to the
working directory. Exit status is non-zero if any check fails.

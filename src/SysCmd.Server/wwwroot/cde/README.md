# CDE palettes and backdrops

These files are copied verbatim from the Common Desktop Environment, which is licensed under the
GNU Lesser General Public License v2.0. Upstream paths, vendored in this repository under `docs/`:

- `palettes/` — `docs/CDE/cde/programs/palettes/*.dp` (41 palettes)
- `backdrops/` — `docs/CDE/cde/programs/backdrops/*.bm`, `*.pm` (29 backdrops)

They are data, not decoration. Nothing here is used as an image directly.

## Palettes

A `.dp` file is up to eight X colour specifications, one per line — and *only* a background for each
colour set. The foreground, top shadow, bottom shadow and select colour are all computed from it by
Motif's colour calculation, which `SysCmd.Core.Theming.MotifColors` ports line for line from
`docs/motif-code/lib/Xm/Color.c`. That is why adding a palette is eight numbers and nothing else.

Two of them are worth knowing:

- **Default** is CDE's own out-of-the-box palette, and it is what this UI was already wearing before
  any of this existed: colour set 1 `#eda870`, 2 `#999999`, 4 `#686f82`, 6 `#4992a7`.
- **Crimson** is the Solaris look. Sampling `docs/website_reference/img/sun-css/term-full.png` gives
  `#b24d7a` chrome over an `#aeb2c3` face, which is Crimson's colour sets 1 and 2 exactly.

Drop another `.dp` file in `config/palettes/` to add one for this lab; it wins over a shipped
palette of the same name.

## Backdrops

Backdrops are stencils, not pictures. An XPM's colour table names Motif resources rather than
colours:

```
"=    s bottomShadowColor m white c #636363636363",
"o    s background    m black c #949494949494",
```

and the loader substitutes the live colour set for those names, so one tile looks completely
different under each palette. X bitmaps (`.bm`) are the two-colour case of the same idea. dtwm
paints the root window in the colour set's *bottom shadow* and stencils the pattern over it in the
set's *background* — which is why a CDE desktop reads as a light pattern on a darker ground.

Colours named `iconGray1`–`iconGray8` are not Motif resources and keep the literal grey in the file,
as Motif does with any symbol it was not given an override for.

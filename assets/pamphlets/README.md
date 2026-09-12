# Resistance pamphlets — crate-loot propaganda drops

Handout images for the v1.10 pamphlet seeding (`OnLootSpawn` in RustQuests.cs): the
plugin loads JPGs from `oxide/data/RustQuests/pamphlets/` at init (`LoadPamphletCache`),
and a configurable fraction of spawning crates (`PamphletLootChance`, default 5%) get a
photo item stamped with a random print tucked in on top of the vanilla loot.

The voice is one faction — **The Unpaid** — ex-Cobalt ground staff who never got their
last check. Angry, purposeful, printed on whatever still runs. Half the sheets are
propaganda, half are guerrilla "field notes" whose tips are real gameplay hints. The
faction bible (and its v2 storyline role) is [docs/content/resistance.md](../../docs/content/resistance.md).
Never name a trader, never quote a grid or a code — the pamphlets know less than the
welcome note does.

## Constraints

- **1708×960 JPG** — 2× the native Rust photo-item resolution, so text stays sharp in
  the inspect view (FakeFriends v0.15.1 finding; sources still author at 854×480 CSS px).
- **≤ 512 KB each** (`LoadPamphletCache` skips bigger files), max 64 files loaded.
- **Frame-safe zone** — picture frames show a squarer center crop (~4:3), cutting
  ~107px off each side of a 16:9 print. Every source wraps its composition in
  `<div class="safe">` scaled to 72% about the center, so framed pamphlets keep all
  their text; the margins read as print margins in hand.
- **Dirt pass** — every source ends with a full-bleed `<div class="dirt">` multiply
  overlay (brown cast + edge vignette + stain blobs) so prints sit next to Rust's
  own grimy papers (e.g. the workbench blueprints) without glowing. Keep it on new
  sheets; tune the cast down before tuning text up if readability suffers.
- System fonts only in `src/*.html` — the renderer has no network.

## Pipeline

Sources are self-contained HTML in `src/` (xerox-zine aesthetic, same as FakeFriends).

```bash
powershell -ExecutionPolicy Bypass -File assets/pamphlets/render.ps1
```

renders every `src/*.html` via headless Chrome and writes the JPGs next to this file.

## Deploy

Copy `*.jpg` to `oxide/data/RustQuests/pamphlets/` on the server.
Takes effect on the next plugin load — `oxide.reload RustQuests` or server restart.
In-game check: `rq.pamphlet.give` hands you a random print.

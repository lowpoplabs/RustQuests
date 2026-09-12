# RustQuests

*"Every season tells a different story."*

A story-driven quest system for low-pop Rust servers. Four named traders live in train cabooses hidden across the map — lights on, slot machines running — and hand out chained quest lines that end in a shared vault only a player all four have paid out can open. Underneath them, a faceless faction seeds propaganda into crate loot and pays for dead drops. And each season, a **story generator** rolls one of six authored storylines and remixes it — who's in it, what order the chain runs, which secrets are true, what arrives mid-wipe, and what your verdict at the vault costs you afterward. A season outlives the map: a wipe rebuilds the world but keeps the story and everyone's progress until somebody opens the vault (capped, `SeasonCarryOverMaxWipes`).

**Status: v3 — v1 (the four chains), v2 (the per-wipe story generator, verdicts included), and v3 (the traders speak) complete.** History in [CHANGELOG.md](CHANGELOG.md).

## What it does

- **Four traders with identities** — Sonia (jungle, hunting), Rebecca (temperate, farming/electrics), Alexa (arid, vehicles), Olivia (arctic, munitions). Each stands in the cashier booth of her own hidden caboose: fixed face, authored outfit and dialog, a chain of quests in her own register, and a shop that re-hides itself in her biome every wipe. They're a **chain, not a menu** — each trader must vouch for you before the next offers work, and the cross-trader vault finale needs all four chains done.
- **A story that changes every wipe** — a seeded wipe bible picks one of six templates: *Four Keys* (the classic), *The Press* (one of them prints the resistance pamphlets), *The Manifest* (40 seats, 200 people — who had one?), *Yours, C.* (who signs the welcome note?), *The Recall* (someone's working the buried magazine), *The Count* (the wipe tally goes public — the island reads its own memory aloud). Templates shuffle the chain order, deal secret roles, roll secrets and quest variants, schedule mid-wipe beats, and end in a **verdict** at the vault.
- **Verdicts with teeth** — your pick at the finale recolors the ending, and then the island answers: traders hold personal grudges (from one cooled line all the way to a full eviction — counter, chat, *and her gambling machines* closed to you), delayed paper and parcels arrive real days later, and one verdict earns a telegraphed, survivable, lootable night visit from three unsigned guns in company hazmats — which the next pamphlet denies ever happened.
- **The Unpaid** — ex-Cobalt ground staff who may not exist. Their pamphlets appear in crate loot as inspectable photo prints, their field sheets grant real quests on pickup (dead drops, pamphlet planting), and they count the wipes — the story generator quotes last wipe's actual events back at your players.
- **Optional free-form trader chat (SLM)** — point the config at any OpenAI-compatible endpoint (a local Ollama or OpenRouter) and each trader gains a Chat button: in-character small talk with per-player memory, warmth that grows as you clear her chain, and secrets she keeps by omission but can *slip* at high warmth. Leave the endpoint empty and the feature is invisible — every quest, verdict, and consequence works fully scripted.
- **Optional trader voice (TTS)** — with the companion `RustQuestsVoice` plugin, every dialog line and chat reply is **spoken aloud** from a radio behind her counter, positionally, on completely vanilla clients (the audio rides Rust's own cassette mechanism). Each trader has her own voice (Kokoro-82M stock voices, configurable), lines render once and replay instantly from a disk cache, stage directions and objective counters stay on screen and out of her mouth, and a TTS failure never blocks the text. Works against a local GPU (Kokoro-FastAPI) **or OpenRouter** (`hexgrad/kokoro-82m`) — same voices either way, so a server with no GPU sounds identical.
- **Data-driven everything** — quests, dialog, notes, stash loot, trader profiles, story templates, and role overlays are editable JSON under `oxide/data/RustQuests/`. New shipped content merges in without overwriting live edits; scoped `rq.*.sync*` commands push authored changes deliberately.
- **Solo-friendly by design** — no PvP objectives, everything completable alone, per-player progress, any number of players racing the same wipe's story.

## Requirements

- A Rust server running **Oxide** (Carbon untested).
- **Pamphlet prints (recommended):** the resistance pamphlets are JPGs the plugin stamps onto photo items. The release zip carries them under `data/RustQuests/pamphlets/` (drop the `data` folder into `oxide/`); they are also in this repo's `assets/pamphlets/` — **without them, pamphlet crate drops and beat-published prints silently stay off** (the loot hook only arms when prints exist and `PamphletLootChance` > 0). Everything else works without them, but The Unpaid lose their voice in the loot table.
- **SLM endpoint (optional):** any OpenAI-compatible chat endpoint for free-form trader chat. Empty `SLMEndpoint` (the default) disables the whole feature cleanly.
- **Trader voice (optional):** two extra files — `RustQuestsVoice.cs` (plugin) and `Oxide.Ext.RustQuestsVoice.dll` (extension, built from [voice-ext/](voice-ext/README.md)) — plus any OpenAI-compatible **speech** endpoint: a local [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI) container, or OpenRouter with an API key (`hexgrad/kokoro-82m`, `TTSFormat: "pcm"` — their endpoint doesn't serve wav). Core RustQuests has no dependency on either file; servers that skip them lose nothing but the audio.
- **CopyPaste (optional):** only if you switch a trader's profile to `"ShopMode": "paste"`. The default caboose shops need nothing.

## Install

Drop `RustQuests.cs` into `oxide/plugins/`, then copy the pamphlet JPGs into `oxide/data/RustQuests/pamphlets/`. On first load the plugin writes its config and all content files; after a map wipe it rolls the wipe's story and places every shop, stash, and note by itself.

**Voice (optional):** copy `Oxide.Ext.RustQuestsVoice.dll` into `RustDedicated_Data/Managed/` and **restart** (extensions load at boot only), drop `RustQuestsVoice.cs` into `oxide/plugins/`, then set `TTSEndpoint` (plus `TTSApiKey`/`TTSModel`/`TTSFormat` for hosted endpoints — the config's own key text carries the exact local and OpenRouter values) in `oxide/config/RustQuestsVoice.json`.

## Permissions

- `rustquests.admin` (name configurable via `AdminPermission`) — grants the `rq.*` admin commands to non-authlevel-2 players, and exempts the holder from the shop no-build zone. Server console and authlevel 2 always work.

## Player commands

| Command | Purpose |
|---|---|
| `/quest` or `/quests` | Quest journal — **The Work** (chain progression, where to go next) and **The Story** (re-readable history in this wipe's voices) |
| `E` on a trader | Talk: accept quests, claim rewards, pick your verdict at the vault |
| **Chat** button (dialog header) | Free-form talk with an unlocked trader — only present when an SLM endpoint is configured |

Everything else reaches players by itself: the welcome note on first wake, pamphlets in crates, field sheets that grant work on pickup, auto-claiming dead drops, and whatever the wipe's verdict sends after them.

## Admin commands (console; `rq.ui.*` are internal UI plumbing, not listed)

**Status & the wipe's story**

| Command | Purpose |
|---|---|
| `rq.status` | Everything at a glance: wipe id, tree, shops, traders, stashes/codes |
| `rq.bible [secrets]` | This wipe's rolled story: template, chain order, arcs, slots, roles, beats, monuments, choice votes (`secrets` reveals the spoilers) |
| `rq.bible.reroll [template]` | Dev: reroll the wipe's story (optionally forcing a template). Orphans in-flight arc progress — test boots only |
| `rq.template.sync [id]` | Push shipped template definitions onto templates.json (discards live edits to those entries) |
| `rq.migrate.check` | Dry-run of the v2 data migration against a live quests.json |
| `rq.beat.fire <id>` | Dev: force a scheduled beat past its clock and gate |
| `rq.piece.status [player]` / `rq.piece.deliver [player]` | Delayed verdict mail: what's scheduled and due when / force everything pending |
| `rq.crew.visit [player]` / `rq.crew.end [now]` | Force the collections crew's night call (drill mode for non-pickers) / wind an active visit down |

**Quest & content sync**

| Command | Purpose |
|---|---|
| `rq.quest.list` | Loaded quest definitions and the active tree |
| `rq.quest.reset <player>` | Reset one player's quest progress |
| `rq.quest.synctext [quest-id]` | Push shipped quest wording; leaves tuned rewards alone |
| `rq.quest.syncrewards <trader\|quest-id>` | Push shipped objectives/rewards — discards live tuning for what it touches, so scope it |
| `rq.tree <jungle\|temperate>` | Override the wipe's quest tree |
| `rq.unlock <player> <trader>` | Vouch a player through to a trader manually |

**Traders & shops**

| Command | Purpose |
|---|---|
| `rq.trader.spawn` / `rq.trader.remove` / `rq.trader.tp` | Manual trader lifecycle / teleport |
| `rq.trader.reload` | Re-read traders.json + overlays and respawn (live editing) |
| `rq.trader.synctext [trader]` | Push shipped trader dialog; leaves face/kit/wiring alone |
| `rq.trader.face [n]` / `rq.trader.nudge <±m>` / `rq.trader.move` | Appearance and position tuning |
| `rq.shop.autoplace` / `rq.shop.paste` / `rq.shop.remove` | Place at a random valid spot / place here / remove |
| `rq.shop.offset` / `rq.shop.lights` / `rq.shop.biomescan` / `rq.shop.diagnose` | Placement calibration and diagnostics |

**Notes, stashes & pamphlets**

| Command | Purpose |
|---|---|
| `rq.note.place <tag>` / `rq.note.remove <tag>` / `rq.note.synctext [tag]` | Lore note boxes + wording sync |
| `rq.note.welcome` | Hand yourself the welcome note |
| `rq.stash.place <tag> <code>` / `rq.stash.remove <tag>` / `rq.stash.reroll` / `rq.stash.syncloot [tag]` | Codelocked stash management + loot sync |
| `rq.pamphlet.status` | Every print in the press and its rotation verdict (arc-gated prints show why they're held) |
| `rq.pamphlet.give` | Hand yourself a random print |

**SLM, voice & dev**

| Command | Purpose |
|---|---|
| `rq.slm` | Endpoint status, breaker, budget, live sessions |
| `rq.slm.prompt <trader> [tier]` | Preview the composed system prompt without an SLM call (tier previews warmth slips) |
| `rq.slm.test <trader> <msg>` | Round-trip the endpoint without a player |
| `rq.voice.status` | Voice engine at a glance: endpoint, per-trader voices, cache, breaker *(RustQuestsVoice)* |
| `rq.voice.test <trader> [text]` | Render + play a line at your position in her voice *(RustQuestsVoice)* |
| `rq.voice.rebuild` | Clear the voice cache — lines re-render on next play *(RustQuestsVoice)* |
| `rq.overlay.synctext` | Push shipped role overlays onto overlays.json |
| `rq.wipe.reset` | Dev: full state reset (new wipe id, fresh story roll) |
| `rq.dev.spawn <prefab>` / `rq.dev.kill` | Prefab spawn probe |

## Data files (`oxide/data/RustQuests/`)

| File | Contents |
|---|---|
| `traders.json` | Trader profiles: identity, outfit, shop, dialog, persona, warmth tiers, eviction/frozen voices |
| `quests.json` | All quest definitions: objectives, rewards, dialog, prerequisites, tree/arc tags |
| `templates.json` | The six wipe-story templates: chains, roles, secrets, slots, pools, beats, verdicts, consequences |
| `overlays.json` | Per-`trader.role` recolors applied at read time on role-rolling wipes |
| `notes.json` | Every piece of paper: lore notes, field sheets, archive sheets, verdict mail |
| `stashes.json` | Stash loot tables by tag |
| `pamphlets_meta.json` | Per-print gates: which arc a print needs before it circulates, what a print grants on pickup |
| `pamphlets/*.jpg` | The prints themselves (copy from `assets/pamphlets/` — see Requirements) |
| `state.json` | World state: wipe id, the rolled bible, placements, cross-wipe memory |
| `players/<steamid>.json` | Per-player progress: chains, choices, trader memories, delivered consequences |
| `voice/cache/*.ogg` | Rendered voice lines, keyed by content hash — safe to delete any time (`rq.voice.rebuild`) |

Editing content takes effect on plugin reload (`rq.trader.reload` for trader/overlay files, no full reload needed). Shipped updates merge **add-only** — your live edits survive; the scoped sync commands exist for when you *want* shipped values to win.

## Configuration

Notable options in `oxide/config/RustQuests.json`:

- `AutoPlaceOnWipe`, `ShopMapMarker`, `ShopLightsOn`, `ShopBiomeStrict`, `ShopClearTrees` — shop placement behavior (plus flatness/clearance/retry tuning)
- `ShopNoBuildRadiusMetres` — no player construction near a shop (default 25 m; other plugins can query it; 0 disables)
- `ProtectWorldObjects`, `TraderGodMode` — indestructible world objects and traders (default true)
- `WelcomeNote` — hand every player the welcome note once per wipe (default true)
- `PamphletLootChance` — chance a spawning crate carries a pamphlet (default 0.05; needs JPGs in the pamphlets dir; 0 disables)
- `SLMEndpoint`, `SLMModel`, `SLMTimeoutSeconds`, `SLMMaxTokens` — free-form trader chat; empty endpoint disables everything SLM
- `SLMHourlyBudget`, `SLMSessionMinutes`, `SLMSessionCooldownMinutes`, `SLMChatPanel` — chat pacing and delivery (CUI panel vs. proximity game chat)
- `JungleSampleThreshold`, `DialogCloseRangeMeters`, `PasteHeightOffset` — tree selection and interaction tuning

And in `oxide/config/RustQuestsVoice.json` (companion plugin):

- `TTSEndpoint`, `TTSModel`, `TTSApiKey`, `TTSFormat`, `TTSTimeoutSeconds` — the speech endpoint; empty endpoint disables voice; the key text carries the exact local (`wav`) and OpenRouter (`pcm`, `hexgrad/kokoro-82m`) values
- `Voices` — trader → voice id map (shipped: Sonia `af_jessica`, Olivia `af_aoede`, Alexa `af_river`, Rebecca `bf_lily`)
- `LoudnessDbfs`, `VoiceSpeed`, `MaxSpokenChars`, `RadioOffset` — loudness target, rate, speech-length cap, and where the hidden radio sits

## Credits

Built for a low-pop home server. Character names originally inspired by 7 Days to Die trader mods (Sonia, Olivia, Alexa, Rebecca) — no assets from those mods are used or portable; Rust has no client-side asset delivery. NPC combat techniques for the collections crew derive from the companion FakeFriends project's live-verified recipes. Trader voices are [Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M) (Apache-2.0); the voice extension embeds [.NET-Ogg-Vorbis-Encoder](https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder) (MIT).

### Clothing skin creators

The traders' looks are built from Steam Workshop clothing skins, shipped in the default trader profiles (per-trader `shortname@skinid` entries in `traders.json` — swap them there to recast). Shoutout to the creators whose work dresses the four:

- **MissLisa** — all four traders' hair is her skinned balaclavas: [cruella hair](https://steamcommunity.com/sharedfiles/filedetails/?id=2879706650) (Sonia & Olivia), [Red Black Hair](https://steamcommunity.com/sharedfiles/filedetails/?id=2879464636) (Alexa), [rose black hair](https://steamcommunity.com/sharedfiles/filedetails/?id=2879714377) (Rebecca) — plus Alexa's [short red/black Top](https://steamcommunity.com/sharedfiles/filedetails/?id=2643203649)
- **ViperTheBandit** — [Woodland Crop Top](https://steamcommunity.com/sharedfiles/filedetails/?id=805920755) (Sonia) and [Urban Crop Top](https://steamcommunity.com/sharedfiles/filedetails/?id=805920497) (Olivia)
- **Woodhead** — [Woodland Camo](https://steamcommunity.com/sharedfiles/filedetails/?id=899216250) pants (Sonia)
- **Val** — [Snow Camo Pants](https://steamcommunity.com/sharedfiles/filedetails/?id=835246371) (Olivia)
- **badtRIP ☣** — [White Puffer Jacket v2 \[CUT OUT\]](https://steamcommunity.com/sharedfiles/filedetails/?id=3332279029) (Olivia)
- **Krovv** — [The Mechanic Pants](https://steamcommunity.com/sharedfiles/filedetails/?id=1402353612) (Alexa)
- **S.Olli/Stummi** — [D´D Tank Top Pink1](https://steamcommunity.com/sharedfiles/filedetails/?id=2548737618) (Rebecca)
- **Napero** — [Pink Camo Pants](https://steamcommunity.com/sharedfiles/filedetails/?id=829596538) (Rebecca)

## Support

Provided as-is. Bug reports welcome via GitHub Issues. No Discord, no custom work, no promises on turnaround. If it saved you time or you and your players enjoy it:

[![Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/lowpoplabs)

## License

MIT — see [LICENSE](LICENSE).

## Compatibility

- Built against the September 3, 2026 Rust update (build 2633.288). Versions from 2.20.2 onward need that build or newer; the previous version is the last one that compiles on August builds.

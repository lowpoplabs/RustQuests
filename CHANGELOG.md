# Changelog

All notable changes to RustQuests. Format loosely follows [Keep a Changelog](https://keepachangelog.com/); versions before the first public release (2.20.3) were internal milestones.

## [2.20.4] — 2026-09-19 — traders stand in their booths on a fresh install

### Fixed
- **Traders spawned under their cabooses on a fresh install** (first public-release bug report). The booth stand-point is stored per trader as an offset from the caboose prefab origin, and the shipped defaults never set it: every new `traders.json` got all-zero offsets, which is the prefab pivot, about 1.4 m below the floor and 6 m from the booth. The live-calibrated offsets (`rq.shop.offset`, July 2026) are now the shipped defaults, the same back-port 2.20.1 did for the outfits.
- Existing installs self-heal: the trader merge now treats all-zero offsets as "never calibrated" and fills the shipped values (the pivot is never a real stand-point, so nothing tuned is touched; any non-zero operator calibration stays). Deploy the .cs and run `rq.trader.reload`, or restart, and she climbs into the booth.

## [2.20.3 + RustQuestsVoice 1.3.1] — 2026-09-12 — first public release

First public release on GitHub — no gameplay changes.

### Added
- One-line load message in each plugin naming the author and tip jar (`RustQuests v2.20.3 loaded - by LowPopLabs - ko-fi.com/lowpoplabs`; same for RustQuests Voice 1.3.1).
- README Support section with the Ko-fi button; support posture is as-is, issues welcome.
- The release zip now carries the pamphlet prints under `data/RustQuests/pamphlets/`, so the `data` folder drops straight into `oxide/`.

### Changed
- README skin credits name the creators and link their Workshop items, without Steam profile links.

## [2.20.2] — 2026-09-03 — September update compatibility

### Fixed
- Compiles again on the September 3, 2026 Rust update (build 2633.288): the game removed `BaseEntity.SetFlag`, so entity flags are now set through the new flags-update scope with a network update, exactly what the old call did. No behaviour change. Two sites: powering compound lights and the lock on a finale stash.

## [2.20.1] — 2026-08-31 — shipped trader looks match live

### Changed
- **The live-tuned trader outfits are now the shipped defaults.** `DefaultTraders()` carried placeholder kits from 2026-07-26 (Sonia and Olivia plain vanilla; Alexa and Rebecca wearing Sonia's look) while the real per-trader Workshop skins lived only in the live server's `traders.json`. Back-ported verbatim: each trader ships her own skinned kit (tank top + pants + the balaclava skin that *is* her hair; Olivia adds the puffer jacket) and all four ship the shared calibrated `FaceSeed` (previously only Alexa/Rebecca had it — a fresh install rolled random faces for Sonia and Olivia).
- README gains a **Clothing skin creators** credits section (all 12 skins linked, 8 creators). Fresh installs only — the trader merge is add-only and no sync command pushes kits, so live servers are untouched by design.

## [RustQuestsVoice 1.3.0] — 2026-08-25 — long lines chunk instead of truncate

### Fixed
- **Long story lines were cut off mid-story** (the owner's report: Sonia's backstory, likely others). The cause was 1.0.0's deliberate shortcut ("sentence-split became sentence-truncate"): anything past `MaxSpokenChars` (400) was silently dropped so the line fit one 30s cassette — and the chain-complete dialogs run 424–525 chars *before* token expansion, so their payoff sentences ("Tell her Sonia vouched for you… Then we talk about the last door.") never played. Now the sentence-split is real: a long line splits at sentence boundaries into cassette-sized chunks and the stop timer — which already lands inside the trailing silence pad — chains the next chunk onto the same cassette (`FileStorage` swap + replay, ~0.7s pause between chunks, reads as a paragraph break). The screen text was never affected.

### Details
- Chunks cache individually; **chunk 1 of a long line hashes identically to the old truncated render**, so the 37 live cached lines stay warm and only the tails render fresh. `SeqGen` on the rig makes a new line (latest-wins queue, dialog switch) cancel any pending continuation; a mid-sequence render failure drops the rest of that line and feeds the breaker as before. Sanity ceiling of 4 chunks (~100s) in place of the old hard cut.
- `MaxSpokenChars` config key renamed (now describes the chunk size) — the live value migrates in the same deploy, per the 2.15.1 rule. `rq.voice.test` now reports chunk count.

### Deploy notes
- Deployed live 2026-08-25 (hot-load clean, config migrated, cache carried). Hear it: open Sonia's chain-complete dialog (any line >400 chars) — she should finish the thought now, two tapes back to back.

## [2.20.0] — 2026-08-22 — season carry-over (Phase S)

*"Every season tells a different story."* Decision [0006](docs/decisions/0006-season-carry-over.md).

### Changed
- **A map wipe no longer ends the story by itself.** At a wipe boot (`OnNewSave` or the map changing under the state) the **season** — the rolled wipe bible AND every player's progress — now CARRIES unless some player has claimed the vault (`grand_finale` in the server-wide `WipeCompleted` ledger) or the season has already been carried `SeasonCarryOverMaxWipes` times; only then does the old reset + reroll path run. What carries: template, chain order, roles, secrets, variant picks, turn-in rolls, beats (fired flags and the `WipeStartUtc` clock — pending beats still land on their real day), choice state, FIELD NOTES №, `WipeId` (so progress files stay valid untouched), `WipeCompleted`, cross-wipe memory un-distilled (`{lastwipe.*}` keep quoting the previous *season*). What is rebuilt for the new map, exactly as on a fresh wipe: shops (the 60 s auto-place), finale stashes (new spots + codes), notes, dead-drop boxes, the welcome-note ledger (the paper went with the map — handed again), and **monument bindings are re-surveyed** (`RebindMonuments`; a requirement the new map can't meet binds to any monument with a warning rather than dropping already-active content). `QuestTree` is re-detected per map (Sonia's hunts 4/5 share Ids and objective shapes across trees, so in-flight counters stay coherent). `{wipes}` still ticks on a carried wipe — the island did reset; the season is the story.
- State gains `MapKey` (the map-change detector — `WipeId` now names the season and may outlive the map; pre-2.20 state adopts the current map on first load) and `CarriedWipes` (consecutive carries; zeroed by any real reset). `rq.status` shows the map key; `rq.status` and `rq.bible` both print a season line: `carried n/max, vault OPEN|unclaimed — next wipe boot will CARRY|RESET (why)`.

### Added
- Config `SeasonCarryOverMaxWipes` (default **2**; `0` = every wipe resets, the old behaviour).
- `rq.wipe.carry` — drill lever: simulate a carried boundary on the running server (tear the world down, keep story + progress, re-detect tree, rebind monuments, re-place everything). `rq.wipe.reset` is unchanged and remains the manual "end the season now".

### Deploy notes
- Mid-wipe hot-load is a no-op for play: no wipe is detected, `state.json` gains `MapKey`/`CarriedWipes: 0`, no sync commands needed. The live *Yours, C.* season (`yours_c`, wipe 7, gate unclaimed — verified in `state.json` after the deploy) then survives the coming wipe as carry 1/2 — look for `season CARRIED (1/2): grand_finale unclaimed` in the boot log, then the usual 60 s shop placement.

## [2.19.0] — 2026-08-19 — tech tree lock goes per-player

### Changed
- **A locked tech tree now lifts per player when they finish the bench's gate trader's chain** — the `WorkbenchGate*` mapping does double duty, so the shipped defaults become: finish Alexa → build a T2 bench AND scrap-unlock its tree; likewise Olivia→T3, Rebecca→Engineering. This closes the teammate loophole (standing at somebody else's bench no longer opens a tree you haven't earned). A bench with `TechTreeLock* = true` but an EMPTY `WorkbenchGate*` keeps the 2.17.0 absolute lock (research-only for everyone). Blocked players at a gated bench get `TechTree.LockedUntil` naming the trader; the ungated absolute lock keeps the old line. No config keys added or renamed — live values untouched.

## [2.18.0] — 2026-08-19 — workbench quest gates

### Added
- **Workbench quest gates** — four config keys (`WorkbenchGateWorkbench1/2/3`, `WorkbenchGateEngineering`) each naming a trader; a player can neither **craft** nor **place** that bench until that trader's chain is done (`WarmthTier >= 2`, the traders' own "her list is clear" bar). Shipped defaults: Wb1 ungated, Wb2←alexa, Wb3←olivia, Engineering←rebecca (the engineering bench is item `iotable`). Mechanisms: `CanCraft` (fires before ingredients are taken) + a workbench branch in `CanDeployItem` (catches placement of a bench acquired any other way) — both verified against the live assembly 2026-08-19. Gates are per-player and reset each wipe with progress. No admin exemption (a progression rule, like the tech tree lock). Boot warns if a gate names an unknown trader key.
- Composes with 2.17.0's tech tree lock: chains earn the *bench* (craft level for researched BPs); the tech tree above tier 1 stays research-only regardless.

### Limits (Rust's own mechanics, documented not fought)
- Static monument benches can't be gated, and a bench placed by an unlocked player grants craft level to anyone standing at it.

### Deploy notes
- Live config gains the four keys with the defaults above on first load — **the gates turn ON at deploy**. Empty a key + `oxide.reload RustQuests` to ungate that bench.

## [2.17.0] — 2026-08-16 — tech tree lock

### Added
- **Tech tree lock** — four config gates (`TechTreeLockWorkbench1/2/3`, `TechTreeLockEngineering`) that block the scrap-unlock path at that bench's tech tree; blocked blueprints must be researched at a research table (or won by experiment) instead. Shipped defaults implement "tier 1 stays open, everything above must be researched": Wb1 open, Wb2/Wb3/Engineering locked. Mechanism: `OnTechTreeNodeUnlock` (fires inside `RPC_TechTreeUnlock` before any scrap is taken; non-null blocks both the normal path and the upgrade bypass/prototype path — verified against the live assembly 2026-08-16). Engineering bench identified by `Workbench.isIOBench`, tested before `Workbenchlevel`. Hook armed only when at least one gate is on; blocked players get one Lang line (`TechTree.Locked`) per attempt.

### Deploy notes
- The live config gains the four keys with the shipped defaults on first load — i.e. **the lock turns ON at deploy** (Wb2/Wb3/Engineering). Flip keys to `false` in `oxide/config/RustQuests.json` + `oxide.reload RustQuests` to relax it.

## [2.16.1 + RustQuestsVoice 1.2.0 + ext 0.3.0] — 2026-08-14 — OpenRouter parity PROVEN; v3 engineering complete

### Fixed
- **Quest titles were spoken** — the offer body is `"{q.Name}\n\n{q.OfferText}"` for the screen; `OpenDialog` gained a `speak` override and the Available branch passes the offer prose alone to the voice hook. Screen unchanged.
- **OpenRouter voice 400'd** — their speech endpoint accepts ONLY `response_format: mp3|pcm` (wav rejected with a ZodError). Extension 0.3.0 adds `Format: "pcm"` (raw 16-bit LE mono @ 24 kHz — decoder-free, better than wav); plugin 1.2.0 exposes `TTSFormat` (wav local / pcm OpenRouter).

### Verified
- **The parity rule end to end (the owner, live): scripted quest audio AND SLM chat audio both played through OpenRouter** (`https://openrouter.ai/api/v1/audio/speech`, `hexgrad/kokoro-82m`, pcm) — a GPU-less server reproduces the identical voices with three config values. Day-to-day config reverted to the local endpoint; **v3's engineering is complete**.
- `RQVoicePoc` retired from the server (source archived in `dev/`).

### Changed
- Both plugins' config keys now carry the OpenRouter examples inline (voice: endpoint + `hexgrad/kokoro-82m`; core SLM: `https://openrouter.ai/api` + `google/gemma-3-12b-it`) — live config values migrated under the renamed keys in the same deploys.

## [RustQuestsVoice 1.1.0 + ext 0.2.0] — 2026-08-14 — first live-test feedback round

the owner's five findings from the first real listen, all addressed:

### Fixed
- **First words repeating at the end of every line** — cassette playback LOOPS at the ogg's end; the old stop timer (+0.8 s) let the loop replay the opening. Extension 0.2.0 pads every render with a trailing second of silence (reported `Seconds` stays speech-only) and the plugin now stops just inside the pad — loop point lands in silence, tail never clipped. *Extension DLL replaced in `Managed/`; loads at the NEXT restart — run `rq.voice.rebuild` once after that restart to purge unpadded cached renders.*
- **Stage directions spoken aloud** — leading third-person narration paragraphs ("She doesn't look up from the bench.", "The woman by the door doesn't step aside.") are dropped from speech (first paragraph only, and only when it opens third-person with no I/you/{0} markers). Screen text unchanged.
- **Still a little quiet** — new `LoudnessDbfs` config (default −13, was effectively −16); loudness is now part of the cache hash, so a tuning change invalidates stale renders automatically.

### Changed
- **Rebecca recast: `bf_lily`** (the owner's call after hearing af_nicole in place).
- **Radio hidden — by geometry only (1.1.1, same day).** Both entity-hiding tricks fail on the client: LootableObjects' `limitNetworking` never networks the entity (no client entity, no audio), and `Flags.Disabled` **mutes the audio too** (1.1.0 shipped it as `RadioHide`; the owner proved it silent live within the hour — the flag is removed, don't reintroduce it). The mechanism is the default `RadioOffset` sinking the box into the counter behind her, which the owner confirmed works.

## [2.16.0] — 2026-08-14 — the traders speak

**v3 Phase C: trader voice is live.** Dialog lines and SLM chat replies play audibly from a radio behind each trader's counter, on completely vanilla clients. Three artifacts ship together; only the first is required:

- **RustQuests.cs 2.16.0 (core)** — two new broadcast hooks, nothing else: `OnRustQuestsTraderLine(player, traderKey, body, greeting, traderPos, traderYaw)` fired from the `OpenDialog` chokepoint, and `OnRustQuestsTraderChat(...)` fired on each delivered SLM chat reply. Voice stays a separate OPTIONAL install because an Oxide plugin that references an extension **fails to compile when the DLL is absent** (observed live 2026-08-14) — a hard dependency would have broken every voiceless install.
- **RustQuestsVoice.cs 1.0.0 (new companion plugin)** — the voice engine: per-trader radio rigs (self-powered boombox + long tape spawned at her counter, `EnableSaving(false)`, stamped `OwnerStamp` so the shop sweep cleans them on re-place), disk cache keyed sha1(model|voice|speed|line) under `oxide/data/RustQuests/voice/cache/` (renders once per wipe-token expansion, byte-identical thanks to the extension's deterministic vorbis serial), one-deep latest-wins render queue per trader, the SLM-style 3-strike/5-min breaker, and a speech sanitizer (markup stripped, objective bullet lines never read aloud, cut at a sentence boundary near `MaxSpokenChars` so lines fit the 30 s tape — screen text is never cut). Config: `TTSEndpoint` (empty = off) / `TTSModel` / `TTSApiKey` / `TTSTimeoutSeconds`, `Voices` map (**sonia=af_jessica, olivia=af_aoede, alexa=af_river, rebecca=af_nicole** — the owner's casting), `VoiceSpeed`, `RadioOffset`. Commands: `rq.voice.status` / `rq.voice.test <trader> [text]` / `rq.voice.rebuild`.
- **Oxide.Ext.RustQuestsVoice.dll 0.1.0 (extension, built from `voice-ext/`)** — the unsandboxed worker: OpenAI `/v1/audio/speech` POST for wav, RIFF parse, −16 dBFS RMS normalize with tanh soft limiter (the fix for the quiet PoC playback), managed Ogg Vorbis encode (vendored MIT encoder). Installs like Oxide.Ext.Discord: one DLL into `RustDedicated_Data/Managed/`, loads at boot only.

### Deploy notes (already done on the live server)
1. Extension DLL into `Managed/` + server restart (done at the 2026-08-14 docker-resize restart).
2. `oxide/config/RustQuestsVoice.json` pre-written with the endpoint + casting, then `RustQuestsVoice.cs` and `RustQuests.cs` copied — both hot-compiled clean; log shows `Trader voice on: http://localhost:8880/v1/audio/speech model=kokoro`.
3. Kokoro-FastAPI GPU container (pureelectricity's CA template, same `ghcr.io/remsky/kokoro-fastapi-gpu` image) serving on :8880 — measured 0.46 s for a 6.4 s line; in-game render times 176–636 ms.
4. `RQVoicePoc` stays as a test rig until the owner confirms the real engine in-game, then delete `RQVoicePoc.cs` + `oxide/data/RustQuests/voice/poc.ogg`.

### Design
- Mechanism decision (cassette-ogg injection, voice-relay rejected) and PoC record: [docs/decisions/0005-v3-trader-voice.md](docs/decisions/0005-v3-trader-voice.md). Model parity rule: local models must also exist on OpenRouter (kokoro-82m does) so hosted servers reproduce identical voices — `TTSEndpoint`+`TTSApiKey` swap is the whole migration.
- Deviations from PLAN C: voices live in RustQuestsVoice's config (not `traders.json` — core must not know about voice); pre-warm is organic (first play caches; no boot-time render pass); sentence-split became sentence-truncate.

## [2.15.2] — 2026-08-12 — the reset that forgot the shops

### Fixed
- **`rq.wipe.reset` now re-places the shops and traders.** It despawned everything and rolled the new story, but only a REAL wipe's boot path ever scheduled `AutoPlaceMissingShops` — after a dev reset the cabooses and traders stayed gone until the next plugin reload (found live by the owner, 2026-08-12). The reset now places them immediately (the world is already up — no 60s delay needed), honoring `AutoPlaceOnWipe`, and the reply says which happened.

## [2.15.1] — 2026-08-12 — the config that ate its own keys

### Fixed
- **All config key names are now pure ASCII** (six carried em-dashes: ShopBiomeStrict, ShopNoBuildRadiusMetres, SLMEndpoint, SLMModel, SLMApiKey, SLMTimeoutSeconds). A hand edit in an ANSI editor mangled the em-dash inside those KEY names; Newtonsoft then silently bound the C# defaults — no warning, plugin loads clean — and `LoadConfig`'s re-serialize **overwrote the user's values with defaults on the very next reload** (found live: the owner's freshly added OpenRouter endpoint/model/key were wiped this way). With ASCII-only keys, the file `SaveConfig` writes survives any editor's encoding.
- Migration is automatic-ish: old em-dash keys stop matching once, but the affected values were already destroyed by the bug itself; the live file was rewritten with the new keys and restored values by hand. Fresh installs are unaffected.

## [2.15.0] — 2026-08-12 — bring your own endpoint

### Added
- **`SLMApiKey` config** — a Bearer token sent as `Authorization` on every SLM call when non-empty. Lets the trader chat run against hosted OpenAI-compatible endpoints (OpenRouter etc.) instead of a LAN Ollama box — e.g. `SLMEndpoint: "https://openrouter.ai/api"`, `SLMModel: "openrouter/free"` (or a pinned `:free` model for a steadier voice). Empty key = no header = exactly the old behavior; the the live server Ollama setup is untouched.
- Free-tier reality check for anyone using OpenRouter's `:free` models: 20 requests/min and **50 requests/DAY** (1,000/day once the account has ever bought $10 of credits) — set `SLMHourlyBudget` low (5–10) or a single chatty session eats the whole day. Quota exhaustion 429s trip the existing 3-strike breaker and traders fall back to scripted dialog; nothing breaks.

## [2.14.1] — 2026-08-11 — who keeps turning the lights off

### Fixed
- **The caboose lights, actually this time.** the owner's report — dark on approach, and lights he flipped on went out again after he left — plus a live entity dump of all four cabooses cracked it: the caboose's interior lighting is a genuine **two-way switch circuit** (two `caboose_lightswitch` feeding a `caboose_xorswitch` whose output drives the lamps — lit means ODD parity, exactly one switch on, like a stairwell). The light healer's rule was "every switch found off gets flipped ON", which made both XOR inputs hot and the output cold — **the healer itself was switching the lights off**, within a minute of anyone lighting them, on every caboose, every 60 seconds, forever. The dump showed the frozen crime scene at all four shops: both switches `on:True`, XOR `on:False`.
- The heal now treats lit as a property of the switch *pair*: when parity is even (dark) it toggles exactly one switch; when parity is odd it keeps its hands off entirely — so a player flipping switches can never fight the healer, and any dark state heals within a minute. Plain-switch (non-XOR) shops keep the old flip-on rule.
- The earlier "electrical sim clears unattended switch flags every ~5 minutes" theory (v1.7-era comment) is superseded — the evidence fits the healer's own parity bug all along, same family as the self-inflicted generator-XOR blink documented on 2026-07-31.

## [2.14.0] — 2026-08-11 — we do not have contractors

**Phase I.6: the collections crew — the one verdict that sends people instead of paper. Phase I is COMPLETE: every consequence in the design now exists.**

### Added
- **The night visit** — `VisitDef` on choice options: real days after the player's own pick (press expose: day 2, exactly as the day-1 warning promised), the first NIGHT the picker is online, awake and outside a safe zone, three unsigned guns in company hazmats (`hazmatsuit_scientist`) walk out of the dark. Telegraphed in chat on arrival, survivable by doctrine (damage scaled to 0.5, ragged fire cadence, bounded 8-minute window, they NEVER path into building privilege — the stakeout at the walls is the performance), and lootable: each carries their pay in scrap, and killed crew leave ordinary scientist corpses. Then they walk off into the dark and are gone. One visit per wipe per picker, ledgered in `PlayerProgress.VisitsDone` (claimed at spawn — crash-safe once-each).
- **The NPC recipe** — FakeFriends' live-proven custom-brain scientist, ported and stripped for the job: component swap on `scientistnpc_roam` (off-monument vanilla scientists are inert — their CLAUDE.md §Key findings), terrain-typed NavMesh agent, movement pump with smooth ground snap, manual 4 Hz think. Visit-specific brain: assigned target (never sensed — the crew only ever wants the picker), approach/combat/watch/leave states, retaliation against third parties who shoot first, `IsDormant` pinned false so the walk-off finishes unwatched.
- **The denial** — `DelayedPieceDef.AfterVisit`: a piece anchored to the visit's resolution instead of the pick, so "the next sheets deny it" can never arrive before the thing they deny. Days later, signed, by the same kind of stone: "WE HAVE REVIEWED OUR FILES. NO SUCH ORDER EXISTS. NO SUCH CREW EXISTS. WE DO NOT HAVE MEMBERS. WE CERTAINLY DO NOT HAVE CONTRACTORS. ... WE ARE GLAD YOU ARE WELL, READER. WE HAVE NO OPINION ON WHY."
- **Dev levers** — `rq.crew.visit [name]` (force the call now, skipping day/night/safe-zone gates; a target who never picked gets a DRILL visit that writes no ledger) and `rq.crew.end [now]` (wind down / despawn). `rq.piece.status` now shows the visit line and per-piece anchors.
- The crew's damage scale rides the existing `OnEntityTakeDamage` protection hook (armed only while a visit is live); crew end on target disconnect, bible reroll, dev wipe, and `Unload`.

### Deploy notes
1. Deploy the .cs — the denial note merges add-only into notes.json.
2. `rq.template.sync the_press` (the expose option gains its Visit + denial piece; the other four templates are unchanged).
3. Nothing changes on the live four_keys wipe. First live crew walks on the next Press wipe where somebody names her — drill steps in the wipe checklist.

## [2.13.0] — 2026-08-11 — the island writes back

**Phase I.5: the delayed pieces. A verdict's other consequence arrives days later, by paper — while the grudges close doors, the mail opens them. Same contract as the grudges: derived from your own pick, never stored beyond a delivered ledger, always post-vault.**

### Added
- **Delayed pieces** — `DelayedPieceDef` on choice options: a note (stocked from notes.json, tokens expanded), supplies, or both, due a rolled number of REAL days after the player's own pick and handed over the next time they're awake past due, telegraphed by one narration line in chat. The due day is derived from (bible seed, choice, piece, player) — nothing scheduled is ever stored; `PlayerProgress.DeliveredPieces` is the once-each ledger and `ChoicePickedUtc` the anchor (a pick recorded before this shipped gets stamped at first check, so nothing arrives instantly). Delivery is checked on wake, on connect, and by a 10-minute clock that runs only while an online player still has mail coming.
- **The six pieces** (house rule: aggression is unsigned and deniable; kindness signs):
  - **Press expose → the warning** (day 1, exactly): "NO SIGNATURE ON THIS SHEET. YOU WILL WORK OUT WHY. ... SLEEP LIGHTLY, READER." The evening-before courtesy — the collections crew (I.6) calls on the night of day 2.
  - **Press protect → care drop** (day 1–3): bandages, syringes, fuel. "BACK PAY FINDS PEOPLE LIKE YOU. THIS IS WHAT THAT MEANS."
  - **Manifest give → care drop** (day 1–3): "YOUR NAME APPEARS IN OURS. UNDER ASSETS."
  - **Count give → care drop** (day 1–3): "THE COUNT KEEPS WHAT IT IS GIVEN. THAT INCLUDES WHO GAVE."
  - **Count burn → the debt payment** (day 2–4): 217 scrap, bank-neat, itemized. "WE DID NOT ASK FOR THAT. ASKING LEAVES RECORDS. ... WE PAY OUR DEBTS. DO NOT REFUSE." Burning the founders' board bought them the last word — they pay for it whether you like it or not.
  - **Yours, C. read-alone → C.'s second envelope** (day 2–4): typed years ago "for whichever reader opened the last envelope and told no one" — pre-written, so it stays TRUE under all three `c_author` rolls (the H.2 rule); only the pencil on the envelope is fresh, and the keeper's hand exists on every roll.
- **Dev levers** — `rq.piece.status [name]` (what's scheduled, due days, delivered state) and `rq.piece.deliver [name]` (force everything pending — the drill can't wait a real day). Name argument for server-console use.
- Validation: piece ids/day-ranges/empty-delivery at template load; NoteTag existence checked after notes merge (same late check as reward NoteTags).

### Deploy notes
1. Deploy the .cs — the six notes merge add-only into notes.json.
2. `rq.template.sync` ×5 (`the_press`, `the_manifest`, `yours_c`, `the_recall`, `the_count`) — the options gain their Delayed pieces. (the_recall carries none yet; synced for uniformity with the shipped set.)
3. Nothing changes on the live four_keys wipe (no choice → no picks → the piece clock never arms).

Remaining in Phase I: I.6, the collections crew — press expose only, one unsigned, telegraphed, survivable night visit on the night of day 2 (the warning's day-1 pin is the telegraph), which the next sheets deny ever happened.

## [2.12.0] — 2026-08-11 — your money's no good here

**Phase I (first slice): the verdicts grow teeth. Consequences land on whoever picked — derived from your own `Choices` record, never stored, always post-vault: they cost relationship, never earned content.**

### Added
- **Grudges** — each choice option can name who holds your pick against you (`@role:`/`@secret:`/literal/`*`) and how hard. Four severities: **reserved** (one shadowed line in her chat prompt), **frozen** (she serves you with exact courtesy and nothing else — and never explains why), **grieving** (one line, forever: "I've said everything I'll ever say to you"), **barred** (the eviction: counter closed, chat gone, and *her machines refuse your scrap*).
- **The gambling lockout** — barred means barred from the whole house: slots, blackjack, and poker at HER caboose all refuse you (loot-open and seat-mount paths, verified against the live assembly; hooks armed only while a barred player is online). "{Trader} waves you off without looking up."
- **Eviction voices** — `BarredText`/`FrozenText` shipped for all four traders in-register (Rebecca: "Kettle's off... some things you can't compost back into good soil." Olivia: "Your money is no longer good here. Both statements have been tested exactly once."), blank-filled into live traders.json on load like the warmth tiers.
- **The grudge map:** name the press → **she bars you**; post the manifest → **the seated bars you**; read C. aloud → **living C. grieves** (a prose-rolled C. wounds nobody — the roll decides the cost); collapse or decommission the magazine → **the digger freezes** (slamming her door would confess — her forced civility is the season's last unnamed clue); arm The Unpaid → **Olivia freezes** (the armorer's respect is the price); burn the founders' board → **all four go reserved**.

### Deploy notes
1. Deploy the .cs — the new profile fields blank-fill into live traders.json at load (no trader sync needed).
2. `rq.template.sync` for the five choice templates (`the_press`, `the_manifest`, `yours_c`, `the_recall`, `the_count`) — the options gain their Grudges.
3. Nothing changes on the live four_keys wipe (no choice, no picks, no grudges — hooks stay unsubscribed).

Next slices (PLAN I.5–I.6): the delayed pieces (warning note, care drops, C.'s private letter, the debt payment) and the collections crew — the one unsigned night visit, press-expose only.

## [2.11.1] — 2026-08-11 — the press knows what it's holding

### Added
- **`rq.pamphlet.status`** — lists every print in the press with its rotation verdict (IN ROTATION / gated out with the inactive arc named) and any per-file GrantsQuest. Beat-gated prints made rotation state worth seeing without spawning crates.

### Verified
- **Beat-print rotation drill PASSED (live, state-restored):** the count print reads *gated out* on the running four_keys wipe (9 of 10 in rotation), **stays gated** after a Count reroll while the beat is unfired, flips to **IN ROTATION (10 of 10)** the moment `count_sheet` fires, and returns to gated with the state restore. The per-file arc gate in `PamphletInRotation` — never before exercised with a real entry — behaves exactly as designed at all four stages.

## [2.11.0] — 2026-08-11 — recovered originals

**v2 Phase H.5: the missing FIELD NOTES as loot, and the first beat-gated print.**

### Added
- **`RewardDef.NoteTag`** — a quest reward of item `note` can name a notes.json entry: the granted note arrives stocked (title + text, tokens expanded), the reward chat line announces the TITLE ("FIELD NOTES №2 (RECOVERED)", not "Note"), and load-time validation catches missing tags or non-note items. Lore as loot, reusable by any future content.
- **The recovered archives** — the low gaps in the numbered run, published at last as recovered originals (resistance.md's hook; the numbering canon now has three registers — circulation prints №3/№5, per-wipe ledger numbers №6+, and these): **№1** (the founding sheet, "TYPED THE NIGHT THE BOATS LEFT... NOBODY IS COMING. GOOD.") granted at BACK PAY, THREE OF THREE; **№2** (the yard, first observations of the tank — annotated in pen, "SOMEBODY FINALLY FILED THE RECALL", the day you did) at Recall Notice; **№4** (what falls from the sky) at Right of Way.
- **The tenth print** — `the-count-published.jpg` ("ASK THE BOX WHAT YEAR IT IS"), rendered through the assets pipeline in the house plate style (1708×960, 115 KB). Wired to The Count's `count_sheet` beat via `Pamphlets` — the **first real use of the F.2 beat→print mechanism**: the print joins the crate rotation only after the sheet publishes, arc-gated through `pamphlets_meta`.

### Deploy notes
1. Deploy the .cs AND copy `the-count-published.jpg` to `oxide/data/RustQuests/pamphlets/`.
2. `rq.template.sync the_count` (the beat gains its print), then scoped `rq.quest.syncrewards unpaid_drop_3` / `olivia_scientists` / `alexa_bradley` — verified lossless against the live file (rewards untuned).
3. The print stays OUT of rotation until a Count wipe's beat fires: its `pamphlets_meta` entry (Arc `count.sheet`) now SHIPS with the defaults — a file with no entry would circulate immediately, so beat prints must always ship their gate. `FireBeat`'s additive write no-ops against it.

## [2.10.0] — 2026-08-11 — write it somewhere. you will not remember that you knew.

**v2 Phase H.4: template 6 — "The Count".**

### Added
- **"The Count"** — THEY COUNT THE WIPES becomes the plot. Everyone half-knows the island restarts; The Unpaid's tally goes public, and the wipe asks what counting is FOR. This is the template that **reads the island's memory aloud**: its canon tells every trader "the sheets say this is wipe {wipes}; the previous season is remembered as '{lastwipe.template}', and it ended with {lastwipe.choice}" — and the beat sheet (THE COUNT, PUBLISHED) says it to the player's face: *"write it somewhere. You will not remember that you knew."* All tokens first-wipe-safe; the template gets richer every real wipe.
- **Rebecca holds the fixed role** (the timekeeper — she ran the light cycles and never stopped keeping the island's calendar), so the rolled cast is Sonia, Alexa, and Olivia — all three roled on every Count wipe: the **tallier** (cover: "counting is morbid superstition," said while knowing today's number — notched doorframes, an hour-meter wired to nothing, punch marks spaced like a gauge), the **eraser** (scrubs tallies openly — "grief needs endings, not arithmetic"), and the **forgetter** (seasons blur; the trader closest to the player's own experience of resets).
- **Secrets:** `tallier_identity` (tier 2) and `true_count` — how the real count relates to the published one (matches exactly / three wipes higher / past counting, the earliest marks washed out). The verdict: the war chest held the **founders' board** — the four's private tally from before the public count. **Post them side by side** or **give the early marks to the box** and the truth is stated; **burn the board** and the island keeps exactly one number, *uncheckable forever* — the only verdict in the pool that destroys information rather than revealing or withholding it.
- Beat `count_sheet` (day 2–5, gated on Good Soil — the timekeeper's chain opening); `tally_wall` binding with a many-hands evidence note where the eraser's wire-brushed rows are visible before she's ever met. Ten overlays, five notes. Pool: **six templates**, weight 1, prefer-not-last.

### Deploy notes
Deploy the .cs only — add-only merge. Drill: `rq.bible.reroll the_count` on a test boot, or the state-backup/restore method proven on 2026-08-11 (PLAN H) for an adopted live wipe.

## [2.9.0] — 2026-08-11 — pick up your brass

**v2 Phase H.3: template 5 — "The Recall". The pool reaches the design floor of five.**

### Added
- **"The Recall"** — spent casings with Cobalt QA headstamps keep turning up, fired years after the company left: somebody found and opened one of the old buried magazines. **Olivia holds the fixed role** for the first time (the examiner — they're her sign-offs coming home; she keeps a jar of returned brass on the counter), which frees **Sonia to join the rolled cast** for the first time: digger (cover: "honest salvage, barrel to bullet, same words every time"), buyer (a crate cheaper than honest, heavier than salvage), and spotter (a pin map with a center of gravity) roll among Sonia, Rebecca, and Alexa.
- **A mystery with no scripted reveal** — `digger_identity` slips at warmth 2 (snare lines past a fenced hillside, shoring timber that never held preserves, a winch re-greased weekly) but NO ending, note, or canon line ever names her. The wipe is allowed to keep a secret; deduction is social or nothing. This also keeps the vault prompt value-safe when the roll makes Sonia — who fronts it — the digger.
- **The verdict is about the hole** — the war chest held the magazine's site chart: **collapse the mouth** (one soft thud at dawn), **give it to Olivia** (the recall answered properly, batch by batch — pays partly in product: 200 rifle rounds), or **leave it in the box** (`recall.armed`: "the next company boat that tests this shore will find the ground staff armed, paid, and current on doctrine"). `magazine_state` (prose secret: half full / last crates / flooding) is stated by each ending.
- Beat `casings_map` (day 2–4, gated on Cordwood) publishes **FIELD NOTES №{fieldnote}: THE RECALL** — real gameplay hints (read your headstamps; cheap ammo has a source; give the examiner her brass back). Second consumer of the cross-wipe numbering ledger.
- Ten overlays (including Sonia's first rolled-role set, vault line riding along), five notes. Pool: **five templates**, weight 1, prefer-not-last.

### Deploy notes
Deploy the .cs only — add-only merge as with templates 3 and 4. Drill: `rq.bible.reroll the_recall` on a test boot (checklist).

## [2.8.0] — 2026-08-10 — you know who. or you will.

**v2 Phase H.2: template 4 — "Yours, C.".**

### Added
- **"Yours, C."** — the typed welcome note every player carries becomes the wipe's spine. `c_author` rolls **three ways**: the keeper (one of the shuffled three, cover: she "found" a bundle of C.'s letters), **Sonia herself** (the note that vouches for her, in her own hand), or *"someone who stopped writing years ago"* — a prose secret value, rendered verbatim by `{secret:c_author}` so a non-trader answer reads as a sentence. Roles: keeper / denier ("gone, I checked") / collector (compares ribbon wear across newcomers' notes) + Sonia the **named**, volunteered by paper she never signed. Every slip is written to stay true under all three rolls — they corroborate the letters, the tin, the locked drawer, the unbroken ink sequence, never the signature. Second secret `c_reason` (why C. writes: a debt, a promise, or apology practice — all prose, tier 2).
- **The quiet verdict** — C.'s last letter, sealed, in the war chest. **Read it aloud** (the island learns; the canon line names), **read it alone** (the *player* learns — the ending names via a narration aside while Sonia explicitly doesn't ask — and the island's SLM canon knows only that somebody knows), or **burn it sealed** (the question keeps its dignity). Typed Unpaid reactions for each: A NOTE ON SIGNATURES / DISCRETION / ASHES.
- Beat `c_letter` (day 3–6, gated on the knife job — C. notices when a newcomer takes the note's advice) publishes *an unsent letter* at the `c_postbox` binding; *a letter, returned* waits there from wipe start. C.'s voice is the welcome note's: typed, plain, lowercase-warm — the deliberate opposite of THE UNPAID's caps.
- Ten overlays, five notes, the E slots/pool riding along. Pool is now **four templates** at weight 1, prefer-not-last.

### Deploy notes
Deploy the .cs only — new ids merge add-only (template, overlays, notes). Drill: `rq.bible.reroll yours_c` on a test boot; roll it a few seeds if possible, since the three-way `c_author` is the thing to see land each way (checklist section).

## [2.7.0] — 2026-08-10 — forty seats, two hundred people

**v2 Phase H.1: template 3 — "The Manifest".**

### Added
- **"The Manifest"** — the evacuation manifest surfaces in pieces. Shuffled chain behind Sonia; roles: one of the three **had a seat and stayed** (cover: "ground trades never rated seats"), one was **scratched** off at the dock, one **typed the list** ("it was alphabetical" — too quickly), Sonia the **witness** who counted the gangway from the treeline. Secrets: `seated_identity` (warmth-2 slips; the seated's own tells strongest, the scratched and the clerk each hold a corroborating angle) and `manifest_leak` (whose carbon reached The Unpaid — the clerk's, or someone gone). The Phase E slots/pool ride along; a **time-only beat** publishes MANIFEST, PAGE SIX by the drop box on day 2–4; a water-stained page waits near the `manifest_dock` binding from wipe start.
- **The first three-option verdict** — the war chest held the only complete copy. At the vault claim, Sonia asks: **post it** (every name public — the reaction note does the arithmetic, including `{secret:seated_identity}`'s row), **burn it** (guesses heal over, lists don't), or **leave it in the box** ("if a reckoning comes, it will come with page numbers"). Per-option endings, canon lines, bonus rewards, and typed Unpaid reactions.
- **`{secret:<id>}` token** — the generalized `{press}`: any rolled secret's value, rendered as a trader name when it is one. Post-reveal prose only, same rule as `{press}`.
- Ten new overlays (`*.seated` / `*.scratched` / `*.clerk` + `sonia.witness`), five notes — all merged add-only into the live data files.

### Deploy notes
Deploy the .cs and that's all: a NEW template id auto-merges into templates.json (add-only), as do the overlays and notes. No sync commands. The pool is now three templates at weight 1 with prefer-not-last — the next wipe rolls The Press or The Manifest at even odds. Drill: `rq.bible.reroll the_manifest` on a test boot (checklist section).

## [2.6.0] — 2026-08-10 — somebody's call

**v2 Phase G: the late-wipe choice.**

### Added
- **The choice engine** (decision 0004.5) — a template `Choice` puts one fork at a designated quest's claim: the Ready dialog becomes an option picker (same panel skin, one button per option, no default, no way past without deciding); the pick is acknowledged in the trader's voice (`ResultText`), then the normal claim flow resumes carrying the picked ending. The ending quest stays **one `QuestDef`**: the pick overrides its Offer/Complete text at display time (counter AND journal story tab) and appends `BonusRewards` at claim. Per-player picks in progress (`Choices`); island-level state in the bible (`FirstPick` = whoever decided first, label captured at pick time, per-option vote counts — shown by `rq.bible`).
- **World flavor off the first pick** — the winning option can name an `Arc` (activated additively via the new shared `ActivateStoryArc`, so notes/stashes/pamphlets hang off the verdict) and a `CanonLine` appended to every trader's SLM prompt. `LastChoiceOutcome` carries the pick's label into next wipe's `{lastwipe.choice}`.
- **`{choice}` token** + optional `prog` param on `ExpandTokens` — the player's own pick where known, else the island's first, else "nothing, yet".
- **The Press: `press_verdict`** — at the vault claim, Sonia (always the skeptic) asks the only question left: **Name her** (the island learns `{press}` — post-reveal prose only — 300 scrap, arc `press.exposed`, a typed NOTICE OF RELOCATION by the drop box) or **Keep it buried** (no name said, 100 scrap + 2 large medkits, arc `press.protected`, A RECEIPT FOR SILENCE). Either way The Unpaid noticed. `four_keys` stays choiceless — the compat floor holds.
- Validation: option ids/labels/counts at template load; prompt/ending quest existence + no-Unpaid-prompt (auto-claims never render a dialog) at every bible apply.

### Deploy notes
1. Deploy the .cs — log shows "Added 2 note(s) to notes.json". Nothing changes on the running four_keys wipe (no choice there, by design).
2. `rq.template.sync the_press` — the live template gains the choice **before the next wipe rolls it** (shipped-set replace; live Press edits are discarded, that's the contract).
3. Drill (test boot): `rq.bible.reroll the_press` → run the vault claim — the fork renders instead of the claim button, picking shows her acknowledgment, the claim pays the bonus, `rq.bible` shows first pick + votes, the reaction note lands by the drop box, and `rq.slm.prompt <any trader>` carries the canon line.

## [2.5.0] — 2026-08-10 — the island counts its resets

**v2 Phase F: beats + wipe memory.**

### Added
- **Beat scheduler** — a template `Beats` entry schedules an arc to go live N real days into the wipe (rolled per-beat from `RealDayMin`–`Max`, per-beat child RNG streams so mid-wipe adoption = fresh-roll determinism), optionally gated on a quest ANY player has claimed (new server-wide `WipeCompleted` ledger in state; fed at claim time, self-healing for pre-ledger progress as records load). The 5-minute clock runs only while a beat is pending; boot runs a catch-up pass so past-due beats fire immediately in due order.
- **Additive-only firing** — `FireBeat` appends the arc, rebuilds the index, pins any new pooled turn-ins, writes additive pamphlet-manifest entries (the beat's prints join the loot rotation through the normal arc gate; operator entries never overwritten), runs the same idempotent `Ensure*` placement pass as boot (failed spots retry next boot), marks fired, saves. Nothing ever deactivates; no def or progress record is touched. Dev lever: **`rq.beat.fire <id>`**.
- **Wipe memory tokens** — `{wipes}` (the running wipe counter), `{lastwipe.template}`, `{lastwipe.choice}` (neutral fallbacks on a first wipe), and `{fieldnote}` — this wipe's claimed **FIELD NOTES number**, drawn from the cross-wipe `UsedFieldNoteNumbers` ledger (skips the canon №3/№5; every wipe claims one whether or not it prints it — gaps imply the larger run, per resistance.md). Note **titles** expand tokens now too.
- **Ride-along beat in `four_keys`** — `field_notes_reprint`: 3–5 real days after somebody becomes A READER (`unpaid_contact` claimed), the watchers publish **FIELD NOTES №{fieldnote}** near the same drop box (self-placing note, arc `four_keys.unpaid.reprint`). Pure lore — no quest, no pamphlet JPGs needed.
- `rq.bible` shows the current real day, the claimed field-notes number + used ledger, and (as before) each beat's due day and fired flag.
- Mid-wipe adoption, same contract as Phase E: a bible rolled before its template gained beats adopts them (`AdoptNewBeats`, same due day a fresh roll would give); a pre-F bible claims its field-notes number on the next apply.

### Deploy notes (mid-wipe safe)
1. Deploy the .cs — log shows "Added 1 note(s) to notes.json" and "FIELD NOTES №6 claimed for the running wipe".
2. `rq.template.sync four_keys` (the live template gains the beat), then reload — log shows "Beat 'field_notes_reprint' adopted mid-wipe: due day N".
3. `rq.bible` — beat line present, unfired. It fires by itself N real days into the wipe once any player who has claimed A READER has connected (the ledger folds their record in). `rq.beat.fire field_notes_reprint` exists for a test boot — don't run it live unless you want the sheet early.

## [2.4.0] — 2026-08-10 — even the classic stops repeating

**v2 Phase E: variant slots + turn-in pools, retrofitted into `four_keys`.**

### Added
- **Slot variants** — a template `Slots` entry rolls one variant per slot into the wipe. Variants share the classic quest's **Id** (so every `RequiresQuest` link and the chain wiring survive any pick, by construction) and differ by `Arc`. The reserved first entry **`"classic"`** means "play the shipped quest": it picks no arc, so a pickless bible, an un-synced live template, or a slotless template all degrade to the classic story — never a broken chain. A picked variant **shadows** the same-Id classic at read time (`ShadowedThisWipe`, computed in `BuildQuestIndex`); no quest def is ever re-tagged or written.
- **Turn-in pools** — `ObjectiveDef.Pool` names a template pool; the bible rolls item + count per objective (`TurninRolls`, deterministic child stream per quest+index) and the roll wins over authored `Target`/`Count` at every read (`EffectiveTarget`/`EffectiveCount` — defs never mutated, so the sync commands can't leak a roll into quests.json). New `{target}`/`{count}` tokens keep offer/objective prose honest. Load-time validation: pools are turnin-only, must exist in some template, live-edited `Target`/`Count` on a pooled objective warns (tune the pool instead), multiple pooled objectives per quest warns (tokens bind to the first).
- **`four_keys` retrofit** — slot `harvest`: Rebecca's *Catch and Keep* or new *What Keeps* (a potato year); slot `garage`: Alexa's *Strip It Down* or new *Parts Is Parts* (roadside salvage); pool `furnace_feed`: Olivia's *Cordwood* now asks 2500–3500 wood **or** 1200–1800 charcoal per wipe. Variants keep their classic's rewards economy exactly.
- **Mid-wipe safety** — a bible rolled before its template gained slots adopts the FIRST-listed variant (the classic, by authoring convention) instead of rolling (`AdoptNewSlots`); a pooled objective arriving mid-wipe gets its AUTHORED values pinned into the bible (RNG rolls happen only in the tick after a fresh `RollBible`), so no later reload can re-roll an ask a player already started. Deploying + syncing mid-wipe changes nothing players see.
- **Chain-link validation** — `ValidateActiveChain` after every bible apply: every `RequiresQuest`/`RequiresQuests` of an active quest must itself be active, and every picked arc must activate at least one quest. Warns loudly in the Oxide log if authoring ever breaks the order.
- **`rq.bible`** now prints slot picks and turn-in rolls.

### Fixed
- **Sonia capped at warmth tier 1 until the vault opened** — `grand_finale` (Giver=sonia) counted toward *her* chain, so "her chain complete" was unreachable pre-vault. Cross-trader finales (any quest with `RequiresQuests`) now count toward tier 3 only; her chain done = tier 2, same as the other three (decided 2026-08-10).
- `MigrateStampArcs` is first-wins per `Id#Tree` — a pre-v2 file migrating today lands on the classic's arc, never a variant's.

### Deploy notes (mid-wipe safe, but sequenced)
1. Deploy the .cs (hot-load). Watch for "Added N new quest definition(s)" (the two variants merge in, inactive).
2. `rq.template.sync four_keys` — the live template gains Slots/Pools (live four_keys edits are discarded, that's the contract).
3. `rq.quest.syncrewards olivia_wood` then `rq.quest.synctext olivia_wood` — pushes the pooled objective + tokened text. It still reads "3000 Wood" (pin, not roll) until a real wipe.
4. Reload (or wait for the next boot): the log shows the `classic` slot adoptions + the Cordwood pin; `rq.bible` shows `slots: harvest=classic, garage=classic` and `turn-in olivia_wood:0: 3000 x wood`; no chain warnings.

## [2.3.6] — 2026-08-10 — the press keeps leaving copies

**Stage 1 of the v2 live playtest PASSED (the owner, 2026-08-10): migration, four_keys adoption, warmth chat, pamphlet recruitment, field sheet, and all three BACK PAY drops end to end.**

### Added
- **Note boxes restock on pickup** — a taken sheet is replaced a tick later (`RestockNoteBox`, shared `StockNoteItem` with the spawner). Closes the multi-player gap from the Stage-1 review: one player pocketing the letter starved everyone else's "find the field sheet" objective until the next restart. Applies to every placed note, including whatever future templates place. A reader putting their copy back simply leaves two — the box only restocks when empty.

### Notes (multi-player review, by design)
- Drop boxes are shared and unlocked: partial deposits are stealable, and one player's abandoned deposit can subsidize another's credit — Rust being Rust, and thoroughly in The Unpaid's voice. Objectives stay per-player; the box is never consumed.
- Sheets are transferable recruitment papers: handing the letter to a friend recruits them too (a hoarded letter goes inert after a restart — the tag mapping rebuilds for the fresh copy).

## [2.3.5] — 2026-08-10 — nobody stays unpaid

### Fixed
- **BACK PAY 1 stranded complete-but-unclaimed after the RPC kick** — the deposit had credited and consumed, but the auto-claim's tick found the player disconnected and there was no retry; re-depositing hit "objective already 5/5" and did nothing. Missed unpaid auto-claims now retry via `SweepUnpaidAutoClaims` on every wake-up AND at the start of every drop settlement (poking the box unsticks it). `OnPlayerSleepEnded` stays subscribed regardless of the WelcomeNote config since it carries the sweep now.
- **Field sheet unreadable in the vanilla note panel** — long paragraphs clip. The sheet is rewritten in short hard-broken lines (it reads MORE like a typewritten work order now, so the constraint improved it). New **`rq.note.synctext [tag]`** — fifth of the sync family: pulls shipped Title/Text over notes.json and respawns placed boxes so the baked item text refreshes without a boot.

## [2.3.4] — 2026-08-10 — never mutate mid-RPC

### Fixed
- **Depositing bandages in the drop box kicked the player ("RPC error in MoveItem").** `OnItemAddedToContainer` fires inside the MoveItem RPC, and the deposit handler consumed the stack (`container.Take`) while the engine was still placing it — the RPC handler threw and dropped the client. All mutating paths in the item hook now defer one tick: drop settlement (`SettleDropDeposit`), note grants + readnote credit, pamphlet recruit grants. The plant path stays synchronous (progress-only, no inventory mutation). Rule recorded: **nothing downstream of OnItemAddedToContainer may touch an inventory in the same tick.**

## [2.3.3] — 2026-08-10 — the sheet stops lying about the box

**Findability pass from Stage 1 ("what am I supposed to do at H20?").**

### Fixed
- **Field sheet and drop box could land 200+ m apart** while the sheet claims "THERE IS A WOODEN BOX NEAR HERE" (130 m apart on the live roll). A note whose monument binding key is also a drop tag now anchors 10–30 m from the *box* (`TryFindSpotNearBox`), falling back to the monument ring.
- **Monument ring searches close-in first** — 15–45 m for 120 attempts before widening to 110 m; a 70 m-out woodbox was a needle in the grass.
- **A READER now quotes a grid** — objective line and offer text carry `{monumentgrid:unpaid_drop}` ("near Mining Outpost (around H19)"). Requires `rq.quest.synctext unpaid_contact` on the live server to reach the existing quests.json.

### Notes
- Already-placed furniture keeps its position (state-persisted); the anchoring and phased ring apply from the next wipe or `rq.bible.reroll`.

## [2.3.2] — 2026-08-10 — the bots don't get a welcome note

### Fixed
- **Boot-time NullReferenceException ×3 in `TryGiveWelcomeNote`** (v1.11-era, spotted in the live log during Stage 1): on a cold boot, sleepers wake via `OnPlayerSleepEnded` *before* `OnServerInitialized` has loaded state — and the sleepers in question were FakeFriends bots, which are `BasePlayer`s that pass the `IsNpc` check. Now guarded with the same `userID.IsSteamId()` exclusion the kill path uses, plus a null-state guard (a real player waking in that window gets the note on their next wake).

## [2.3.1] — 2026-08-10 — Stage-1 playtest fixes

**First live findings from the v2 deploy (mid-wipe adoption itself went clean: migration stamped 30, merged exactly the 4 Unpaid quests, four_keys no-roll bible, Unpaid furniture placed).**

### Fixed
- **`rq.pamphlet.give` never granted A READER** — the `_tuckingPamphlet` reentrancy guard (which keeps our own crate-seeding from crediting players) also swallowed the admin command's pickup path. Recruit logic extracted to `TryPamphletRecruit`, called explicitly by the command (reply now says "— and it recruited you"). Organic crate pickup was unaffected.
- **Chat panel cut off the newest reply** (found on a warmed-up Rebecca) — the transcript window kept a fixed 12 *stored* lines, but v1.11's tier-2+ replies run to 320 chars (~6 wrapped lines each), and an overflowing label truncates the NEWEST text. The window now budgets by estimated wrapped lines, admitting newest-first; the oldest clip off the top as designed.

## [2.3.0] — 2026-08-10 — the first wipe that isn't the last wipe (v2 Phase D)

**"The Press" — the first template that genuinely rolls a different story (PLAN Phase D). NOT YET DEPLOYED; the D.5 drill (wipe-checklist) must run on a test wipe before a live Press roll.**

### Added
- **Template `the_press` (Weight 1, alongside `four_keys`).** One of Rebecca/Alexa/Olivia secretly runs the pamphlet press. The chain **shuffles** behind Sonia (pinned first — the welcome note keeps pointing at her railcar, the vault stays at her counter, and the operator-`""` precedence question stays safely deferred). Roles cast every wipe: press, bitter, sympathizer among the shuffled three; Sonia is always the skeptic. Secrets: `press_identity` (slips at warmth 2) and `c_author` — is C. of the welcome note the press, or someone long gone? (slips at warmth 3).
- **Ten shipped overlays** — every (trader, role) The Press can roll: role-tinted persona and small talk, warmth-gated slips (the press's own ink-tells are the strongest signal, by design), and chain-safe closings. The press's own chat prompt is **never told she is the press** — cover stance + authored tells only, keep-by-omission applied to herself.
- **`{onward}` + `{nexthint}` tokens and `TraderProfile.LocatorHint`** — one authored closing line renders a mid-chain referral (next trader's name, locator hint, live shop grid, "tell her X vouched for you") or the back-to-the-head vault line, resolved from the effective chain at display time. This is what makes referral prose survive any shuffle without per-position authoring.
- **`{role}` (via `TemplateDef.RolePhrases`), `{faction}`, `{press}` tokens** — `{press}` resolves the rolled press's *name* and is documented as post-reveal-prose-only (finale/choice text, Phase G).
- **"an unsigned scrawl"** — a handwritten watcher note (arc `press.rumors`, own monument binding) that teaches the deduction game: watch hands, earn warmth, listen.
- Wipe checklist grew a v2 section + The Press drill (D.5's script).

### Fixed
- **Warmth tier 3 required BACK PAY** — Phase B's Unpaid quests had silently joined `WarmthTier`'s "every quest done" count, so "vault open" warmth suddenly demanded the side chain too. Unpaid quests are now excluded: tier 3 means the traders' tree and vault, as designed.

## [2.2.0] — 2026-08-10 — she only knows what the wipe told her (v2 Phase C)

**SLM canon goes data-driven and dialog learns roles (PLAN Phase C) — the gate before any wipe can roll roles. NOT YET DEPLOYED; ships with 2.0.0/2.1.0 at the wipe boundary. With `four_keys` rolling no roles, behavior is byte-identical to 2.1.0 — every seam is dormant until Phase D.**

### Added
- **Canon from the template.** The hardcoded "true facts about the island" block in `TraderChatPrompt` now comes from the active template's `CanonFacts` (token-expanded, so a future shuffle template states the *rolled* chain via the new `{chain:n}` token); the v1 sentence remains as a compiled-in fallback for a broken `templates.json`. Per-role `RoleCanon` (her public stance) and `UnknownFacts` (explicit don't-knows — the anti-invention fence) follow it into the prompt.
- **`overlays.json`** (merge-by-key, `rq.overlay.synctext`, reloaded by `rq.trader.reload`): dialog overlays keyed `"<trader>.<role>"`. Scripted fields (Greeting/Progress/Ready/ChainDone/ChainPending/LockedGreeting) **replace** the profile's when authored; `PersonaAdd`/`SmallTalkAdd` **append** to the SLM prompt. Applied at read time via `ActiveOverlay` — profiles are never mutated, so no `SaveTraders()` can leak rolled state into `traders.json` (same rule as chain rewiring). The journal's Story tab re-reads in the rolled voice too. Shipped set is empty; Phase D authors "The Press".
- **Keep-by-omission slips (decision 0004.7 delivered).** A secret's *value* never enters the chat prompt at any tier. At warmth ≥ the secret's `SlipTier`, the overlay's authored slip line joins — a breadcrumb of controlled precision for the deduction game ("something you might let slip, just once…"). The "never state stash codes" guard stays verbatim.
- **`rq.slm.prompt <trader> [tier]`** — dumps the composed system prompt without spending an SLM call: canon, role stance, unknowns, warmth line, slips, exactly as the model would see them. Optional tier override previews what higher warmth would reveal. This is the Phase C/D test surface alongside `rq.slm.test`.

## [2.1.0] — 2026-08-10 — the island starts paying forward (v2 Phase B)

**The Unpaid become a working quest-giver (PLAN Phase B). NOT YET DEPLOYED — ships together with 2.0.0 at the wipe boundary.**

### Added
- **A faceless giver.** `"unpaid"` is a valid quest `Giver` with no profile, NPC, shop, warmth or vouching. Their quests **auto-claim the moment the work is done** ("payment was under the box") and the next sheet in their chain hands itself out — they don't wait to be asked. The journal's Work tab grows a **THE UNPAID** section that appears only after first contact.
- **Paper grants work.** `NoteContent.GrantsQuest`: picking up a note auto-accepts its quest — grant lands *before* the readnote credit, so a sheet granting a quest whose objective is reading that very sheet completes in one pickup (the chain flows from finding the paper cold). `pamphlets_meta.json` (merge-by-key, `"*"` = any print): picking up any stamped Unpaid print grants the contact quest; per-file `Arc` entries gate prints out of the loot rotation until their arc is live.
- **`drop` objective (dead drops).** A quest box (no lock) placed per wipe near the bible's monument binding of the same key as the drop tag; depositing the required `Item` × `Count` credits and consumes. Partial deposits stay in the box and can be taken back.
- **`plant` objective (pamphlet distribution).** Deposit a stamped print into a container at N *distinct* monuments — nearest-bounds attribution, per-monument distinctness in a new `PlantLedger` on progress. Own boxes and our own quest boxes don't count; a reentrancy flag keeps our own loot-seeding from crediting a player mid-loot.
- **Monument-ring placer.** `TryFindSpotNearMonument`: 15–110 m ring outside the monument (the MONUMENT topology mask finds the seam on big pads), cheap stash-spot validation, anywhere-valid fallback. Notes with a `Monument` binding key now **self-place** when their arc is live — the LorePlacer v1 deferred, finally shipped.
- **Tokens** `{monument:<key>}` / `{monumentgrid:<key>}` resolve against the bible's bindings at display time.
- **Content: BACK PAY**, the first Unpaid side chain, always-on inside `four_keys` (arc `four_keys.unpaid`): a pamphlet or the field sheet starts A READER, then three dead drops — 5 bandages, 100 low grade, 500 metal fragments — paying 325 scrap + syringes total. `four_keys` gains the `unpaid_drop` monument binding (`any_monument` — the always-eligible guarantee holds).

### Fixed
- **Wipe-boot double-spawn:** `EnsureAllFinaleStashes` spawned a fresh stash and `SpawnWorldObjects` then spawned the whole list again — two overlapping boxes, one orphaned until restart. All spawners (notes/stashes/drops) now guard on an already-live box. Latent since v0.6.0.

### Notes
- The item-movement hook is armed whenever someone has an unfinished readnote/drop/plant objective **or** any paper grant is live in the world (a granting note placed, or a granting pamphlet in rotation) — a fresh player with no quests must still be able to pick up a recruiter.
- Documented limitation: a picked-up print can't be traced to its source JPG, so pamphlet grants use the first arc-active manifest entry. One recruitment campaign at a time.
- In-game verify (wipe drill additions): `rq.pamphlet.give` → pickup should grant A READER; `rq.bible` shows the `unpaid_drop` binding; the field sheet and drop box stand in the ring outside that monument; the three drops complete end to end with auto-claims.

## [2.0.0] — 2026-08-10 — the story learns to roll dice (v2 Phase A)

**Generator foundation (decision 0004, PLAN Phase A). Gameplay is deliberately identical to 1.11.0 — the v1 story now just arrives via the machinery that will randomize it. NOT YET DEPLOYED; wants a wipe-boundary deploy after the v1.11/v1.10 playtests.**

### Added
- **The wipe bible.** A per-wipe rolled record in state (`rq.bible` to read it): template, seed (stable hash of world seed + size + wipe counter, child RNG per subsystem so later-added rolls never shift existing ones), chain order, active arcs, roles, secrets (spoiler-guarded — `rq.bible secrets`), monument bindings, beat schedule. Wipes with everything else; a `CrossWipe` memory (last template/choice/press, used FIELD NOTES numbers) survives resets like `WipeCounter` — the outgoing bible is distilled into it on wipe.
- **`templates.json`** (merge-by-Id, never-overwrite, `rq.template.sync`): authored storyline templates — monument requirements (`veto`/`any_monument`/`skip_arc`), chain spec (fixed/shuffle), role slots, quest-variant slots, turn-in pools, beats, choice, SLM canon paragraphs. Ships with **`four_keys`**: the v1 story as the compat floor — zero monument requirements (always eligible), nothing to remix, so a `four_keys` wipe IS a v1 wipe.
- **`Arc` on quest defs** (and notes/stashes): null = every wipe, else only when the bible rolled it — the `Tree` filter generalized. All v1 quests ride `four_keys.core`; the vault rides `four_keys.vault`. `QuestActiveThisWipe` (tree AND arc) is now the single visibility predicate.
- **Effective chain wiring.** The bible's `ChainOrder` overrides `RequiresTrader`/`UnlockQuest` through `EffectiveRequiresTrader`/`EffectiveUnlockQuest` — deliberately never mutating profiles, so no `SaveTraders()` can leak a rolled chain into `traders.json`. Profiles gain `ChainFinaleQuest` (which quest of hers vouches the player onward, whatever position she rolls). Operator `RequiresTrader: ""` still wins. The journal, `rq.status` and `rq.unlock` all read effective wiring.
- **Generator in boot.** `GenerateWipeBible` rolls on the first boot of a wipe (guarded — a throw falls back to `four_keys`, no remix); restarts replay `ApplyBible` from stored state; a **mid-wipe v2 deploy** self-detects and adopts `four_keys` without touching in-flight progress. `rq.bible.reroll [template]` for drills; `rq.wipe.reset` now rolls a fresh bible too.
- **v1→v2 migration.** Merge key grew `(Id, Tree)` → `(Id, Tree, Arc)`, so first v2 load backs up the live `quests.json` (`quests.pre-v2-backup.json`), stamps `Arc` onto every entry the shipped defaults know (operator-authored quests keep `Arc: null` = active every wipe), and sets `DataVersion 2` — without the stamp every quest would duplicate on merge. Parse failure defers the migration, file untouched. `rq.migrate.check` dry-runs/reports it.

### Notes
- Roles/secrets/slots/beats/choice are rolled and recorded but have no consumers yet — those land in Phases B–G. The SLM canon block is still the hardcoded v1 sentence (Phase C moves it onto the template's `CanonFacts`).
- Local Roslyn compile against the live server assemblies: clean (warnings only, existing pattern).

## [1.11.0] — 2026-08-09 — she remembers who did the work

### Added
- **Per-character warmth progression in SLM chat.** Each trader now carries `WarmthTiers` (four authored tone lines) and `SmallTalk` (true, arc-derived interests) in `traders.json`, both blank-filled on load and re-synced by `rq.trader.synctext`. A new `WarmthTier` computes where the player stands in *her* story from the progress file: **0** stranger (no jobs done) → **1** warming (some done) → **2** trusted (her chain complete) → **3** family (every quest in the tree done, vault open). The tier's line plus her interests (tier 1+) go into the chat system prompt.
- **Warmth is in-register, not uniform.** Sonia goes from sizing-you-up to teasing/flirty; Olivia *thaws* — full sentences, the rare dry joke, never gushing; Alexa's sarcasm turns into affection and shop-pride; Rebecca goes from farmer's reserve to mothering shamelessly. The tier lines live per-trader in data precisely so an operator can retune each voice.
- **The leash loosens with the warmth.** Word cap in the prompt scales 30 → 40 (tier 2) → 45 (tier 3), and the chat path raises the sanitizer/token caps (220 chars/80 tokens → 320/110 at tier 2+) so a warm aside isn't truncated mid-sentence.

### Notes
- `rq.slm.test` and the passerby line have no progress record and correctly land on tier 0.
- Live `traders.json` picked the new fields up via the load-time blank-fill — no sync command was needed this time.

## [1.10.0] — 2026-08-08 — the island starts leaving notes

### Added
- **Resistance pamphlets seeded into crate loot.** The Unpaid — ex-Cobalt ground staff, faction bible in `docs/content/resistance.md`, deliberately planted as the v2 storyline hook — now slip anti-Cobalt propaganda into monument crates. `OnLootSpawn` (verified live: it fires before `PopulateLoot`, so the print is added on `NextTick`), gated by `PamphletLootChance` (default 0.05, `crate*` prefabs only). Hook subscribed only when the chance is >0 AND JPGs exist in `oxide/data/RustQuests/pamphlets/` — zero-regression doctrine as usual.
- **Nine prints shipped.** Photo items stamped via `PhotoEntity.SetImageData(OwnerStamp, …)` — the same tech as FakeFriends' roleplayer photos. Sources are HTML compositions rendered through a headless-Chrome pipeline in `assets/pamphlets/` (`render.ps1`): re-rendered at 2× (1708×960) for inspect-view sharpness with the image cache limit raised 256KB → 512KB, compositions scaled into a frame-safe zone, and given a dirt pass so they age like Rust's in-world papers.
- **Admin surface:** `rq.pamphlet.give` to hand yourself prints; in-game verify is that plus looting a few monument crates.

### Fixed
- **Finale objective lines pointed at the wrong trader's stash** (post-release fix on this version).

### Notes
- This replaced the never-shipped "lore notes at monuments" idea — the note-box machinery (`rq.note.place`, `readnote` objectives, `shop_rumor` content) still exists, but nothing auto-places it.
- Not yet playtested as of shipping; the loot hook behavior itself was verified live.

## [1.9.2] — 2026-08-07 — room for a bigger brain

### Changed
- **`SLMTimeoutSeconds` shipped default 15 → 25.** the owner moved both plugins' live configs to `gemma3:12b` (the 3060 upgrade holds a 12B resident); measured cold load through the plugin's request shape is **15.9s**, so the old 15s default would eat the first call of the day as a timeout and feed the breaker a free strike. Live config already carried 25 — this aligns the shipped default, same bump FakeFriends made. Warm replies ~2.6s. `SLMModel`'s shipped default deliberately stays `llama3.2:3b` (both plugins' convention: the model choice lives in the config file).

## [1.9.1] — 2026-08-01 — the journal grew tabs

### Added
- **`/quests` journal reworked into two tabs.** **The Work**: every trader in chain order — strangers stay anonymous with a vouch hint, known traders show jobs-done count, shop grid, and the current job with live objective progress (or next-job-waiting / vault-pending / list-clear). **The Story**: a per-trader selector (locked traders render as unclickable `???`) and one page per lived interaction — her greeting, each taken quest's offer prose, the completion prose once claimed (tokens expanded, so a forgotten stash code can be re-read), and her chain-done referral onward. Prev/next pagination lands on the latest chapter.
- Built entirely from the progress file + authored content already on disk — nothing new is persisted, and SLM chat transcripts are deliberately not part of the story (by design; sessions stay ephemeral beyond the one distilled memory sentence).

### Notes
- the owner's in-game test confirmed the chat panel input field works live — Enter submits, and the full-panel re-render with `Autofocus` regains focus. The `CuiInputFieldComponent` spike is settled; panel delivery is the proven path.

## [1.9.0] — 2026-08-01 — the traders talk back

### Added
- **SLM trader chat, live and enabled.** FakeFriends' Phase 7 brain ported intact — `SlmAsk` (OpenAI-compatible `/v1/chat/completions`, Oxide webrequest, `done(null)` on every failure path), `SlmSanitize` (rich-text strip, quote swap, preamble/alternatives trim), 3-strike/5-minute circuit breaker, tumbling hourly budget (default 60) — pointed at the same Ollama endpoint and `llama3.2:3b` as FakeFriends so one copy stays VRAM-resident. Smoke-tested through the plugin's exact request shape: 8.7s cold load, **1.1s warm**.
- **Quest-aware system prompt.** Each trader gets an authored `Persona` in `traders.json` (blank-filled on load, re-synced by `rq.trader.synctext`), plus live state per player: the chain quest they're on with objective progress, the job waiting if they haven't taken it, chain-done/vault-pending phrasing, and pinned world facts so the 3B doesn't invent factions and farmers' markets. Stash codes and grids are never in the prompt.
- **Chat sessions.** A **Chat** button in the dialog header (unlocked traders only) opens a CUI chat panel — the plugin's first `CuiInputFieldComponent` (Enter submits `rq.ui.chat.send`, full panel re-render + `Autofocus` refocuses after each exchange; this ships as the live spike). 5-minute session, 60-minute per-player+trader cooldown charged only when a real exchange happened. `SLMChatPanel: false` switches delivery to proximity game chat (`OnPlayerChat` hot-subscribed only while such a session exists).
- **Trader memories.** Session end distills one ≤120-char sentence ("the most important thing you learned about them") into `TraderMemories` in the player's progress file — cap 4 per trader, oldest fades, wiped with everything else, fed back into her next session's prompt. Works even when the player logs off mid-goodbye: the distill callback re-reads and re-writes the flushed progress file.
- **Admin surface:** `rq.slm` (endpoint, ok/fallback tallies, latency, budget, breaker, live sessions), `rq.slm.test <trader> <message…>` (persona round-trip from the console, no player needed).

### Notes
- Zero-regression doctrine inherited: empty `SLMEndpoint` disables everything — no button, no hook, no webrequest.
- Live-assembly finding: `ConsoleSystem.Arg.FullString` is now a `Facepunch.StringView`; call sites need `.ToString()`.

## [1.8.5–1.8.7] — 2026-07-31 — the caboose lights itself now

### Fixed
- **Shop lights now come on the way the caboose intends — by its own switches** (v1.8.5, from the owner's tip that the caboose has a working light switch inside the door). A player flip goes through `ElectricSwitch.SetSwitch`, which sets the flag AND marks the IO graph dirty so the internal wiring powers every lamp downstream; the old code set flags without the dirty-mark and then force-fed each lamp a transient 25 energy the sim recomputed away. The plugin now flips the caboose's two `caboose_lightswitch` entities exactly as a player would, and only direct-feeds devices the wiring genuinely never reaches.
- **The lights were blinking on a 5-minute cycle, and it was partly self-inflicted** (v1.8.6, found via a live entity dump). The caboose feeds its switches through an **XOR** whose other input is `caboose_generator` — and the old code force-flagged the generator on, making both XOR inputs hot, which cut the whole lighting circuit and let the sim clear the switch flips. Power sources (0-input IO entities) are now never touched.
- **Even untouched, the sim clears unattended switch flags on a ~5-minute cycle** (exact trigger unidentified; likely the save/IO maintenance pass). The re-flip timer is the accepted self-heal: now every **60s** instead of 300s, capping any dark window at a minute (v1.8.7). `SetSwitch` no-ops when already on, so a healthy tick costs 8 flag checks across all four shops.
- **Light-timer log spam eliminated** — the old top-up logged `powered 4 device(s)` per shop per pass (~1,150 lines/day into console + logfile) because re-fed devices always counted as changed. Routine re-flips are now silent; `rq.shop.lights` reports full per-entity state (type, IO inputs, powered, on) on demand.

### Notes
- Out of a full performance review (2026-07-31): everything else passed — hot hooks are subscribed only while needed, `OnEntityTakeDamage` stays three cheap tests, the no-build check is pure math, and per-player state evicts on disconnect. The lights timer was the only genuine finding. `OnPlayerInput` (the one per-frame hook, needed for E-through-the-cashier-cage) is negligible at low pop; if the server ever runs 30+ players, arm it by proximity from the 2s sweep.

## [1.8.4] — 2026-07-31

### Changed
- **Olivia's The Good Stuff adds a jackhammer and 50 F1 grenades** (the owner) — placed on the quest *before* Recall Notice, deliberately, so the player walks into the Bradley job already carrying them. Her dialog folds them in. Reaches live via `rq.quest.syncrewards olivia_hqm` + `rq.quest.synctext olivia_hqm`.

## [1.8.3] — 2026-07-31

### Changed
- **Alexa's refinery quest de-jargoned** (the owner, playtest): "Crack It Yourself" → **"Cook It Yourself"**, and the closing line went from "It's cheaper and nobody shorts you" to "It's cheaper and nobody shoots you" — oil-cracking was a refinery in-joke that read as a typo. Needs `rq.quest.synctext alexa_refined` on the live server.

## [1.8.2] — 2026-07-31

### Fixed
- **Purchase-quest offers rendered their rewards as "nothing."** `{rewards}` only read the quest's claim-time `Rewards` list, but purchase quests (Rebecca's Good Soil, Olivia's Tools of the Trade) pay everything through `GrantOnAccept` and have no `Rewards` — so Good Soil's offer read "…you walk out of here a farmer: nothing." (found live). The token now falls back to `GrantOnAccept` when `Rewards` is empty; claim rewards still win when both exist. Code-only; no sync needed.

## [1.8.1] — 2026-07-31

### Fixed
- **Long dialog bodies clipped off the bottom of the CUI — including the line that mattered most.** A `CuiLabel` silently truncates past its bottom edge, the finale offers run 650–875 characters, and every one of them puts the stash grid + code on the *last* line: found live when Sonia's "The Long Way Round" never showed where her stash was. The dialog panel is now taller (0.17–0.83 of screen height, was 0.22–0.78) and the body font steps down as text grows (13pt ≤500 chars, 12pt ≤800, 11pt above — 11pt holds roughly double the longest authored dialog). Code-only; no sync command needed.

## [1.8.0] — 2026-07-31 — Olivia reworked; the quest pass is complete

### Changed
- **Olivia's chain retuned** (the owner's spec). Her purchase now includes the harvesting tools (research table + salvaged icepick + salvaged axe); the four turn-in quests keep their objectives (3000 wood / 3000 stone / 2000 metal ore / 100 HQ ore) but pay full escalating ammo tiers — 300 pistol rounds, 225 shells, 400 rifle rounds, then a hazmat suit + rocket launcher + 10 HV rockets + 10 syringes. Her cellar is the severance shipment at full strength: 500 scrap, **20 C4, 20 rockets, 100 explosives, 2000 gunpowder, 50 tech trash**.
- **The Bradley kill is back — as Olivia's "Recall Notice."** Every shell the launch-site tank fires went through her QA checkpoint; she's issuing a recall. Pays the grenade launcher + 150 rounds of 40mm. This also un-duplicates the 5-scientist kill that Alexa's Right of Way took over in v1.7.0. Quest id `olivia_scientists` kept for progression wiring.
- Her Q4/Q5 dialog rewritten: the old lines promised the launcher "comes last" when 40mm arrived at Q4 — now the rocket launcher lands at The Good Stuff and the tube-and-40mm at the recall.

### Notes
- "10 meds" resolved to `syringe.medical`. All 126 shortnames in the plugin verified against the item bundle.
- Reaches the live server via `rq.quest.syncrewards olivia` + `rq.stash.syncloot olivia_finale` + `rq.quest.synctext`.
- With this, all four chains are reworked (Rebecca v1.6.x, Alexa v1.7.0, Olivia v1.8.0; Sonia's hunting chain was already in its intended shape). Next feature: SLM trader chat, after the GPU swap.

## [1.7.2] — 2026-07-31 — weapons arrive fitted

### Added
- **`Attachments` on rewards** — a list of weapon-mod shortnames seated into each granted weapon before it's handed over, using the same `item.contents` mechanism as the filled water jugs. A mod that can't seat (unknown shortname, no mod slots, slots full, incompatible) is given to the player loose with a log line rather than lost. `{rewards}` renders them as "(fitted: …)". Works in quest rewards and stash loot alike via the shared `GrantRewardItem`.
- **Sonia's cellar AK** now carries a weapon flashlight, holosight and extended magazine. **The vault's three guns arrive fitted**: L96 with lasersight + extended mag + Variable Zoom Scope, M249 with lasersight + holosight, M4 Shotgun with lasersight + extended mag + holosight.

### Notes
- Shortname trap, verified against the item bundle: the **Variable Zoom Scope is `weapon.mod.8x.scope`** and `weapon.mod.small.scope` is the 8x Zoom Scope — the shortnames are inverted from the display names.
- Reaches a live server via `rq.stash.syncloot sonia_finale` + `rq.stash.syncloot grand_finale`.

## [1.7.1] — 2026-07-31

### Changed
- **Every cellar now carries 500 scrap** (the owner): Rebecca's and Alexa's stashes gained the 500 that Sonia's and Olivia's already had.
- **Sonia's stash drops the 2 timed explosives** — her cellar is now scrap, AK + ammo, metal armour, medkits and supply signals; C4 stays Olivia's territory.
- **Catch and Keep gains a chicken coop and a beehive** — eggs and honey alongside the water gear, with her offer/complete lines extended to match (shortnames `chickencoop` / `beehive`, verified).

## [1.7.0] — 2026-07-31 — Alexa reworked

### Changed
- **Alexa's whole chain retuned** (the owner's spec). Repair bench to start (unchanged), then: strip 10 of every low-quality engine part for a T1 workbench + engineering workbench; 5 diesel + 300 crude for the car lift and 4 garage doors; 15 tech trash for a T2 workbench + small oil refinery; 5 CCTV cameras + 5 laptops + 5 green keycards for a tier-3 internals set and 2000 low grade; kill 5 scientists for a complete rig (cockpit-with-engine, camper, 2 storage modules, engine module). Finale pays 300 scrap from her hand; the cellar is the motor pool's master set — **T3 workbench plus all ten workbench upgrades**.
- **The Bradley kill left the chain.** Its slot (`alexa_bradley`, id kept for progression wiring) is now the 5-scientist "Right of Way", using the same `Scientist` species key Olivia's Field Testing has already proven live. Quest ids all kept, so Olivia's unlock (`alexa_finale`) and the vault's four-finale requirement are untouched.
- **Her finale dialog no longer references the tank.** The offer's "You killed the tank" claim-line became "You cleared my roads", and the crate's story now matches its contents — the master bench set packed for a transfer that never happened.

### Notes
- "Laptop" resolves to `targeting.computer` — no vanilla item is literally named Laptop; the laptop-looking puzzle component is it (confirmed with the owner). Diesel is `diesel_barrel`, the engineering workbench is `iotable`.
- All 113 item shortnames in the plugin (the sweep now includes stash loot tables) verified against the server's item bundle; all resolve.
- Reaches a live server via `rq.quest.syncrewards alexa` + `rq.stash.syncloot alexa_finale` + `rq.quest.synctext`.

## [1.6.4] — 2026-07-30

### Changed
- **Three of Rebecca's quest names now describe what the quest is actually about** (the owner). "Full Larder" → **Catch and Keep** (rain caught, water stored), "Something to Run It" → **Down One Wire** (the nine root combiners are the point), "On the Grid" → **While You Sleep** (conveyors and adaptors moving stock unattended — and a quiet setup for the finale, where she admits she couldn't). The old names were left over from the pre-1.6.0 chain, when those quests paid sprinklers, a wind turbine and a solar array respectively. Names only — objectives and rewards untouched, so `rq.quest.synctext` alone carries this.

## [1.6.3] — 2026-07-30

### Changed
- **Rebecca's cellar is now exactly the five items and nothing else** (the owner): water wheel, wind turbine, test generator, 5 advanced healing tea, 5 advanced max health tea. The 400 scrap, seed stock, blue berry clones, 4 large planters and 100 fertilizer that had been in there since v1.0.0 are gone — her chain pays the farming, her cellar pays the power, and the 300 scrap for the finale comes from her hand on the claim.
- **Her finale dialog was rewritten to match what's actually in the box.** It had promised "what was left of the seed stock" and thanked the player for seeds that "haven't seen sun in years" — with the seeds removed, both lines were describing loot that no longer exists. She now buries the gear that arrived two months too late, and the tea she brewed for the nights she couldn't sleep.

## [1.6.2] — 2026-07-30

### Changed
- **Rebecca's cellar drops the 6 sprinklers and the small fuel generator** (the owner). Her chain no longer teaches plumbing, so the sprinklers arrived without a pump to run them; and between the water wheel, the wind turbine and Q3's ten-panel array, the cellar was handing out a fourth power source nobody needed.

## [1.6.1] — 2026-07-30

### Changed
- **Rebecca's finale gear moved from her hand into the cellar** (the owner). She now pays 300 scrap on the claim, and the water wheel, wind turbine, test generator and 5 each of advanced healing/max-health tea come out of the buried crate instead — which is where the story says they've been sitting all along. Note that stash loot is *granted to whoever opens it* rather than physically stocked in the box (an existing decision: a shared container let a second finisher walk off with someone else's share, and the box runs `EnableSaving(false)`), so the items still arrive in the opener's inventory.

### Added
- **`rq.stash.syncloot [tag]`** — third of the sync family. `stashes.json`, like `quests.json` and `traders.json`, only ever gains keys it has never seen, so a revised loot table can't reach a live server without it. Takes one tag to keep the blast radius small; placements, codes and claim history live in `state.json` and are untouched.

### Fixed
- **Stash loot had the same over-stack bug as quest rewards** — `GrantStashLoot` created one item per entry at the full amount regardless of stack size. Both paths now share `GrantRewardItem`, so everything the plugin hands out arrives in stack-legal pieces.

## [1.6.0] — 2026-07-30 — Rebecca reworked, and rewards that arrive intact

### Changed
- **Rebecca's whole chain retuned** (the owner's spec). Her purchase is now a farm in a box — 2 large planters, 100 fertilizer, 20 hemp seed and 4 **filled** water jugs — and the chain runs soil → water → power → industrial: 1000 cloth for a composter and three seed types; 100 corn + 100 pumpkins for a large water catcher, water barrel and cooking workbench; 1000 low grade for a ten-panel solar array, large battery, 9 root combiners, branches, fridge and splitter; 20 tech trash + 100 HQM for 3 electric furnaces, 4 storage adaptors and an industrial conveyor. Her finale adds a water wheel, wind turbine, test generator and 5 each of advanced healing/max-health tea to the 300 scrap. Objective wording and her teaching lines were rewritten to match — the old text sold sprinklers and timers she no longer hands out.

### Added
- **`Water` on rewards.** A reward can now hand over a *filled* container: `Water` is millilitres loaded into each granted item, clamped to what the container actually holds and logged when clamped. A Water Jug caps at 5000 ml (the item's own description says so), which is why 20 000 ml of water is four jugs, not one. Verified against the live assembly: `ItemModContainer.OnItemCreated` builds `item.contents` server-side and `maxStackSize` is the liquid capacity.
- **`rq.quest.syncrewards [quest-id|trader]`** — the other half of `rq.quest.synctext`: pulls shipped objectives, rewards, accept cost and on-accept grants over `quests.json`. It deliberately discards live tuning for what it touches, so the filter accepts a **trader key** as well as a quest id — reworking one chain can't reach across and flatten another trader's tuning. Prerequisites, giver and tree are untouched.

### Fixed
- **Rewards larger than a stack were handed over as a single illegal stack.** `GiveRewards` created one item with the full amount regardless of the item's `stackable`, so 10 large solar panels (stack 3) and 3 electric furnaces (stack 1) arrived as over-stacks — deployables in that state can't be placed without splitting them first. Rewards are now granted in stack-legal pieces, which is also what makes 4 water jugs four *separate full* jugs rather than one quadruple-stacked empty one. Existing quests are unaffected except where they already exceeded a stack.

### Notes
- Every item shortname in the plugin (74 of them, across all four chains) was checked against the server's item bundle. All resolve. Two in Rebecca's new list would have failed a reasonable guess: Storage Adaptor is `storageadaptor`, not `storageadapter`, and the large battery's shortname misspells "rechargable" while its display name doesn't.
- Deploying does not retune the live `quests.json` — the load-time merge only adds quests it has never seen. `rq.quest.syncrewards rebecca` is what moves this content onto a running server.

## [1.5.0] — 2026-07-30 — the traders are a chain

### Added
- **Trader progression: Sonia → Rebecca → Alexa → Olivia.** A trader now does business only with players another trader has vouched for. Sonia is the one open door every wipe; claiming her finale opens Rebecca, Rebecca's opens Alexa, Alexa's opens Olivia. The wiring is data, not code — `RequiresTrader` and `UnlockQuest` per profile in `traders.json`, so the order is a two-line edit and an operator can open any trader permanently by setting `RequiresTrader` to `""` (an explicit empty string is preserved across loads; a missing key is filled from the shipped default). Unlocks are per player and per wipe, recorded in `UnlockedTraders` in the player's progress file.
- **Cold greetings.** A trader who hasn't been introduced greets you without giving her name — the dialog header reads *A stranger* — points at the slot machines as the only thing on offer, and shows no quest button at all. Four authored `LockedGreetingText` lines, one per voice. The generic Lang fallback exists but no shipped trader uses it.
- **`ChainPendingText`** — a distinct line for "my list is clear but I have something you haven't earned the right to yet", separate from "we're genuinely finished". Sonia is the case that needed it: after her own finale the vault is still waiting on the other three chains, and until v1.5.0 she said *nothing left to send you after* while a quest of hers sat blocked. `CurrentChainQuest` now reports the blocking quest so the dialog can tell the two states apart.
- **`{next}` and `{nextgrid}` tokens** — the name of the trader this one hands you on to, and where her shop actually landed this wipe. Every referral line uses them, so a referral can never point at a stale grid reference.
- **`rq.trader.synctext [trader]`** — pulls revised trader *dialog* from the plugin over `traders.json`, leaving face, kit, offsets, biome, shop mode, stash tag and unlock wiring exactly as tuned. The load-time merge only fills blank fields, so this is the only way reworded dialog reaches a live file. Mirrors `rq.quest.synctext`.
- **`rq.unlock <player> [trader|all]`** — show a player's progression state, or hand out introductions they'd otherwise have to earn. `rq.status` now reports each trader's unlock wiring.

### Changed
- Rebecca's, Alexa's and Olivia's chain-done dialog now names the next trader and her grid; Olivia's — the end of the chain — sends the player back to Sonia for the vault. Sonia's chain-done text is now reserved for after the vault is open. **These four rewrites need `rq.trader.synctext` on a server whose `traders.json` already has the old text**; every genuinely new field fills itself on load.

### Notes
- Shop map markers are world entities, not per-player, so all four shops remain visible on everyone's map regardless of unlock state. That is deliberate: walking into a locked trader's shop and getting the cold shoulder is how the progression announces itself.
- Dropping this onto a live wipe cannot strand anyone. `IsTraderUnlocked` treats any trader the player already has an active or completed quest with as open, so a quest accepted before the rule existed stays claimable, and a player who finished the unlock quest before upgrading is let in without needing the new bookkeeping.

## [1.4.2] — 2026-07-26

### Fixed
- **The no-build zone missed deployables — including the walls somebody would actually use.** `CanBuild` only fires from `Planner`, which places building blocks; deployables go through `Deployer`, and the live assembly shows `Deployer.DoDeploy` gating on `CanDeployItem` alone and never calling `CanBuild`. So the zone stopped foundations and twig walls while leaving high external stone walls — the standard way to ring somebody in — and cupboards completely unblocked. That is the exact griefing path 1.4.0 was written to close. `CanDeployItem` is now hooked alongside `CanBuild`, subscribed and dropped by the same `RecomputeProtectionHook` condition. The hook doesn't carry the placement point, so it tests the player's own position widened by `Deployer`'s 8 m placement reach: it can refuse a placement just outside the ring, never permit one inside it. Admins stay exempt. Because that widened ring doesn't match `ShopNoBuildRadiusMetres`, the refusal uses a new `Deploy.Blocked` lang line that quotes no distance — `Build.Blocked` would have told a player standing 30 m out that he was inside a 25 m ring.

## [1.4.1] — 2026-07-26

### Fixed
- **`rebecca_grid` paid no solar panels.** The reward used `solarpanel.large`, which is not an item shortname — the engine's only one is `electric.solarpanel.large` (confirmed against the server's vanilla item dump). The plugin warned and skipped the item at every load, so the quest's headline reward silently never arrived. Fixed in the shipped default and in the live `quests.json`.

## [1.4.0] — 2026-07-26 — shop no-build zone

### Added
- **No-build zone around every shop** (`ShopNoBuildRadiusMetres`, default 25 m). A `CanBuild` hook cancels player construction inside the radius and says why in chat — closing the standing hole where a player could wall a trader in and deny the quest line to the whole server, with the map marker advertising exactly where to do it. It's a cylinder, so the airspace above the shop is denied too; admins are exempt.
- **Public API for other plugins** — `IsNearQuestShop(Vector3 pos, float extra)`, `GetQuestShopNoBuildRadius()`, `GetQuestShopPositions()`. FakeFriends v0.13.14 calls the first of these before anchoring a bot's home or pasting a base, which is what keeps resident bases off the traders' doorsteps: cabooses are prefab entities, not building blocks, so FakeFriends' own footprint checks never saw them.
- Shop placement now rejects spots with an existing build inside the no-build radius (new `SpotReject.Neighbours`, tallied by `rq.shop.diagnose`) — a shop must not land on somebody's back yard and freeze it, and at wipe the bot bases go down before the shops do.

## [1.3.0] — 2026-07-26 — the welcome note

### Added
- **Welcome note.** Every player is handed a note item five seconds after their first wake-up each wipe (once per player, tracked in `state.json`). It names the four as ex-Cobalt without naming three of them — only Sonia is identified, with her railcar and her willingness to explain things — and it teaches the two controls a new player needs: press E at her counter, and `/quest` for the journal. Text lives in `notes.json` under the `welcome` tag and uses `{trader}`, so it's editable live like every other note.
- **`/quest`** as an alias for `/quests` (the note teaches the shorter one).
- `rq.note.welcome` — re-delivers the note to yourself regardless of the once-per-wipe ledger, for testing wording.
- Config `WelcomeNote` gates the whole thing; when off, `OnPlayerSleepEnded` is never subscribed.

## [1.2.0] — 2026-07-26 — placement that respects biomes

### Fixed
- **Traders drifted out of their biome.** The spot search abandoned the biome requirement after 250 of 400 attempts and took whatever fit, and trees counted as hard blockers — which in jungle (the densest, rarest biome) rejected ~90% of otherwise-valid spots. Sonia therefore almost never landed in the jungle. Now: strict biomes by default (`ShopBiomeStrict`), trees are felled rather than avoided (`ShopClearTrees`), a much larger search budget (`ShopSearchAttempts`, 4000), and a retry loop instead of settling for the wrong biome.
- **Sonia's biome read "any"** — her jungle preference was inferred from the wipe's quest tree instead of stored, so diagnostics tested her unconstrained. Biomes are now explicit per profile with a `BiomeFallback` used only when the preferred biome is genuinely exhausted (Sonia jungle→temperate, Olivia arctic→tundra, Alexa arid→temperate, Rebecca temperate→tundra).
- **`rq.shop.biomescan` under-sampled rare biomes**, reporting "0 usable" for jungle when the real figure was ~20%. It now samples in-biome positions with the same 6 orientations placement uses.

### Added
- `rq.shop.diagnose <biome>` — tallies exactly which constraint rejects spots (flatness / topology / rock / doorway / monument) plus mean ground spread, so tuning is aimed rather than guessed.
- Footprint constraints are config-tunable without a redeploy: `ShopFlatnessMetres`, `ShopClearHeightMetres`, `ShopDoorClearanceMetres`.
- `rq.trader.move <back> [right] [up] [yaw] [trader]` — reposition a trader relative to her own facing; re-derives her shop-relative offsets so the fix carries into every future placement. `rq.trader.nudge` is now shorthand for its vertical case.
- `rq.quest.synctext [id]` — pulls revised dialog from the plugin over `quests.json` while leaving rewards, objectives, costs and prerequisites as tuned.

### Changed
- Sonia's and Olivia's offer text now uses the `{rewards}` token, so retuning their rewards can't leave the dialog stale.

## [1.0.0] — 2026-07-26 — all four traders + the vault

### Added
- **Alexa** (arid) — launch-site mechanic. Repair bench to start, then gears → engine internals, tech parts → vehicle modules, HQM → workbench 2, a big haul → **car lift + workbench 3**, and **destroying the Bradley APC** for a 4-mod chassis and armoured cockpit. Finale "The Last Contract": she serviced the company's armour and took one crate that wasn't hers.
- **Rebecca** (temperate) — greenhouse grower and electrician. Planter to start, then cloth → composter and seed, crops → sprinklers and plumbing, fuel → wind turbine and battery, tech parts + HQM → solar array, battery bank and sensors. Finale "What the Lights Were For": she chose which greenhouse rows lived when the power failed, and buried the surviving seed stock.
- **The vault — "Four Keys"** — a cross-trader finale requiring all four personal finales. Four women off the same payroll, one code split four ways. Contains the full BDU + ballistic kit, L96, M249, M4 shotgun and 1000 scrap.
- **`{rewards}` dialog token** — offer text expands to the quest's actual reward list, so retuning rewards never leaves the dialog stale.
- Multi-prerequisite quests (`RequiresQuests`), Bradley and patrol-helicopter kill targets, and automatic stash placement for any stash a quest references (not just per-trader finales).

### Notes
- No "L9" item exists on this build; the vault ships the **L96 Rifle** — change `grand_finale` in `stashes.json` if a different weapon was meant.
- Alexa and Rebecca start on Sonia's face and outfit by design; cycle and dress them per taste.

## [0.9.2] — 2026-07-25 — multi-trader + Olivia

### Added
- **Multi-trader support.** The plugin now tracks any number of traders, each with her own shop, biome preference, booth calibration, quest chain, map marker, and optional finale stash. Every admin command takes an optional trader name or infers it from whoever you're standing next to; `rq.shop.autoplace` with no argument places them all.
- **Olivia** — arctic arms dealer running a foundry. Chain: 50 scrap for a research table, then 3000 wood → pistol ammo, 3000 stone → shotgun ammo, 2000 metal ore → 5.56 ammo, 100 HQ ore → 40mm ammo, kill 5 scientists → grenade launcher. Finale "Cold Storage": she was a munitions QA fabricator for the scientists until she stopped signing off on crates, and her last shipment is buried on the map behind a code.
- **Per-trader dialog** (greeting/progress/ready/chain-complete) in each profile, so traders don't inherit each other's voice.
- Per-trader finale stashes, each hidden at its own random spot with its own code each wipe.

### Fixed
- **Only one scientist variant counted as a kill.** Scientists span two unrelated class hierarchies — classic `scientistnpc_*` (BasePlayer-derived, reports "player") and Gen2 `scientist2*` (`ScientistNPC2 : BaseNPC2`, reports "Scientist2"). Matching now runs on the prefab name and covers every variant, while excluding corpses, ragdolls, plushie deployables and `sentry.scientist.*` turrets.
- **Trader profiles written by older versions were missing every field added since** (how Olivia inherited Sonia's hunting dialog and had no finale stash). Profiles now fill blank fields from the shipped defaults on load, never overwriting anything the operator has set.

## [0.8.0] — 2026-07-25 — hardening

Three parallel deep reviews (correctness/state, performance/uMod compliance, edge cases/multiplayer/exploits) and the fixes they produced.

### Fixed — critical
- The finale was **uncompletable for the first session of every wipe**: the stash is created after the code hook's subscription decision, leaving `OnCodeEntered` unsubscribed.
- Reading the stash code in Sonia's offer text *before accepting* permanently softlocked the finale — once whitelisted on the lock, the keypad never reappears. Opening the box now also credits the objective.
- **Wipe detection never fired on a same-seed wipe or custom map** (`seed-size` is constant), so players kept a completed chain into a fresh wipe. Wipe ids now carry a counter; `rq.wipe.reset` genuinely resets progress.
- An exception anywhere in boot world-setup left every engine hook unsubscribed for the whole session (no kill credit, no saves). Setup is now guarded; hooks wire regardless.

### Fixed — high
- Stash loot went into a **shared container**: a second finisher could be robbed, and anything left inside vanished on restart. Loot is now granted directly to each player who earned it.
- Hot-reloading the plugin silently stopped crediting kills for players already online.
- A typo in `quests.json` or `traders.json` **overwrote the file with defaults**, destroying hand-tuned content and the trader's calibration. Parse failures now leave files untouched and run on defaults.
- The entity owner stamp was a structurally valid SteamID64 — that account's entire base would have been indestructible and sweepable. Moved below the Steam range (old stamp still honored).

### Fixed — performance
- Removed per-kill allocations: quest lookup is an index instead of a `List.Find` closure, and kill targets are pre-split at load.
- `OnPlayerInput` (per player, per frame) and `OnEntityTakeDamage` (hottest hook in the game) reordered to their cheapest test first.
- Placement search: layer masks hoisted out of the inner loop; the finale stash uses a light spot-finder instead of the caboose-sized footprint test.
- `OnItemAddedToContainer` gates on an item id instead of a string compare.

### Fixed — abuse and lifecycle
- Dead or sleeping players can no longer claim rewards into a discarded corpse inventory.
- `rq.ui.*` throttled per player; `rq.dev.*` restricted to authlevel 2 (it spawns arbitrary protected prefabs).
- Accepts and claims save immediately, closing a crash-duplication window.
- `rq.shop.remove` no longer destroys nearby note boxes and stashes; re-placing a shop removes the old one instead of orphaning it.
- The trader self-heals if another plugin or an admin destroys her; dialogs close on trader removal and on respawn.
- Dev-spawned entities and in-flight paste callbacks are cleaned up on unload.
- `readnote` objectives credit by tag (they could never match before); chain order follows prerequisites rather than file order; stash codes validated as 4 digits; `TraderGodMode: false` now actually disables immunity.

### Added
- [README.md](README.md), [CHANGELOG.md](CHANGELOG.md), [wipe checklist](docs/wipe-checklist.md), MIT [LICENSE](LICENSE).

### Verified
- Wipe drill passed live: full unattended sequence (state reset → tree detection → stash hidden → shop auto-placed with trader, lights and map marker) with no intervention.

## [0.7.1] — 2026-07-25

### Added
- **Trader NPC (Sonia)** — spawned NPCTalking NPC with a fixed identity: face seed, authored outfit with Steam Workshop skins, name, and god-mode. Identity lives in `oxide/data/RustQuests/traders.json`; `rq.trader.reload` applies edits live.
- **Dialog UI** — E on the trader opens a CUI dialog (greeting, quest offer/progress/claim, leave). Interaction works through the caboose's cashier cage via an aim-ray proximity test.
- **Quest engine** — data-driven definitions (`quests.json`) with five objective types (kill, turn-in, visit, read-note, code-entry), offer → accept → progress → claim lifecycle, per-player progress files, and prerequisite chains.
- **Sonia's hunting chain** — 50-scrap Combat Knife purchase, then five sequential tracked-kill quests (chicken → boar → stag → big cat → crocodile) rewarding a bow → compound → Python → SAR → LR-300 ladder. Swaps to wolf and bear on maps without jungle.
- **Story finale** — "The Long Way Round": her rail-crew backstory plus the grid reference and code to a buried stash, hidden at a fresh random spot with a fresh code each wipe. Loot granted per player, so concurrent players each get a full share.
- **Journal** — `/quests` shows active quests, objective progress, and what to do next.
- **Caboose shop** — `traincaboose.static` spawned as one entity (furnished, working slot machines), placed at a validated random location: jungle-preferred, flat, clear of rocks/trees/monuments/roads/player builds. Interior lights switched on automatically; shows on the map as a named shop pin. Auto-places itself after a map wipe.
- **World objects** — lore notes (`notes.json`) and codelocked stashes (`stashes.json`) placeable by admin command, indestructible, respawned from state on load.
- **Admin tooling** — `rq.status`, shop placement/removal/calibration, trader face cycling and height nudging, quest listing and per-player reset, stash re-roll, prefab spawn probe.

### Notes
- Content files merge on update: new authored quests, notes, and stash tables appear without overwriting live edits.
- CopyPaste is optional (only for `ShopMode: "paste"`); the default caboose shop has no dependencies.

### Design decisions
- [0001](docs/decisions/0001-trader-delivery-architecture.md) — custom NPCs over vanilla mission providers; workshop skins over impossible custom models; kill tracking over DLC-gated head turn-ins.
- [0002](docs/decisions/0002-v1-scope-story-model.md) — v1 scope, per-wipe story model, scripted dialog first, vanilla non-DLC rewards only.
- [0003](docs/decisions/0003-caboose-shop.md) — the train caboose as the shop; vending and trophy walls cut in favor of the caboose's own slot machines.

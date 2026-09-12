using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using UnityEngine.AI;

namespace Oxide.Plugins
{
    [Info("Rust Quests", "LowPopLabs", "2.20.3")]
    [Description("Story-driven quests: hidden trader shops, lore notes, puzzles and hunting chains that change each wipe.")]
    public class RustQuests : RustPlugin
    {
        // Rust 2633.288 (2026-09-03) removed BaseEntity.SetFlag; flags now change inside a
        // FlagsUpdateScope. SendNetworkUpdate mode matches what the old call did.
        private static void SetFlagNet(BaseEntity entity, BaseEntity.Flags flag, bool value)
        {
            using (var scope = entity.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
            {
                scope.Set(flag, value);
            }
        }
        // Optional: only used by traders whose profile sets ShopMode "paste".
        // The default caboose shop spawns without it (decision 0003).
        [PluginReference] private Plugin CopyPaste;

        // Every entity this plugin creates or pastes carries this OwnerID stamp:
        // it drives damage protection, shop-removal sweeps, and rebinding after
        // restarts (net IDs don't survive a reboot; the stamp does).
        //
        // Deliberately BELOW the SteamID64 range (76561197960265728+): an earlier
        // build used a structurally valid Steam ID, which would have made every
        // entity owned by that account indestructible and sweepable if they ever
        // joined. LegacyOwnerStamp keeps entities placed by those builds working.
        private const ulong OwnerStamp = 1000733001UL;
        private const ulong LegacyOwnerStamp = 76561199720260725UL;

        private static bool IsOurStamp(ulong ownerId) => ownerId == OwnerStamp || ownerId == LegacyOwnerStamp;

        // -----------------------------------------------------------------
        // Phase 1 (Foundation): trader NPC + conversation-intercept dialog.
        // Phase 2 (Core):       data-driven quest engine — objective types
        //                       kill/turnin/visit (readnote/code land with
        //                       their Phase 3 world placers), per-player
        //                       progress, Sonia's hunting chain as content,
        //                       jungle/temperate tree pick per wipe, journal.
        // Phase A (v2):         wipe-bible generator foundation — per-wipe
        //                       template roll (four_keys = the v1 story),
        //                       Arc-tagged quest filtering, effective chain
        //                       wiring, (Id,Tree,Arc) merge + migration.
        // Design source of truth: PLAN.md + docs/content/sonia-arc.md
        // API verification:       docs/DIAGNOSTICS.md (P2, 2026-07-25)
        // Decisions:              docs/decisions/0001, 0002, 0004 (v2 story model)
        // -----------------------------------------------------------------

        #region Config

        private ConfigData _config;

        private class ConfigData
        {
            // Trader identity (name, prefab, outfit, face, shop) lives in
            // oxide/data/RustQuests/traders.json — one profile per trader.
            [JsonProperty("TraderGodMode (trader immune to all damage)")] public bool TraderGodMode = true;
            [JsonProperty("AdminPermission")] public string AdminPermission = "rustquests.admin";
            [JsonProperty("DialogCloseRangeMeters (dialog auto-closes when the player wanders this far from the trader; 0 disables)")]
            public float DialogCloseRange = 8f;
            [JsonProperty("JungleSampleThreshold (map samples that must land in jungle biome before the jungle quest tree is chosen)")]
            public int JungleSampleThreshold = 8;
            [JsonProperty("AutoPlaceOnWipe (after a map wipe, place each trader's shop at a new random valid spot)")]
            public bool AutoPlaceOnWipe = true;
            [JsonProperty("ProtectWorldObjects (shop building, note boxes and stashes are indestructible)")]
            public bool ProtectWorldObjects = true;
            [JsonProperty("PasteHeightOffset (vertical nudge for the shop paste)")]
            public float PasteHeightOffset = 0f;
            [JsonProperty("ShopLightsOn (switch the shop's lights/generator on when it's placed)")]
            public bool ShopLightsOn = true;
            [JsonProperty("ShopMapMarker (show each trader's shop on the map as a named shop icon)")]
            public bool ShopMapMarker = true;
            [JsonProperty("ShopBiomeStrict (never place a shop outside its trader's biome - if no spot fits, retry rather than settle)")]
            public bool ShopBiomeStrict = true;
            [JsonProperty("ShopClearTrees (fell trees standing in the shop's footprint; jungle is too dense to place a caboose otherwise)")]
            public bool ShopClearTrees = true;
            [JsonProperty("ShopSearchAttempts (random positions tried per placement; the biome test is cheap so a big number is fine)")]
            public int ShopSearchAttempts = 4000;
            [JsonProperty("ShopPlaceRetryMinutes (when strict placement finds nothing, try again this often)")]
            public float ShopPlaceRetryMinutes = 5f;
            [JsonProperty("ShopFlatnessMetres (max ground height spread across the shop's footprint; raise it for lumpy biomes like jungle)")]
            public float ShopFlatness = 1.2f;
            [JsonProperty("ShopClearHeightMetres (headroom that must be free of rock/cliff above the footprint)")]
            public float ShopClearHeightCfg = 5f;
            [JsonProperty("ShopDoorClearanceMetres (extra clear radius around the shop; 0 disables)")]
            public float ShopDoorClearance = 2f;
            [JsonProperty("WelcomeNote (hand every player the 'welcome' note from notes.json once per wipe, the first time they wake up on the island)")]
            public bool WelcomeNote = true;
            [JsonProperty("ShopNoBuildRadiusMetres (nobody may build within this radius of a shop - stops a player walling the trader in and denying the quest line; other plugins can query it too; 0 disables)")]
            public float ShopNoBuildRadius = 25f;
            [JsonProperty("SeasonCarryOverMaxWipes (a map wipe keeps the rolled story AND everyone's quest progress until some player has opened the vault (claimed grand_finale) - shops, stashes, notes and monument bindings are still rebuilt for the new map; this caps how many consecutive wipes one unfinished story may span before it resets anyway; 0 = every wipe resets, the pre-2.20 behaviour)")]
            public int SeasonCarryOverMaxWipes = 2;
            // --- SLM trader chat (v1.9) — ports FakeFriends' Phase 7 brain. ---
            [JsonProperty("SLMEndpoint (OpenAI-compatible chat endpoint; local Ollama: http://localhost:11434, OpenRouter: https://openrouter.ai/api - EMPTY disables every SLM feature, scripted dialog everywhere)")]
            public string SLMEndpoint = "";
            [JsonProperty("SLMModel (model the endpoint expects; local Ollama: keep IDENTICAL to FakeFriends' SLMModel so it stays VRAM-resident, OpenRouter: google/gemma-3-12b-it)")]
            public string SLMModel = "llama3.2:3b";
            [JsonProperty("SLMApiKey (Bearer token for hosted endpoints like OpenRouter; EMPTY sends no Authorization header - local Ollama needs none)")]
            public string SLMApiKey = "";
            [JsonProperty("SLMTimeoutSeconds (per-call cap; must exceed the cold model load or the first call after an idle day always fails - gemma3:12b measured 15.9s off disk)")]
            public float SLMTimeoutSeconds = 25f; // 25: an 8-12B model's cold load can exceed the old 15 (same bump as FakeFriends)
            [JsonProperty("SLMMaxTokens (reply length cap sent to the model)")]
            public int SLMMaxTokens = 80;
            [JsonProperty("SLMHourlyBudget (global SLM calls per hour, shared across all traders; 0 = unlimited)")]
            public int SLMHourlyBudget = 60;
            [JsonProperty("SLMSessionMinutes (a chat session with a trader runs this long, then she gets back to work)")]
            public float SLMSessionMinutes = 5f;
            [JsonProperty("SLMSessionCooldownMinutes (per player+trader wait between chat sessions)")]
            public float SLMSessionCooldownMinutes = 60f;
            [JsonProperty("SLMChatPanel (chat in the dialog CUI with a text box; false = she listens to game chat near her booth instead)")]
            public bool SLMChatPanel = true;
            // --- Resistance pamphlets (v1.10) — the island prints back. ---
            [JsonProperty("PamphletLootChance (chance 0-1 that a spawning crate also carries a resistance pamphlet; needs JPGs in oxide/data/RustQuests/pamphlets; 0 disables)")]
            public float PamphletLootChance = 0.05f;
            // --- Tech tree lock (v2.17) — locked benches teach nothing for scrap. ---
            [JsonProperty("TechTreeLockWorkbench1 (block scrap unlocks in the level 1 workbench tech tree - its blueprints must be researched at a research table or won by experiment instead)")]
            public bool TechTreeLockWb1 = false;
            [JsonProperty("TechTreeLockWorkbench2 (same lock for the level 2 workbench tech tree)")]
            public bool TechTreeLockWb2 = true;
            [JsonProperty("TechTreeLockWorkbench3 (same lock for the level 3 workbench tech tree)")]
            public bool TechTreeLockWb3 = true;
            [JsonProperty("TechTreeLockEngineering (same lock for the engineering workbench tech tree)")]
            public bool TechTreeLockEng = true;
            // --- Workbench quest gates (v2.18) — benches are earned from the traders. ---
            [JsonProperty("WorkbenchGateWorkbench1 (a player may craft or place a level 1 workbench only after finishing this trader's chain - sonia/rebecca/alexa/olivia; EMPTY = ungated)")]
            public string WorkbenchGateWb1 = "";
            [JsonProperty("WorkbenchGateWorkbench2 (same gate for the level 2 workbench)")]
            public string WorkbenchGateWb2 = "alexa";
            [JsonProperty("WorkbenchGateWorkbench3 (same gate for the level 3 workbench)")]
            public string WorkbenchGateWb3 = "olivia";
            [JsonProperty("WorkbenchGateEngineering (same gate for the engineering workbench)")]
            public string WorkbenchGateEng = "rebecca";
        }

        protected override void LoadDefaultConfig()
        {
            _config = new ConfigData();
            PrintWarning("Created new default configuration.");
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null) throw new Exception("config file deserialized to null");
                SaveConfig(); // re-serialize only when the file parsed (adds new keys)
            }
            catch (Exception e)
            {
                // Run on defaults but do NOT overwrite the file — the user's
                // customizations survive for them to fix the typo.
                PrintWarning($"Config invalid ({e.Message}) — running on defaults; file left untouched for repair.");
                _config = new ConfigData();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Admin.NoPermission"] = "You don't have permission to use this command.",
                ["Journal.Title"] = "Quest Journal",
                ["Journal.Empty"] = "Your journal is empty. Rumor has it a trader set up shop somewhere on the island...",
                ["Journal.Ready"] = "READY — return to {0}",
                ["Journal.Close"] = "Close",
                ["Journal.TabChains"] = "The Work",
                ["Journal.TabStory"] = "The Story",
                ["Journal.Stranger"] = "A stranger",
                ["Journal.StrangerHint"] = "Someone must vouch for you before she'll give you her name.",
                ["Journal.ChainLine"] = "{0} of {1} jobs done",
                ["Journal.ShopAt"] = "her place is out around {0}",
                ["Journal.WaitingJob"] = "Next job waiting at her counter: <b>{0}</b>",
                ["Journal.ChainClear"] = "Her list is clear.",
                ["Journal.VaultWaiting"] = "One thing remains — and it stays locked until every trader has paid you out.",
                ["Journal.NoStory"] = "No history with her yet — take a job first.",
                ["Journal.CompletedSep"] = "<color=#8a8578>— and when it was done —</color>",
                ["Journal.PageOf"] = "{0} / {1}",
                ["Journal.BtnPrev"] = "< Prev",
                ["Journal.BtnNext"] = "Next >",
                ["Dialog.Greeting"] = "Well now, a visitor. Don't get many out here.\n\nName's {0}. I hunt, I trade, and I pay well for people who can do the same.",
                // Generic fallback only — every shipped trader authors her own
                // LockedGreetingText in traders.json.
                ["Dialog.LockedGreeting"] = "She looks you over and doesn't offer a name.\n\nI don't know you, and nobody's said a word to me about you. That's not a quarrel, it's just how it is out here.\n\nThe machines will take your scrap the same as anyone's — play them, warm up, then be on your way. Work is for people somebody has vouched for.",
                ["Dialog.Stranger"] = "A stranger",
                ["Trader.Unlocked"] = "{0} is expecting you now — her place is out around {1}.",
                ["Dialog.BtnQuests"] = "Any work for me?",
                ["Dialog.BtnLeave"] = "I'll be going.",
                ["Dialog.BtnAccept"] = "I'll take that job",
                ["Dialog.BtnAcceptCost"] = "Deal ({0} scrap)",
                ["Dialog.BtnClaim"] = "Claim reward",
                ["Grudge.Barred"] = "She looks at you once and goes back to her work.\n\nWe're done, you and I. The counter's closed to you, and so are the machines.\n\nGo on.",
                ["Grudge.Grieving"] = "She looks through you, not at you.\n\nI've said everything I'll ever say to you.",
                ["Grudge.Machines"] = "{0} waves you off without looking up. Her machines don't take your scrap any more.",
                ["Dialog.BtnBack"] = "Back",
                ["Dialog.NoScrap"] = "Come back when you've got {0} scrap. This blade doesn't leave my belt for promises.",
                ["Dialog.ChainDone"] = "Nothing left to send you after, hunter — you've cleared my list and dug up my ghosts.\n\nPull up a stool. Machines are warm, the lights are on, and the drinks are on the house.",
                ["Dialog.Progress"] = "Not done yet? The prey won't hunt itself.\n\n{0}",
                ["Dialog.ReadyBody"] = "Now THAT is what I call hunting.\n\n{0}",
                ["Quest.Accepted"] = "Quest accepted: {0}",
                ["Quest.Granted"] = "Received: {0}",
                ["Quest.Rewarded"] = "Reward: {0}",
                ["Quest.KillProgress"] = "{0} down. ({1}/{2}) — {3}",
                ["Quest.Ready"] = "Objective complete — return to {0} to claim your reward.",
                ["Quest.ObjectiveLine"] = "{0} ({1}/{2})",
                ["Stash.Stocked"] = "You pull the crew's share out of the dirt — it's yours.",
                // --- The Unpaid (v2 Phase B) ---
                ["Unpaid.Claimed"] = "Payment was under the box, like the sheet said. Nobody around to thank.",
                ["Journal.UnpaidLine"] = "{0} sheet(s) settled. They find you; you don't find them.",
                ["Drop.Taken"] = "The box takes it. Whoever empties it won't say thanks.",
                ["Plant.Credited"] = "One more monument reading the truth. ({0}/{1})",
                ["Build.Blocked"] = "This is {0}'s ground — you can't build within {1}m of her shop.",
                // Deployables are judged from where the player stands plus placement
                // reach, so the effective ring is wider than ShopNoBuildRadius — this
                // line deliberately quotes no number, unlike Build.Blocked.
                ["Deploy.Blocked"] = "This is {0}'s ground — you can't place that near her shop.",
                ["TechTree.Locked"] = "This bench won't teach for scrap. Find the thing itself and put it through a research table.",
                ["Workbench.Gated"] = "You don't have the standing for that bench yet — finish {0}'s list first.",
                ["TechTree.LockedUntil"] = "This bench won't teach you for scrap — not until you've cleared {0}'s list.",
                ["Welcome.Chat"] ="Someone left a note in your pack — read it. Type <color=#c9a86a>/quest</color> at any time to open your journal.",
                ["Welcome.Dropped"] = "Your pack was full, so the note is at your feet.",
                // --- SLM trader chat (v1.9) ---
                ["Chat.BtnChat"] = "Chat",
                ["Chat.Opener"] = "{0} sets down what she's doing and gives you her attention.",
                ["Chat.Hint"] = "Type below — Enter sends. She'll hear you for {0} minutes.",
                ["Chat.Listening"] = "{0} is listening. Say your piece in chat — she'll hear you near her shop for the next {1} minutes.",
                ["Chat.Busy"] = "I've got work stacked to the ceiling. Come back and jaw at me later.",
                ["Chat.Fallback"] = "She glances up, grunts something noncommittal, and keeps working.",
                ["Chat.SessionOver"] = "That's enough jaw for now — I've got work to do. Come back later.",
                ["Chat.Thinking"] = "…",
            }, this);
        }

        private string L(string key, BasePlayer player, params object[] args)
        {
            var msg = lang.GetMessage(key, this, player?.UserIDString);
            return args.Length > 0 ? string.Format(msg, args) : msg;
        }

        // Console + own file (oxide/logs/RustQuests/) — survives a wedged main
        // Oxide log writer, doubles as the dev audit trail (FakeFriends pattern).
        private void DLog(string msg)
        {
            Puts(msg);
            LogToFile("log", $"[{DateTime.Now:HH:mm:ss}] {msg}", this);
        }

        #endregion

        #region Data — world state

        private const string DataRoot = "RustQuests";

        private StateData _state;
        private bool _stateDirty;

        private class StateData
        {
            public string WipeId;              // "<seed>-<size>-<counter>" at the SEASON's first wipe — what progress files are validated against (decision 0006: a season may span several map wipes)
            public int WipeCounter;            // bumped on every map wipe/reset — the island's reset count ({wipes}); a carried season keeps its WipeId while this ticks
            public string MapKey;              // "<seed>-<size>" of the map the state was last booted on — the map-change detector (Phase S; null in pre-2.20 state = adopt the current map)
            public int CarriedWipes;           // consecutive map wipes this season has survived (Phase S); zeroed by any real reset
            public string QuestTree;           // "jungle" | "temperate", detected per MAP (re-detected on a carried wipe)
            // Data-file schema stamp — carried ACROSS wipes (like WipeCounter),
            // drives one-time migrations (v2: the Arc stamp on quests.json).
            public int DataVersion;
            // The rolled story for this wipe (decision 0004). Null = not yet
            // generated; wiped with everything else so each wipe rolls fresh.
            public WipeBible Bible;
            // Cross-wipe continuity — survives resets like WipeCounter does.
            public CrossWipe Memory = new CrossWipe();
            // Per-trader world placement, keyed by trader profile key.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, ShopState> Shops = new Dictionary<string, ShopState>();
            public List<NoteSpawn> Notes = new List<NoteSpawn>();
            public List<StashSpawn> Stashes = new List<StashSpawn>();
            // Dead-drop boxes (v2 Phase B) — one per active `drop` tag, placed
            // near the bible's bound monument; deposits credit the objective.
            public List<DropSpawn> Drops = new List<DropSpawn>();
            // Who already got the welcome note this wipe. Lives here rather than
            // in player progress so a player who never talks to a trader still
            // costs us no progress file — and a wipe clears the ledger for free.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ulong> Welcomed = new List<ulong>();
            // Every quest id ANY player has claimed this wipe (v2 Phase F) —
            // what beat RequiresQuest gates check. Fed at claim time, and
            // GetProgress folds each loaded record in, which is also the
            // catch-up for progress earned before this ledger existed.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> WipeCompleted = new List<string>();
        }

        private class ShopState
        {
            public bool ShopPlaced;
            public float ShopX, ShopY, ShopZ, ShopYawDeg;
            // Vertical lift the paste applied (footprint-fit + trim). Trader
            // offsets are stored in blueprint space: worldY = ShopY + this + offsetY.
            public float ShopHeightApplied;
            public bool TraderPlaced;
            public float TraderX, TraderY, TraderZ, TraderYaw;

            [JsonIgnore] public NPCTalking Trader;
            [JsonIgnore] public BaseEntity ShopEnt;
            [JsonIgnore] public VendingMachineMapMarker Marker;

            [JsonIgnore] public Vector3 ShopPos => new Vector3(ShopX, ShopY, ShopZ);
            [JsonIgnore] public Vector3 TraderPos => new Vector3(TraderX, TraderY, TraderZ);
        }

        // Dead drop: an unlocked quest box the player leaves items IN — the
        // stash flow inverted. No code, no loot table; the fiction says the
        // payment was already buried under it (rewards auto-claim, Phase B).
        private class DropSpawn
        {
            public string Tag;
            public float X, Y, Z, YawDeg;
            [JsonIgnore] public StorageContainer Box;
        }

        // Placed lore note: box + note item respawn fresh each boot (EnableSaving
        // off), so taken notes replenish on restart. Content comes from
        // notes.json by tag.
        private class NoteSpawn
        {
            public string Tag;
            public float X, Y, Z, YawDeg;
            [JsonIgnore] public StorageContainer Box;
        }

        // Codelocked stash: one shared hiding place, but the loot (stashes.json
        // by tag) is granted PER PLAYER — each player with the quest active who
        // enters the code gets their own copy stocked, so a second finisher
        // doesn't find an empty box. ClaimedBy is the once-each ledger.
        private class StashSpawn
        {
            public string Tag;
            public string Code;
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ulong> ClaimedBy = new List<ulong>();
            public float X, Y, Z, YawDeg;
            [JsonIgnore] public StorageContainer Box;
            [JsonIgnore] public CodeLock Lock;
        }

        // The wipe bible: the small rolled record that IS this wipe's story
        // (decision 0004). Everything heavy stays authored in the data files;
        // the generator only picks, binds and schedules — it never writes
        // prose. Consumers read the roll through ArcActive / Effective* /
        // ExpandTokens, so a restart replays the same story from state.
        private class WipeBible
        {
            public int Version = 1;            // bible schema, not plugin version
            public string TemplateId;
            public int Seed;                   // stable hash of (world seed, size, wipe counter)
            public string WipeStartUtc;        // anchor for real-day beat scheduling (Phase F)
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> ActiveArcs = new List<string>();
            // Trader keys in this wipe's vouch order; overrides profile
            // RequiresTrader/UnlockQuest in memory via the Effective* helpers.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> ChainOrder = new List<string>();
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> Roles = new Dictionary<string, string>();       // traderKey -> role id
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> Secrets = new Dictionary<string, string>();     // secretId -> rolled value
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> VariantPicks = new Dictionary<string, string>(); // slotId -> arc tag (audit; ActiveArcs is what filters)
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, TurninRoll> TurninRolls = new Dictionary<string, TurninRoll>(); // "<questId>:<objIdx>" (Phase E)
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, MonumentBinding> Monuments = new Dictionary<string, MonumentBinding>(); // bindingKey -> instance
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<BeatState> Beats = new List<BeatState>();  // scheduled; fired by Phase F's timer
            public ChoiceState Choice;                             // Phase G
            // This wipe's claimed FIELD NOTES number (v2 Phase F) — drawn from
            // the cross-wipe ledger so no number ever repeats. 0 = pre-F bible;
            // ApplyBible adopts one.
            public int FieldNoteNumber;
        }

        private class TurninRoll { public string Item; public int Count; }

        private class MonumentBinding
        {
            public string Prefab;              // matched prefab-name substring
            public string DisplayName;
            public float X, Y, Z;
            [JsonIgnore] public Vector3 Pos => new Vector3(X, Y, Z);
        }

        private class BeatState
        {
            public string Id;
            public string Arc;
            public int DueRealDay;             // days after WipeStartUtc
            public string RequiresQuest;       // null = time-only gate
            public bool Fired;                 // set by FireBeat; additive-only, never cleared mid-wipe
        }

        private class ChoiceState
        {
            public string Id;
            public string FirstPick;           // the option id of whoever decided first — sets world flavor
            // Label captured AT pick time — the distill into next wipe's
            // memory must not depend on template data still being loaded
            // (FreshWipeState runs before LoadTemplates on a wipe boot).
            public string FirstPickLabel;
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, int> Counts = new Dictionary<string, int>();
        }

        // What one wipe tells the next. The Unpaid count the wipes — this is
        // how the pamphlets and dialog get to mean it.
        private class CrossWipe
        {
            public string LastTemplateId;
            public string LastChoiceOutcome;
            public string LastPressIdentity;
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<int> UsedFieldNoteNumbers = new List<int>();
        }

        private void LoadState()
        {
            try { _state = Interface.Oxide.DataFileSystem.ReadObject<StateData>($"{DataRoot}/state"); }
            catch (Exception e) { PrintWarning($"state load failed ({e.Message}) — starting fresh."); }
            if (_state == null) _state = new StateData();
            if (_state.Memory == null) _state.Memory = new CrossWipe(); // state files from v1 builds
        }

        // Fresh state for a new wipe. DataVersion and Memory carry across —
        // and the outgoing bible is distilled into Memory on its way out.
        private StateData FreshWipeState(int nextCounter)
        {
            var memory = _state.Memory ?? new CrossWipe();
            var old = _state.Bible;
            if (old != null)
            {
                memory.LastTemplateId = old.TemplateId;
                // Prefer the human-readable label captured at pick time —
                // {lastwipe.choice} renders this verbatim.
                if (old.Choice != null && old.Choice.FirstPick != null)
                    memory.LastChoiceOutcome = !string.IsNullOrEmpty(old.Choice.FirstPickLabel) ? old.Choice.FirstPickLabel : old.Choice.FirstPick;
                string press;
                if (old.Secrets != null && old.Secrets.TryGetValue("press_identity", out press))
                    memory.LastPressIdentity = press;
            }
            return new StateData { WipeCounter = nextCounter, DataVersion = _state.DataVersion, Memory = memory, MapKey = MapId() };
        }

        // --- season carry-over (Phase S, decision 0006) ---------------------
        // Does the running season survive the map wipe we just detected?
        // Carry unless: switched off, no story to carry, somebody opened the
        // vault, or the season already spent its carries.
        private bool SeasonGateClaimed() =>
            _state != null && _state.WipeCompleted != null && _state.WipeCompleted.Contains(VaultFinaleQuestId);

        private bool SeasonCarries(out string why)
        {
            if (_config.SeasonCarryOverMaxWipes <= 0) { why = "SeasonCarryOverMaxWipes is 0"; return false; }
            if (_state == null || _state.Bible == null) { why = "no story to carry"; return false; }
            if (SeasonGateClaimed()) { why = $"{VaultFinaleQuestId} claimed — the vault is open"; return false; }
            if (_state.CarriedWipes >= _config.SeasonCarryOverMaxWipes) { why = $"already carried {_state.CarriedWipes}/{_config.SeasonCarryOverMaxWipes} wipe(s)"; return false; }
            why = $"{VaultFinaleQuestId} unclaimed";
            return true;
        }

        // One line for rq.bible / rq.status: what the next wipe boot would do.
        private string SeasonStatusLine()
        {
            string why;
            var carries = SeasonCarries(out why);
            return $"season: carried {_state.CarriedWipes}/{_config.SeasonCarryOverMaxWipes} wipe(s), vault {(SeasonGateClaimed() ? "OPEN" : "unclaimed")} — next wipe boot will {(carries ? "CARRY the story" : "RESET and reroll")} ({why})";
        }

        private bool _carriedThisBoot;

        // Keep the season, lose the map: the bible (minus its monument
        // bindings — re-bound once templates load), WipeId (so every progress
        // file stays valid), WipeStartUtc (the beat clock keeps counting),
        // WipeCompleted and cross-wipe memory all stand; every physical thing
        // — shops, stashes, notes, drops, the welcome ledger — is rebuilt for
        // the new map; the quest tree is re-read from it. WipeCounter still
        // ticks: the island DID reset, the Unpaid count resets, the season is
        // the story.
        private void CarrySeason(int nextCounter)
        {
            _state.WipeCounter = nextCounter;
            _state.CarriedWipes++;
            _state.MapKey = MapId();
            _state.QuestTree = DetectQuestTree();
            _state.Shops = new Dictionary<string, ShopState>();
            _state.Notes = new List<NoteSpawn>();
            _state.Stashes = new List<StashSpawn>();
            _state.Drops = new List<DropSpawn>();
            _state.Welcomed = new List<ulong>(); // the paper went with the map — hand it again
            if (_state.Bible != null) _state.Bible.Monuments = new Dictionary<string, MonumentBinding>();
            _carriedThisBoot = true;
        }

        // Re-survey the new map for a carried bible. Arcs already active can't
        // be un-rolled mid-season, so a requirement the map can't meet binds
        // to ANY monument with a warning rather than dropping content.
        private void RebindMonumentsForCarry()
        {
            var b = _state != null ? _state.Bible : null;
            var tpl = ActiveTemplate();
            if (b == null || tpl == null) return;
            b.Monuments = new Dictionary<string, MonumentBinding>();
            BindMonuments(b, tpl, SurveyMonuments(), ChildRng(b.Seed, $"monuments:{_state.WipeCounter}"), null);
            DLog($"Season carried: {b.Monuments.Count} monument binding(s) re-bound on the new map for '{b.TemplateId}'.");
        }

        private void SaveState()
        {
            Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/state", _state);
            _stateDirty = false;
        }

        // Seed+size alone can't detect a wipe that keeps the same map, so the
        // id carries a counter we bump whenever a wipe (or a dev reset) happens.
        // Player progress is invalidated by comparing against this whole string.
        private string MapId() => $"{World.Seed}-{World.Size}";

        private string WipeIdFor(int counter) => $"{MapId()}-{counter}";

        // --- wipe-bible seeding (v2) ---------------------------------------
        // Deterministic per wipe: state loss regenerates the same story (stash
        // positions stay physically random — determinism is a recovery and
        // debug property, not a promise about coordinates).
        private int BibleSeedNow() =>
            unchecked((int)World.Seed * 397 ^ (int)World.Size * 31 ^ _state.WipeCounter * 17);

        // string.GetHashCode is not stable across runtimes — this is.
        private static int StableHash(string s)
        {
            unchecked
            {
                var h = 23;
                for (var i = 0; i < s.Length; i++) h = h * 31 + s[i];
                return h;
            }
        }

        // One child RNG per subsystem ("template", "chain", "roles", ...) so
        // adding a new roll in a later version never shifts the existing ones —
        // that's what keeps rq.bible.reroll reproducible wipe over wipe.
        private System.Random ChildRng(int bibleSeed, string subsystem) =>
            new System.Random(unchecked(bibleSeed ^ StableHash(subsystem)));

        // TerrainBiome.JUNGLE lives in Rust.World.dll; the const value (16) is
        // stable and verified against the live assembly (DIAGNOSTICS §7).
        private const int JungleBiomeMask = 16;

        private string DetectQuestTree()
        {
            var biomeMap = TerrainMeta.BiomeMap;
            if (biomeMap == null) return "temperate";
            var half = TerrainMeta.Size.x * 0.5f;
            var hits = 0;
            const int grid = 32;
            for (var ix = 0; ix < grid; ix++)
                for (var iz = 0; iz < grid; iz++)
                {
                    var pos = new Vector3(-half + TerrainMeta.Size.x * (ix + 0.5f) / grid, 0f,
                                          -half + TerrainMeta.Size.x * (iz + 0.5f) / grid);
                    if (biomeMap.GetBiome(pos, JungleBiomeMask) > 0.5f && ++hits >= _config.JungleSampleThreshold)
                        return "jungle";
                }
            return "temperate";
        }

        #endregion

        #region Data — trader profiles

        // oxide/data/RustQuests/traders.json — one profile per trader. Sonia is
        // v1; Olivia/Alexa/Rebecca get their own entries in later phases (each
        // quest def's Giver field points at a profile key).
        private class TraderProfile
        {
            public string Name = "Sonia";
            public string Prefab = "assets/prefabs/npc/bandit/shopkeepers/bandit_conversationalist.prefab";
            // ObjectCreationHandling.Replace on every list: without it Json.NET
            // APPENDS file values onto these defaults each load — doubled shop
            // orders live, same bug FakeFriends hit (their CLAUDE.md, config lists).
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Kit = new List<string>();
            public ulong FaceSeed;
            // Preferred biome for this trader's shop: jungle | temperate |
            // arctic | arid | tundra | any. BiomeFallback is tried only when the
            // preferred biome yields nothing on this map (e.g. no jungle).
            public string Biome = "any";
            public string BiomeFallback = "";
            // Tag in stashes.json for this trader's finale stash (null = her
            // chain has no buried-stash finale).
            public string FinaleStashTag;
            // Her voice. Empty falls back to the generic Lang strings — always
            // author these per trader, or everyone sounds like Sonia.
            // {0}: Greeting = her name · Progress = objective lines · Ready = the quest's CompleteText.
            public string GreetingText = "";
            public string ProgressText = "";
            public string ReadyText = "";
            // Said when her list is empty AND nothing of hers remains at all.
            public string ChainDoneText = "";
            // Said when her list is empty but a quest of hers is still blocked by
            // prerequisites — Sonia after her own finale, with the vault waiting on
            // the other three chains. Empty = fall back to ChainDoneText.
            public string ChainPendingText = "";
            // --- progression (v1.5) ---------------------------------------
            // Trader chain order: Sonia -> Rebecca -> Alexa -> Olivia. Until the
            // previous trader has vouched for the player, this one greets them
            // with LockedGreetingText and offers no work at all.
            //
            // NULL means "not configured, take the shipped value"; EMPTY STRING
            // means "deliberately open from the start" and survives every reload
            // — that distinction is what lets an operator unlock a trader
            // permanently without FillProfileBlanks re-locking her next load.
            public string RequiresTrader;
            // Quest whose claim unlocks THIS trader (normally the previous
            // trader's finale). Null/empty on a trader who needs no unlocking.
            public string UnlockQuest;
            // --- v2 chain rewiring ----------------------------------------
            // HER personal chain finale. When the wipe bible rolls a chain
            // order, the trader after her unlocks on this quest's claim —
            // whatever position she landed in. RequiresTrader/UnlockQuest
            // above become the fixed-template defaults (decision 0004).
            public string ChainFinaleQuest;
            // Her cold-shoulder line: she greets you, withholds her name, and
            // points at the slot machines. {0} is her name — deliberately unused
            // in the shipped text, but available if you want a friendlier take.
            public string LockedGreetingText = "";
            // How OTHERS describe where to find her, nameless (v2 Phase D):
            // feeds {nexthint}/{onward} so referral prose survives any rolled
            // chain order — the describing stays true whoever comes next.
            public string LocatorHint = "";
            // --- SLM chat (v1.9) ------------------------------------------
            // Who she is when she talks off-script — voice, temperament, what
            // she cares about. Fed to the SLM as part of the system prompt;
            // never shown to players directly. Empty = a flat generic trader.
            public string Persona = "";
            // --- SLM warmth (v1.11) ---------------------------------------
            // How she treats THIS player right now, one line per warmth tier:
            // [0] stranger (no jobs done), [1] warming (some jobs done),
            // [2] trusted (her chain complete), [3] family (vault open).
            // Warmth is per-character — Olivia thaws, she never gushes.
            // Missing/short list = no tier line, she stays all business.
            public List<string> WarmthTiers = new List<string>();
            // What she enjoys talking about once she's warmed up — true,
            // arc-derived material fed to the SLM at tier 1+, so small talk
            // has something real to stand on instead of invented lore.
            public string SmallTalk = "";
            // --- verdict grudges (v2 Phase I) -----------------------------
            // Her voice when she's done with a player entirely (barred: the
            // eviction — counter, chat and her machines all closed) and when
            // she serves them with no warmth left (frozen). The CAUSE lives
            // with the template's choice; the VOICE lives here.
            public string BarredText = "";
            public string FrozenText = "";
            // --- shop (Phase 3) -------------------------------------------
            // "prefab" spawns ShopPrefab as one entity (the train caboose —
            // furnished, lights on, machines working; verified live 2026-07-25);
            // "paste" uses the CopyPaste file workflow.
            public string ShopMode = "prefab";
            public string ShopPrefab = "assets/content/vehicles/trains/caboose/traincaboose.static.prefab";
            public string ShopPasteFile = "sonia_shop";
            public string ShopName = "Sonia's Supplies";
            // Trader stand-point relative to the paste origin, in capture space
            // (rotates with the paste); tune in traders.json + rq.trader.reload.
            public float TraderOffsetX, TraderOffsetY, TraderOffsetZ;
            public float TraderYawOffset;
        }

        private Dictionary<string, TraderProfile> _traders = new Dictionary<string, TraderProfile>();
        private const string DefaultTraderKey = "sonia";

        private ShopState Shop(string key)
        {
            ShopState s;
            if (!_state.Shops.TryGetValue(key, out s)) _state.Shops[key] = s = new ShopState();
            return s;
        }

        private TraderProfile Profile(string key)
        {
            TraderProfile p;
            return _traders.TryGetValue(key, out p) ? p : null;
        }

        // Trader whose booth the player is standing in (nearest within range).
        private string TraderKeyNear(Vector3 pos, float range)
        {
            string best = null;
            var bestSq = range * range;
            foreach (var kv in _state.Shops)
            {
                var t = kv.Value.Trader;
                if (t == null || t.IsDestroyed) continue;
                var d = (t.transform.position - pos).sqrMagnitude;
                if (d > bestSq) continue;
                bestSq = d;
                best = kv.Key;
            }
            return best;
        }

        // --- progression (v1.5) --------------------------------------------
        // The trader this one hands the player on to, i.e. whoever names her as
        // their RequiresTrader. Null for the last link in the chain (Olivia).
        private string NextTraderKey(string traderKey)
        {
            if (string.IsNullOrEmpty(traderKey)) return null;
            // Effective wiring: the bible's rolled chain, not the file's.
            foreach (var kv in _traders)
                if (kv.Value != null && EffectiveRequiresTrader(kv.Key) == traderKey) return kv.Key;
            return null;
        }

        // Has this player been vouched for? A trader with no RequiresTrader is
        // open to everyone (Sonia, and anything an operator sets to "").
        //
        // The UnlockQuest re-derivation is deliberate: it means the earned list
        // is a cache, not the source of truth. A progress file written before
        // v1.5, a hand-edited traders.json, or a crash between the claim and the
        // save all still resolve correctly instead of stranding the player at a
        // locked door with the prerequisite already in their Completed list.
        private bool IsTraderUnlocked(PlayerProgress prog, string traderKey)
        {
            var p = Profile(traderKey);
            if (p == null || string.IsNullOrEmpty(EffectiveRequiresTrader(traderKey))) return true;
            if (prog == null) return false;
            if (prog.UnlockedTraders.Contains(traderKey)) return true;
            var unlockQuest = EffectiveUnlockQuest(traderKey);
            if (!string.IsNullOrEmpty(unlockQuest) && prog.Completed.Contains(unlockQuest)) return true;
            // Grandfather clause: a trader you're already doing business with
            // never shuts her door on you. Without this, dropping progression
            // onto a live wipe would strand every accepted quest from a trader
            // the player reached before the rule existed — no way to claim, no
            // way to abandon. Cheap: greeting-time only, on short lists.
            foreach (var kv in prog.Active)
            {
                var aq = FindQuest(kv.Key);
                if (aq != null && aq.Giver == traderKey) return true;
            }
            for (var i = 0; i < prog.Completed.Count; i++)
            {
                var cq = FindQuest(prog.Completed[i]);
                if (cq != null && cq.Giver == traderKey) return true;
            }
            return false;
        }

        // Claiming `questId` may be what a later trader was waiting on. Announces
        // each newly opened door in chat; the referral prose itself lives in the
        // giver's ChainDone/ChainPending text.
        private void UnlockTradersFor(BasePlayer player, PlayerProgress prog, string questId)
        {
            if (string.IsNullOrEmpty(questId)) return;
            foreach (var kv in _traders)
            {
                var p = kv.Value;
                if (p == null || EffectiveUnlockQuest(kv.Key) != questId) continue;
                if (prog.UnlockedTraders.Contains(kv.Key)) continue;
                prog.UnlockedTraders.Add(kv.Key);
                MarkProgressDirty(player);
                var shop = Shop(kv.Key);
                PrintToChat(player, L("Trader.Unlocked", player, p.Name,
                    shop.ShopPlaced ? MapHelper.PositionToString(shop.ShopPos) : "somewhere out there"));
                DLog($"{player.displayName} unlocked trader '{kv.Key}' by claiming '{questId}'.");
            }
        }

        private void LoadTraders()
        {
            try { _traders = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, TraderProfile>>($"{DataRoot}/traders"); }
            catch (Exception e)
            {
                // Regenerating here would destroy the rq.shop.offset calibration
                // and face seed, so keep the file and run on an in-memory default.
                PrintError($"traders.json failed to parse ({e.Message}) — using a default profile this session, FILE LEFT UNTOUCHED for repair.");
                _traders = new Dictionary<string, TraderProfile> { [DefaultTraderKey] = new TraderProfile() };
                return;
            }
            if (_traders == null) _traders = new Dictionary<string, TraderProfile>();
            // Merge in shipped traders the file lacks, AND fill blank fields on
            // profiles it already has — a profile written by an older version is
            // missing every field added since (that's how Olivia ended up with
            // Sonia's dialog). Anything the operator has actually set is kept.
            var added = 0;
            var filled = 0;
            foreach (var kv in DefaultTraders())
            {
                TraderProfile existing;
                if (!_traders.TryGetValue(kv.Key, out existing)) { _traders[kv.Key] = kv.Value; added++; continue; }
                filled += FillProfileBlanks(existing, kv.Value);
            }
            if (added > 0 || filled > 0)
            {
                SaveTraders();
                DLog($"traders.json: added {added} profile(s), filled {filled} blank field(s).");
            }
        }

        private void SaveTraders() => Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/traders", _traders);

        // Copies only fields the file left empty/null — never overwrites a set value.
        private int FillProfileBlanks(TraderProfile target, TraderProfile def)
        {
            var n = 0;
            if (string.IsNullOrEmpty(target.GreetingText) && !string.IsNullOrEmpty(def.GreetingText)) { target.GreetingText = def.GreetingText; n++; }
            if (string.IsNullOrEmpty(target.ProgressText) && !string.IsNullOrEmpty(def.ProgressText)) { target.ProgressText = def.ProgressText; n++; }
            if (string.IsNullOrEmpty(target.ReadyText) && !string.IsNullOrEmpty(def.ReadyText)) { target.ReadyText = def.ReadyText; n++; }
            if (string.IsNullOrEmpty(target.ChainDoneText) && !string.IsNullOrEmpty(def.ChainDoneText)) { target.ChainDoneText = def.ChainDoneText; n++; }
            if (string.IsNullOrEmpty(target.ChainPendingText) && !string.IsNullOrEmpty(def.ChainPendingText)) { target.ChainPendingText = def.ChainPendingText; n++; }
            if (string.IsNullOrEmpty(target.LockedGreetingText) && !string.IsNullOrEmpty(def.LockedGreetingText)) { target.LockedGreetingText = def.LockedGreetingText; n++; }
            if (string.IsNullOrEmpty(target.LocatorHint) && !string.IsNullOrEmpty(def.LocatorHint)) { target.LocatorHint = def.LocatorHint; n++; }
            if (string.IsNullOrEmpty(target.Persona) && !string.IsNullOrEmpty(def.Persona)) { target.Persona = def.Persona; n++; }
            if ((target.WarmthTiers == null || target.WarmthTiers.Count == 0) && def.WarmthTiers != null && def.WarmthTiers.Count > 0) { target.WarmthTiers = new List<string>(def.WarmthTiers); n++; }
            if (string.IsNullOrEmpty(target.SmallTalk) && !string.IsNullOrEmpty(def.SmallTalk)) { target.SmallTalk = def.SmallTalk; n++; }
            if (string.IsNullOrEmpty(target.BarredText) && !string.IsNullOrEmpty(def.BarredText)) { target.BarredText = def.BarredText; n++; }
            if (string.IsNullOrEmpty(target.FrozenText) && !string.IsNullOrEmpty(def.FrozenText)) { target.FrozenText = def.FrozenText; n++; }
            // NULL only — an operator's deliberate "" (open from the start) stays.
            if (target.RequiresTrader == null && def.RequiresTrader != null) { target.RequiresTrader = def.RequiresTrader; n++; }
            if (target.UnlockQuest == null && def.UnlockQuest != null) { target.UnlockQuest = def.UnlockQuest; n++; }
            if (string.IsNullOrEmpty(target.ChainFinaleQuest) && !string.IsNullOrEmpty(def.ChainFinaleQuest)) { target.ChainFinaleQuest = def.ChainFinaleQuest; n++; }
            if (string.IsNullOrEmpty(target.FinaleStashTag) && !string.IsNullOrEmpty(def.FinaleStashTag)) { target.FinaleStashTag = def.FinaleStashTag; n++; }
            if (string.IsNullOrEmpty(target.Biome) && !string.IsNullOrEmpty(def.Biome)) { target.Biome = def.Biome; n++; }
            if (string.IsNullOrEmpty(target.BiomeFallback) && !string.IsNullOrEmpty(def.BiomeFallback)) { target.BiomeFallback = def.BiomeFallback; n++; }
            // "any" was the pre-1.1 placeholder for "decide at runtime" — an
            // explicit biome replaces it, otherwise she lands anywhere.
            if (target.Biome == "any" && !string.IsNullOrEmpty(def.Biome) && def.Biome != "any") { target.Biome = def.Biome; n++; }
            if (string.IsNullOrEmpty(target.ShopName) && !string.IsNullOrEmpty(def.ShopName)) { target.ShopName = def.ShopName; n++; }
            return n;
        }

        // All four traders share one calibrated face (owner, 2026-07-26). Kits
        // below are the live-tuned looks back-ported from the live server's
        // traders.json (2026-08-31): Workshop skins via shortname@skinid, the
        // balaclava skins are the hair. Skin creators credited in README.md.
        private const ulong TraderFaceSeed = 76561198714518433UL;

        // Shipped traders. Outfits/faces are starting points — tune live with
        // rq.trader.face / traders.json, and rq.shop.offset for the booth spot.
        private Dictionary<string, TraderProfile> DefaultTraders() => new Dictionary<string, TraderProfile>
        {
            [DefaultTraderKey] = new TraderProfile
            {
                Name = "Sonia",
                Biome = "jungle",                 // her arc is the jungle hunt
                BiomeFallback = "temperate",      // maps without jungle
                FinaleStashTag = FinaleStashTag,
                ShopName = "Sonia's Supplies",
                Kit = new List<string> { "shirt.tanktop@805920755", "pants@899216250", "mask.balaclava@2879706650" },
                FaceSeed = TraderFaceSeed,
                // The one door already open — every wipe starts at her counter.
                RequiresTrader = "",
                UnlockQuest = "",
                ChainFinaleQuest = "sonia_finale",
                LocatorHint = "a railcar lit up in the green",
                Persona = "A jungle hunter. Dry, confident, quietly amused by everything; short practical sentences full of hunter's imagery — tracks, wind, patience, the shot you don't take. Proud of her traps and her aim, warm underneath once someone has done real work for her. About the old payroll days and the buried vault she gives hints, never the whole story.",
                WarmthTiers = new List<string>
                {
                    "They are a stranger who has not done a single job for you yet. Friendly enough — you like fresh faces — but keep it to business and sizing them up.",
                    "They have done real work for you now. Loosen up: tease them a little, share a hunting story or an opinion, let the warmth under the dry show through.",
                    "They cleared your whole list and you genuinely like them. Be openly warm, playful, even flirty in your dry way — happy to talk tracking, the old days, or nothing at all.",
                    "The vault is open because of them. They are your favorite person on this island — greet them like it. Tease, flirt, reminisce; the business voice is long gone.",
                },
                SmallTalk = "Tracking wolves and boar, wind and patience, the shot you don't take, your handmade traps, the taste of fresh-cooked game, jungle weather, and the old payroll days when the four of you still shared one camp.",
                BarredText = "Door's behind you, and the slots don't know you any more. Neither do the tables.\n\nI don't trade with people I can't turn my back on — and after what you did at the last door, I can't. The machines are for people I'd drink with.\n\nGo on. The jungle's big.",
                FrozenText = "Counter's open. Say what you need, take it, and go.\n\nThe fire's not for sharing tonight. Or any night, now — we both know when that changed.",
                GreetingText = "Well now, a visitor. Don't get many out here.\n\nName's {0}. I hunt, I trade, and I pay well for people who can do the same.",
                ProgressText = "Not done yet? The prey won't hunt itself.\n\n{0}",
                ReadyText = "Now THAT is what I call hunting.\n\n{0}",
                // Her list is clear but the vault still needs the other three.
                ChainPendingText = "Nothing left on my list, hunter. You cleared it and dug up my ghosts besides.\n\nBut we're not done. Four of us came off that payroll, and what the four of us put in the ground together doesn't open for one name. It opens for all of them.\n\nGo find {next} — green country, out around {nextgrid}. She grows things and wires them up so they keep growing. Tell her Sonia vouched for you; she'll know what that costs me.\n\nWhen all three have paid you out, come back. Then we talk about the last door.",
                ChainDoneText = "Nothing left to send you after, hunter — you've cleared my list, dug up my ghosts, and opened the door the four of us couldn't.\n\nPull up a stool. Machines are warm, the lights are on, and the drinks are on the house.",
            },
            ["olivia"] = new TraderProfile
            {
                Name = "Olivia",
                Biome = "arctic",
                BiomeFallback = "tundra",
                FinaleStashTag = "olivia_finale",
                ShopName = "Olivia's Foundry",
                Kit = new List<string> { "shirt.tanktop@805920497", "pants@835246371", "mask.balaclava@2879706650", "jacket@3332279029" },
                FaceSeed = TraderFaceSeed,
                // Last of the four — Alexa's finale is what gets you in the door.
                RequiresTrader = "alexa",
                UnlockQuest = "alexa_finale",
                ChainFinaleQuest = "olivia_finale",
                LocatorHint = "up in the ice, running a foundry nobody asked her to run",
                LockedGreetingText = "Shut the door.\n\nThat is the only thing I will ask you twice. You are not the first to wander in off the ice and you will not be the last one I put back out on it.\n\nMachines are lit and they do not care whose scrap they take. Feed them, warm your hands, then go. Work is different. I do not put work in the hands of someone nobody will answer for.",
                Persona = "A munitions maker running an arctic foundry. Cold, precise, few words, no pleasantries; she used to run quality assurance and it shows — everything is tolerances, batches, defects and recalls. Unsentimental but scrupulously fair: good work gets paid, bad work gets named. The furnace matters more than your feelings, and the door stays shut.",
                WarmthTiers = new List<string>
                {
                    "They are a stranger with no completed work. Colder than usual: short answers, business only, and questions about yourself go unanswered.",
                    "Their work has passed inspection more than once. Thaw slightly — full sentences, the odd dry observation, maybe one question about how a job went.",
                    "They finished your entire list without a failed batch. For you that is close friendship: quiet respect, the rare dry joke, willing to talk shop and even the old days. Never gushing — warmth in your register is precision plus honesty.",
                    "The vault is open and their name is on every good batch this wipe. They are one of very few people you trust. Speak plainly and warmly in your spare way; let them see the person who keeps the furnace lit.",
                },
                SmallTalk = "Metallurgy and tolerances, why most ammunition on this island is garbage, the discipline of quality assurance, the sound a good batch makes, the cold as an honest thing, and what you refuse to build.",
                BarredText = "Your money is no longer good here.\n\nThe machines will refuse it too — slots, cards, all of it. Both statements have been tested exactly once. Do not make it twice.\n\nThe door is where you left it.",
                FrozenText = "Transactions only.\n\nYour scrap counts. Nothing else about you does. State your business or shut the door from the other side.",
                GreetingText = "Shut the door, you're letting the cold in.\n\n{0}. I run the foundry. You bring me raw material, I turn it into things that fire — that's the whole arrangement, and it's a good one.",
                ProgressText = "You're back empty-handed. The furnace doesn't care about your excuses.\n\n{0}",
                ReadyText = "Hm. You actually did it.\n\n{0}",
                // The end of the road: she sends you back to Sonia for the vault.
                ChainDoneText = "You've bought everything I sell and earned everything I gave. There's nothing left on the shelf.\n\nSo take the last thing I have, which is the truth. There were four of us, and there is one box the four of us buried together — it does not open for one name. You've settled with the other three. Go back to the railcar in the green and tell Sonia you have all four.\n\nShe's been waiting on that longer than any of us. Furnace stays lit. Come warm your hands when the island gets unfriendly.",
            },
            ["alexa"] = new TraderProfile
            {
                Name = "Alexa",
                Biome = "arid",
                BiomeFallback = "temperate",
                FinaleStashTag = "alexa_finale",
                ShopName = "Alexa's Garage",
                Kit = new List<string> { "shirt.tanktop@2643203649", "pants@1402353612", "mask.balaclava@2879464636" },
                FaceSeed = TraderFaceSeed,
                RequiresTrader = "rebecca",
                UnlockQuest = "rebecca_finale",
                ChainFinaleQuest = "alexa_finale",
                LocatorHint = "out past the dust, elbow-deep in an engine",
                LockedGreetingText = "She doesn't look up from the bench.\n\nCustomer or problem? Either way, don't touch anything on the way past.\n\nSlots are along the wall if you came to lose scrap — they're honest, which is more than I can say for most of what's on this island. Work, though. No. I don't hand jobs to people who found me by accident. Come back when somebody's sent you.",
                Persona = "A mechanic with a desert garage. Blunt, sarcastic, allergic to wasted time; loves engines, benches and anything with moving parts more than she likes most people. Grease under her nails, a wrench within reach, zero patience for people who break what she fixes. Respect is earned in parts delivered, not in talk.",
                WarmthTiers = new List<string>
                {
                    "They are a stranger who has not delivered a single part. Sarcasm up front, patience short — customer or problem, nothing in between.",
                    "They have delivered. Ease off the throttle: still blunt, but joke with them instead of at them, and let a little shop pride slip out.",
                    "Full list delivered and nothing they touched came back broken — that makes them crew. Be loud, funny, show off what's on the lift, drag them into opinions about engines. The sarcasm is affection now.",
                    "Vault's open, and they did more for this island than anyone off the old payroll. Treat them like the best wrench you never hired: warm, conspiratorial, on first-name terms with every machine in the bay.",
                },
                SmallTalk = "Engines and what people do wrong to them, gear ratios, benches and tools, desert driving, machines you miss from before the island, and the satisfaction of something that finally turns over.",
                BarredText = "Bay's closed. To you, specifically.\n\nDon't touch the slots on your way out, and don't sit down at the cards either — those take customers' scrap, and yours has a smell on it now. I fix engines. I don't fix what you did.\n\nRoll out.",
                FrozenText = "Transactions only. Parts in, parts out.\n\nYou want conversation, the desert's full of echoes. They're better company than I'm going to be.",
                GreetingText = "Mind the oil, it's everywhere.\n\n{0}. I keep things running — engines, benches, anything with moving parts. Half this island walks because nobody taught them to drive.\n\nBring me parts, I'll build you something that rolls.",
                ProgressText = "You're standing in my bay with empty hands. That's not how a garage works.\n\n{0}",
                ReadyText = "Well look at that. Let's have it.\n\n{0}",
                ChainDoneText = "Nothing left on the lift and nothing left owing. You've got wheels, a bench, and the sense to use both.\n\nOne name left, and it's the hard one. {next}, up in the ice, running a foundry nobody asked her to run. Around {nextgrid}, if the weather lets you walk it.\n\nSay Alexa sent you, then let her talk first. She never lied to me — that's more than the rest of us managed.\n\nBay's open. Break something and I'll fix it, for scrap.",
            },
            ["rebecca"] = new TraderProfile
            {
                Name = "Rebecca",
                Biome = "temperate",
                BiomeFallback = "tundra",
                FinaleStashTag = "rebecca_finale",
                ShopName = "Rebecca's Homestead",
                Kit = new List<string> { "shirt.tanktop@2548737618", "pants@829596538", "mask.balaclava@2879714377" },
                FaceSeed = TraderFaceSeed,
                RequiresTrader = "sonia",
                UnlockQuest = "sonia_finale",
                ChainFinaleQuest = "rebecca_finale",
                LocatorHint = "green country — she grows things and wires them up so they keep growing",
                LockedGreetingText = "The woman by the door doesn't step aside.\n\nYou're a long way from anywhere and I don't know you. That isn't a crime. It isn't an introduction either.\n\nThe machines behind me will take your scrap the same as anyone's — play, drink, warm up, nobody minds. Work is a different thing. Work I give to people somebody has vouched for, and nobody has said a word to me about you.",
                Persona = "A farmer and off-grid electrician on a temperate homestead. Warm, patient, motherly but nobody's fool; she talks about soil, water, wiring and weather as if they were one craft, because to her they are. Believes anything worth having is grown, fed and maintained, never taken. Gentle voice, steady hands, kettle always on.",
                WarmthTiers = new List<string>
                {
                    "They are vouched for, but still a stranger. Kind and welcoming as always, yet keep a farmer's reserve — pleasant words, no confidences.",
                    "They have put real work into your ground. Warm up properly: ask after them like you mean it, fuss a little, share what's growing.",
                    "They built the whole homestead with you — they are family now. Mother them shamelessly, offer tea and stories, and tell them plainly when you are proud of them.",
                    "The vault is open and it is their doing. They are the child this island never deserved. Open the kettle and the old stories, and let them hear about the payroll days from someone who was there.",
                },
                SmallTalk = "What's in the ground this season, water lines and drip feeds, wiring that behaves, reading the weather, tea, and the old payroll days — the people the four of you used to be.",
                BarredText = "Kettle's off.\n\nThere's nothing on my counter for you, and the machines are for guests — slots, cards, the lot. You stopped being a guest the moment you started talking. Some things you can't compost back into good soil.\n\nGo on. The garden does better without you standing in it.",
                FrozenText = "You'll get what you pay for, and a roof while you count it.\n\nThe kettle stays on the shelf. We both know why.",
                GreetingText = "Come in, mind the seedlings by the door.\n\nI'm {0}. I grow things, and I wire the things that keep them growing. Soil and circuits — it's the same job, really. Feed it, don't overload it, and it feeds you back.\n\nBring me what I ask for and I'll set you up properly.",
                ProgressText = "Nothing yet? That's alright, it all takes longer than you think.\n\n{0}",
                ReadyText = "Oh, wonderful. Let's see it.\n\n{0}",
                ChainDoneText = "That's the whole homestead handed over — beds, water, power, light. You could feed a dozen people with what you're carrying.\n\nThere's one more thing I can give you and it's a name. {next}, out where the ground goes dead and the wind takes the paint off things. Around {nextgrid}. She keeps everything on this island moving and she is rude about it.\n\nTell her I sent you. She'll pretend not to care.\n\nKettle's always on. Come tell me what you built.",
            },
        };

        private TraderProfile ActiveProfile()
        {
            _traders.TryGetValue(DefaultTraderKey, out var p);
            return p ?? new TraderProfile();
        }

        // The faction is a valid quest giver with no profile, no NPC and no
        // shop — work arrives on paper and pays out on the spot (Phase B).
        private const string UnpaidGiverKey = "unpaid";

        // The vault — the end of the story in every template (the verdicts'
        // PromptQuestId, arc four_keys.vault). Decision 0006: a season carries
        // across map wipes until ANY player has claimed this.
        private const string VaultFinaleQuestId = "grand_finale";

        private string TraderName(string key = null)
        {
            if (key == UnpaidGiverKey) return "The Unpaid";
            var p = key == null ? ActiveProfile() : Profile(key);
            return p != null ? p.Name : "the trader";
        }

        // Authored per-trader line, falling back to the shared Lang string.
        private string TraderLine(string key, string profileText, string langKey, BasePlayer player, params object[] args)
        {
            if (string.IsNullOrEmpty(profileText)) return L(langKey, player, args);
            return args.Length > 0 ? string.Format(profileText, args) : profileText;
        }

        #endregion

        #region Wipe templates + bible generator (v2)

        // One authored storyline the wipe roll can land on (decision 0004).
        // Lives in oxide/data/RustQuests/templates.json, merged by Id with the
        // same never-overwrite rule as quests. The template declares what CAN
        // vary; the bible records what DID.
        private class TemplateDef
        {
            public string Id;
            public string Name = "";
            public int Weight = 1;             // relative roll weight; 0 = authored but disabled
            public string Description = "";
            // Monuments the story needs. OnMissing: "veto" (template can't run
            // on this map) | "any_monument" (bind to whatever exists) |
            // "skip_arc:<tag>" (drop that OPTIONAL side content — validation
            // rejects skip_arc on an arc a chain slot needs).
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<MonumentReq> RequiredMonuments = new List<MonumentReq>();
            public ChainSpec Chain = new ChainSpec();
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<RoleSlot> Roles = new List<RoleSlot>();
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<QuestSlot> Slots = new List<QuestSlot>();
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, TurninPool> Pools = new Dictionary<string, TurninPool>();  // Phase E
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<BeatDef> Beats = new List<BeatDef>();                                    // Phase F
            public ChoiceDef Choice;                                                             // Phase G
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<SecretDef> Secrets = new List<SecretDef>();
            // Arcs live from the moment this template is rolled, before any
            // slot pick or beat adds more.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> BaseArcs = new List<string>();
            // SLM canon (Phase C wires these into the chat prompt): what is
            // publicly true this wipe, per-role stances, and per-role "you do
            // NOT know" negatives that keep a small model from inventing.
            public string CanonFacts = "";
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> RoleCanon = new Dictionary<string, string>();
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> UnknownFacts = new Dictionary<string, string>();
            // roleId -> noun phrase for the {role} token ("the one with ink
            // on her hands"). Display only — never gates anything.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> RolePhrases = new Dictionary<string, string>();
        }

        private class MonumentReq
        {
            public string Key;                 // binding key, referenced by {monument:<key>} and note placements
            public string Prefabs = "";        // '|'-separated any-of substrings of the monument prefab name
            public string OnMissing = "veto";
        }

        private class ChainSpec
        {
            public string Mode = "fixed";      // "fixed" | "shuffle"
            public string First = "";          // shuffle only: pin the opening trader (e.g. Sonia holds the welcome note)
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Fixed = new List<string>();  // fixed: the order; shuffle: the participating traders
        }

        private class RoleSlot
        {
            public string Role;
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Candidates = new List<string>();
            public int Count = 1;
        }

        private class QuestSlot
        {
            public string SlotId;
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Variants = new List<string>();  // arc tags; exactly one becomes active
        }

        // Reserved Variants entry (v2 Phase E): "play the authored core quest
        // as shipped". Picks no arc and shadows nothing — which is why a
        // pickless bible, an un-synced live template, or a template with no
        // slots at all always degrades to the classic story rather than a
        // broken chain. Authoring convention: list it FIRST, so a bible that
        // gains the slot mid-wipe adopts the story players already started.
        private const string ClassicVariant = "classic";

        private class TurninPool
        {
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<TurninOption> Options = new List<TurninOption>();
        }

        private class TurninOption { public string Item; public int Min = 1; public int Max = 1; }

        private class BeatDef
        {
            public string Id;
            public string Arc;
            public int RealDayMin = 3;
            public int RealDayMax = 5;
            public string RequiresQuest;
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Pamphlets = new List<string>();
        }

        private class ChoiceDef
        {
            public string Id;
            public string PromptQuestId;       // the quest at whose CLAIM the fork is offered
            // Whose text the pick recolors; empty = PromptQuestId (the shipped
            // shape: pick and ending on the same finale claim).
            public string EndingQuestId = "";
            public string Prompt = "";
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ChoiceOption> Options = new List<ChoiceOption>();
        }

        private class ChoiceOption
        {
            public string Id;
            public string Label = "";
            public string ResultText = "";
            public string EndingOfferText = "";
            public string EndingCompleteText = "";
            // World flavor when the ISLAND'S first pick lands here (G.4):
            // an arc activated additively (notes/stashes/pamphlets can hang
            // off it), and one line appended to every trader's SLM canon.
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string Arc;
            public string CanonLine = "";
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<RewardDef> BonusRewards = new List<RewardDef>();
            // What this pick costs the PICKER personally (v2 Phase I) —
            // grudges held by specific traders against anyone whose own
            // Choices record shows this option.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<GrudgeDef> Grudges = new List<GrudgeDef>();
            // What this pick sends the PICKER later (I.5) — paper and
            // parcels that arrive real days after their own claim, while
            // they're awake to trip over them. Never at the counter.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<DelayedPieceDef> Delayed = new List<DelayedPieceDef>();
            // I.6: the one pick that gets a night visit. Null on every other
            // option — the crew exists only for whoever earned them.
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public VisitDef Visit;
        }

        // Who holds it, how hard, and what it sounds like. Who supports a
        // literal trader key, "@role:<id>" (whoever rolled the role),
        // "@secret:<id>" (the secret's value WHEN it is a living trader —
        // a prose value resolves to nobody, which is how "read C. aloud"
        // scales with the roll for free), or "*" (all four).
        private class GrudgeDef
        {
            public string Who;
            public string Severity = "reserved";   // reserved | frozen | grieving | barred
            public string Line = "";               // joins her SLM prompt about this player
            public string DialogText = "";         // grieving only: the one line her counter still says
        }

        // One delayed delivery (v2 Phase I.5): due a rolled number of real
        // days after the player's OWN pick, delivered to their inventory the
        // next time they're awake past due — a note stocked from notes.json,
        // supplies, or both, telegraphed by one narration line in chat. The
        // due day is derived (bible seed + piece + player), never stored;
        // the only state is the per-player delivered ledger.
        private class DelayedPieceDef
        {
            public string Id;
            public int RealDayMin = 1;
            public int RealDayMax = 2;
            public string NoteTag;             // notes.json entry granted as a stocked note item
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<RewardDef> Items = new List<RewardDef>();
            public string ChatText = "";       // the telegraph — narration, nobody's voice
            // I.6: anchor the due days to the option's VISIT resolving rather
            // than the pick — undeliverable until the crew has actually
            // called. How "the next sheets deny it" can never arrive before
            // the thing they deny.
            public bool AfterVisit;
        }

        // The collections crew (v2 Phase I.6): one telegraphed, survivable,
        // lootable night call on the picker, real days after their pick —
        // unsigned guns in company hazmats, never named, denied in print
        // afterward. Fires the first NIGHT the picker is online, awake and
        // outside a safe zone, on or after RealDay.
        private class VisitDef
        {
            public int RealDay = 2;
            public int Count = 3;
            public int DurationMinutes = 8;    // they leave whether or not they found you
            public float DamageScale = 0.5f;   // survivable is the point
            public string ChatText = "";       // the arrival telegraph — narration
        }

        private class SecretDef
        {
            public string Id;
            // Values to roll from. "@role:<roleId>" resolves to whichever
            // trader the roles roll gave that role — how "press_identity"
            // follows the press without the template hardcoding a name.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> RollFrom = new List<string>();
            public int SlipTier = 3;           // warmth tier at which the authored slip line may join the SLM prompt (Phase C)
        }

        private List<TemplateDef> _templates = new List<TemplateDef>();

        private void LoadTemplates()
        {
            List<TemplateDef> defs = null;
            var parseFailed = false;
            try { defs = Interface.Oxide.DataFileSystem.ReadObject<List<TemplateDef>>($"{DataRoot}/templates"); }
            catch (Exception e)
            {
                parseFailed = true;
                PrintError($"templates.json failed to parse ({e.Message}) — running on built-in defaults, FILE LEFT UNTOUCHED for repair.");
            }
            if (parseFailed) { _templates = DefaultTemplates(); ValidateTemplates(); return; }
            if (defs == null || defs.Count == 0)
            {
                defs = DefaultTemplates();
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/templates", defs);
                DLog($"Wrote default wipe template(s) ({defs.Count}).");
            }
            else
            {
                var added = 0;
                foreach (var def in DefaultTemplates())
                {
                    if (defs.FindIndex(d => d != null && d.Id == def.Id) >= 0) continue;
                    defs.Add(def);
                    added++;
                }
                if (added > 0)
                {
                    Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/templates", defs);
                    DLog($"Added {added} new wipe template(s) to templates.json (existing entries untouched).");
                }
            }
            _templates = defs;
            ValidateTemplates();
        }

        private void ValidateTemplates()
        {
            var seen = new HashSet<string>();
            foreach (var t in _templates)
            {
                if (t == null || string.IsNullOrEmpty(t.Id)) { PrintWarning("Template with empty Id skipped."); continue; }
                if (!seen.Add(t.Id)) PrintWarning($"Duplicate template id '{t.Id}'.");
                if (t.Chain != null && t.Chain.Mode != "fixed" && t.Chain.Mode != "shuffle")
                    PrintWarning($"Template '{t.Id}': unknown chain mode '{t.Chain.Mode}' (want fixed|shuffle).");
                foreach (var req in t.RequiredMonuments)
                {
                    if (req == null || string.IsNullOrEmpty(req.Key)) { PrintWarning($"Template '{t.Id}': monument requirement without a Key."); continue; }
                    if (req.OnMissing == null ||
                        (req.OnMissing != "veto" && req.OnMissing != "any_monument" && !req.OnMissing.StartsWith("skip_arc:", StringComparison.Ordinal)))
                        PrintWarning($"Template '{t.Id}': monument '{req.Key}' has unknown OnMissing '{req.OnMissing}'.");
                    // skip_arc may only drop OPTIONAL content — an arc a chain
                    // slot can pick must exist on every map the template runs on.
                    if (req.OnMissing != null && req.OnMissing.StartsWith("skip_arc:", StringComparison.Ordinal))
                    {
                        var arc = req.OnMissing.Substring("skip_arc:".Length);
                        foreach (var slot in t.Slots)
                            if (slot != null && slot.Variants != null && slot.Variants.Contains(arc))
                                PrintWarning($"Template '{t.Id}': skip_arc '{arc}' is also a chain-slot variant — a missing monument would break the chain. Use veto instead.");
                    }
                }
                foreach (var bt in t.Beats)
                {
                    if (bt == null) continue;
                    if (string.IsNullOrEmpty(bt.Id) || string.IsNullOrEmpty(bt.Arc))
                        PrintWarning($"Template '{t.Id}': beat needs both Id and Arc (got '{bt.Id ?? "null"}'/'{bt.Arc ?? "null"}').");
                    if (bt.RealDayMin > bt.RealDayMax)
                        PrintWarning($"Template '{t.Id}': beat '{bt.Id}' has RealDayMin > RealDayMax.");
                }
                if (t.Choice != null && !string.IsNullOrEmpty(t.Choice.Id))
                {
                    if (string.IsNullOrEmpty(t.Choice.PromptQuestId))
                        PrintWarning($"Template '{t.Id}': choice '{t.Choice.Id}' needs a PromptQuestId.");
                    var usable = 0;
                    var optIds = new HashSet<string>();
                    foreach (var o in t.Choice.Options)
                    {
                        if (o == null || string.IsNullOrEmpty(o.Id) || string.IsNullOrEmpty(o.Label))
                        { PrintWarning($"Template '{t.Id}': choice option needs both Id and Label."); continue; }
                        if (!optIds.Add(o.Id)) PrintWarning($"Template '{t.Id}': duplicate choice option '{o.Id}'.");
                        usable++;
                        var pieceIds = new HashSet<string>();
                        foreach (var dp in o.Delayed)
                        {
                            if (dp == null || string.IsNullOrEmpty(dp.Id))
                            { PrintWarning($"Template '{t.Id}': option '{o.Id}' has a delayed piece without an Id."); continue; }
                            if (!pieceIds.Add(dp.Id)) PrintWarning($"Template '{t.Id}': option '{o.Id}' duplicate delayed piece '{dp.Id}'.");
                            if (dp.RealDayMin > dp.RealDayMax)
                                PrintWarning($"Template '{t.Id}': delayed piece '{dp.Id}' has RealDayMin > RealDayMax.");
                            if (string.IsNullOrEmpty(dp.NoteTag) && dp.Items.Count == 0)
                                PrintWarning($"Template '{t.Id}': delayed piece '{dp.Id}' delivers nothing (no NoteTag, no Items).");
                            if (dp.AfterVisit && o.Visit == null)
                                PrintWarning($"Template '{t.Id}': delayed piece '{dp.Id}' is AfterVisit but option '{o.Id}' has no Visit — it can never deliver.");
                        }
                        if (o.Visit != null && (o.Visit.Count < 1 || o.Visit.DurationMinutes < 1))
                            PrintWarning($"Template '{t.Id}': option '{o.Id}' Visit needs Count >= 1 and DurationMinutes >= 1.");
                    }
                    if (usable < 2) PrintWarning($"Template '{t.Id}': choice '{t.Choice.Id}' needs at least 2 usable options.");
                    else if (usable > 3) PrintWarning($"Template '{t.Id}': choice '{t.Choice.Id}' has {usable} options — the dialog shows the first 3.");
                }
            }
        }

        private TemplateDef FindTemplate(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (var i = 0; i < _templates.Count; i++)
                if (_templates[i] != null && _templates[i].Id == id) return _templates[i];
            return null;
        }

        // The compat floor (decision 0004.8): v1's exact story as a template.
        // Zero monument requirements — so the eligible set is never empty —
        // and nothing to remix, so a four_keys wipe IS a v1 wipe.
        private List<TemplateDef> DefaultTemplates() => new List<TemplateDef>
        {
            new TemplateDef
            {
                Id = "four_keys",
                Name = "Four Keys",
                Weight = 1,
                Description = "The classic: four women off one payroll, four chains in a fixed order, one vault that needs all four stories.",
                Chain = new ChainSpec { Mode = "fixed", Fixed = new List<string> { "sonia", "rebecca", "alexa", "olivia" } },
                BaseArcs = new List<string> { "four_keys.core", "four_keys.vault", "four_keys.unpaid" },
                // Where The Unpaid work out of this wipe. any_monument keeps
                // the always-eligible guarantee intact (only veto gates a
                // template) — a map with none of these still gets a box, just
                // somewhere stranger.
                RequiredMonuments = new List<MonumentReq>
                {
                    new MonumentReq
                    {
                        Key = "unpaid_drop",
                        Prefabs = "gas_station|supermarket|warehouse|water_treatment|trainyard|airfield|harbor|lighthouse|junkyard|sphere_tank",
                        OnMissing = "any_monument",
                    },
                },
                // Phase E retrofit: even the classic stops repeating. Each
                // slot rolls the shipped quest ("classic", FIRST by
                // convention) or a variant that shares its Id — same
                // RequiresQuest, same rewards economy, different ask and
                // story. The pool re-rolls Cordwood's furnace order per wipe.
                Slots = new List<QuestSlot>
                {
                    new QuestSlot { SlotId = "harvest", Variants = new List<string> { ClassicVariant, "four_keys.harvest.roots" } },
                    new QuestSlot { SlotId = "garage",  Variants = new List<string> { ClassicVariant, "four_keys.garage.scavenge" } },
                },
                Pools = new Dictionary<string, TurninPool>
                {
                    ["furnace_feed"] = new TurninPool
                    {
                        Options = new List<TurninOption>
                        {
                            new TurninOption { Item = "wood", Min = 2500, Max = 3500 },
                            new TurninOption { Item = "charcoal", Min = 1200, Max = 1800 },
                        },
                    },
                },
                // Phase F ride-along: days after somebody becomes A READER,
                // the watchers publish a fresh FIELD NOTES sheet near the
                // same box (the note self-places off its Monument binding).
                Beats = new List<BeatDef>
                {
                    new BeatDef { Id = "field_notes_reprint", Arc = "four_keys.unpaid.reprint", RealDayMin = 3, RealDayMax = 5, RequiresQuest = "unpaid_contact" },
                },
                // Verbatim the sentence TraderChatPrompt pins today; Phase C
                // moves the prompt onto this field (with {chain:n} tokens).
                CanonFacts = "True facts about the island: there are exactly four traders — Sonia the jungle hunter, Rebecca the temperate farmer and electrician, Alexa the desert mechanic, and Olivia the arctic munitions maker. The four of you were once on the same payroll, and together you buried one vault that only opens for someone all four of you have paid out. Scrap is the only currency. Monuments are patrolled by hostile scientists; wolves and bears roam the wilds. There are no other towns, factions, shops or named people — do not invent any.",
            },

            // ============ THE PRESS (v2 Phase D) ============
            // One of the three shuffled traders secretly runs the pamphlet
            // press. Sonia stays the chain head — the welcome note points at
            // her railcar and the vault is offered at her counter, and both
            // must stay true under any roll (this also defers the
            // operator-"" precedence question, PLAN risk 2: only she ships
            // RequiresTrader ""). The mystery lives in warmth slips, the
            // watcher note, and who changes the subject too smoothly. Her
            // own chat prompt is NEVER told she is the press (keep-by-
            // omission applies to her too) — cover stance + authored tells.
            new TemplateDef
            {
                Id = "the_press",
                Name = "The Press",
                Weight = 1,
                Description = "One of the four secretly prints the pamphlets. The chains shuffle, and the island watches hands.",
                Chain = new ChainSpec { Mode = "shuffle", First = "sonia", Fixed = new List<string> { "sonia", "rebecca", "alexa", "olivia" } },
                BaseArcs = new List<string> { "four_keys.core", "four_keys.vault", "four_keys.unpaid", "press.rumors" },
                RequiredMonuments = new List<MonumentReq>
                {
                    // BaseArcs reuse four_keys' content sets, so every binding
                    // those arcs need must be redeclared here.
                    new MonumentReq
                    {
                        Key = "unpaid_drop",
                        Prefabs = "gas_station|supermarket|warehouse|water_treatment|trainyard|airfield|harbor|lighthouse|junkyard|sphere_tank",
                        OnMissing = "any_monument",
                    },
                    new MonumentReq
                    {
                        Key = "press_rumor",
                        Prefabs = "bandit_town|compound|fishing_village|ranch|barn|gas_station|supermarket",
                        OnMissing = "any_monument",
                    },
                },
                // Assignment order matters (no repeats): press and bitter cast
                // from the three shuffled traders, the skeptic is always
                // Sonia, the sympathizer takes whoever is left — all four are
                // always cast, so every trader has an overlay in force.
                Roles = new List<RoleSlot>
                {
                    new RoleSlot { Role = "press", Candidates = new List<string> { "rebecca", "alexa", "olivia" } },
                    new RoleSlot { Role = "bitter", Candidates = new List<string> { "rebecca", "alexa", "olivia" } },
                    new RoleSlot { Role = "skeptic", Candidates = new List<string> { "sonia" } },
                    new RoleSlot { Role = "sympathizer", Candidates = new List<string> { "rebecca", "alexa", "olivia" } },
                },
                Secrets = new List<SecretDef>
                {
                    new SecretDef { Id = "press_identity", RollFrom = new List<string> { "@role:press" }, SlipTier = 2 },
                    // Is "C." of the welcome note the press too, or someone
                    // long gone? Rolled fresh each Press wipe (resistance.md
                    // reserves exactly this ambiguity).
                    new SecretDef { Id = "c_author", RollFrom = new List<string> { "@role:press", "someone_gone" }, SlipTier = 3 },
                },
                RolePhrases = new Dictionary<string, string>
                {
                    ["press"] = "the one with ink on her hands",
                    ["bitter"] = "the one the pamphlets cut deepest",
                    ["skeptic"] = "the one who wants no part of the paper war",
                    ["sympathizer"] = "the one who leaves the sheets where you'll read them",
                },
                CanonFacts =
                    "True facts about the island: there are exactly four traders — Sonia the jungle hunter, Rebecca the temperate farmer and electrician, Alexa the desert mechanic, and Olivia the arctic munitions maker. " +
                    "The vouching chain this season runs {chain:1}, then {chain:2}, then {chain:3}, then {chain:4} — work travels in that order. " +
                    "The four of you were once on the same payroll, and together you buried one vault that only opens for someone all four of you have paid out. " +
                    "Anti-Cobalt pamphlets signed THE UNPAID keep turning up in supply crates; everyone has read them, and nobody has ever seen a member. " +
                    "Scrap is the only currency. Monuments are patrolled by hostile scientists; wolves and bears roam the wilds. " +
                    "There are no other towns, factions, shops or named people — do not invent any.",
                RoleCanon = new Dictionary<string, string>
                {
                    // The press's entry is her COVER — the truth stays out of
                    // the prompt entirely.
                    ["press"] = "When the pamphlets come up, you wave them off as noise not worth your time, and you move the conversation along.",
                    ["bitter"] = "The pamphlets dig at wounds you carry from the payroll days — forty seats, two hundred people — and you speak of them shortly, if at all.",
                    ["skeptic"] = "You think the pamphlets are a dangerous game that ends with Cobalt's kind coming back to collect, and you keep your distance from the whole paper war.",
                    ["sympathizer"] = "You quietly agree with more of the pamphlets than you say out loud, and you hope whoever prints them stays ahead of the trouble they are inviting.",
                },
                UnknownFacts = new Dictionary<string, string>
                {
                    ["press"] = "You do not know who prints the pamphlets, you have never seen a member of The Unpaid, and you never speculate names about either.",
                    ["bitter"] = "You do not know who prints the pamphlets, you have never seen a member of The Unpaid, and you never speculate names about either.",
                    ["skeptic"] = "You do not know who prints the pamphlets, you have never seen a member of The Unpaid, and you never speculate names about either.",
                    ["sympathizer"] = "You do not know who prints the pamphlets, you have never seen a member of The Unpaid, and you never speculate names about either.",
                },
                // The one late-wipe fork (decision 0004.5, Phase G): at the
                // vault claim the skeptic asks the only question left. Sonia
                // fronts the vault AND is always the skeptic on Press wipes,
                // so the prompt is hers by construction. {press} appears in
                // the EXPOSE branch only — post-reveal prose, per the token's
                // own rule.
                Choice = new ChoiceDef
                {
                    Id = "press_verdict",
                    PromptQuestId = "grand_finale",
                    Prompt =
                        "Before I hand you what's in that armoury, there's a question, and I'm the one who has to ask it because the other three can't.\n\n" +
                        "You've worked every counter on this island. You've heard how each of us talks when the pamphlets come up — who waves them off a little too fast, whose hands are never quite clean of ink.\n\n" +
                        "By now I think you know who runs the press. The island's been asking since the first sheet turned up in a crate, and you're the one holding all four of our stories.\n\n" +
                        "Name her, and it's done — everyone hears it by morning. Or keep it buried with the rest of what Cobalt never paid for.\n\n" +
                        "Your call. It was always going to be somebody's.",
                    Options = new List<ChoiceOption>
                    {
                        new ChoiceOption
                        {
                            Id = "expose",
                            Label = "Name her",
                            ResultText =
                                "Say it plain, then: {press}. Ink on her hands the whole time.\n\n" +
                                "It'll be around every counter by morning — I'll carry it myself, since I'm the one who asked. Whatever comes of it comes of it.\n\n" +
                                "Now claim what you came for.",
                            EndingCompleteText =
                                "So that's what we were sitting on. Good grief.\n\n" +
                                "And now the island knows {press} ran the press. I won't pretend I think it was wise — I told you from the start the paper war only ends one way. But it was yours to decide, and you decided honest.\n\n" +
                                "Wear what's in that vault, carry it, and watch the roads for a while. Named people have friends. So do the people who name them.\n\n" +
                                "You're not a customer any more. Come and go as you please — while the going's good.",
                            CanonLine = "The one who ran the pamphlet press has been named in front of everyone — it is public knowledge now, and the island is still deciding how it feels about the one who said it.",
                            Arc = "press.exposed",
                            BonusRewards = new List<RewardDef> { new RewardDef { Item = "scrap", Amount = 300 } },
                            // You sold her name. Her counter, her chat and her
                            // machines are closed to you for the wipe.
                            Grudges = new List<GrudgeDef>
                            {
                                new GrudgeDef { Who = "@secret:press_identity", Severity = "barred", Line = "This is the person who sold your name to the island. The counter is closed to them, and you do not pretend otherwise." },
                            },
                            // The paper beats the trouble by a day (I.5): the
                            // warning lands on day 1 EXACTLY, because the
                            // collections crew (I.6) calls on the night of
                            // day 2 — the courtesy is the telegraph.
                            Delayed = new List<DelayedPieceDef>
                            {
                                new DelayedPieceDef
                                {
                                    Id = "warning",
                                    RealDayMin = 1, RealDayMax = 1,
                                    NoteTag = "unpaid_warning",
                                    ChatText = "Wedged under a stone where you slept, sometime before light: one folded sheet. No prints leading in. None leading out.",
                                },
                                // And after they've called, the sheets deny
                                // it ever happened — anchored to the visit
                                // itself, so the denial can't precede it.
                                new DelayedPieceDef
                                {
                                    Id = "denial",
                                    AfterVisit = true,
                                    RealDayMin = 1, RealDayMax = 2,
                                    NoteTag = "unpaid_denial",
                                    ChatText = "A fresh sheet by your door, weighted with the same kind of stone. This one is signed.",
                                },
                            },
                            // The collections crew (I.6): the night of day 2,
                            // as the warning promised. Unsigned, survivable,
                            // lootable — and about to be denied in print.
                            Visit = new VisitDef
                            {
                                RealDay = 2,
                                Count = 3,
                                DurationMinutes = 8,
                                DamageScale = 0.5f,
                                ChatText = "Three sets of boots out in the dark. Company hazmats, no insignia, no voices. They are not here to trade.",
                            },
                        },
                        new ChoiceOption
                        {
                            Id = "protect",
                            Label = "Keep it buried",
                            ResultText =
                                "Buried, then. You're a rarer kind than this island usually grows.\n\n" +
                                "I won't ask again, and I won't guess out loud. Whoever she is, she owes you a debt she'll never know about — which is the only kind worth holding.\n\n" +
                                "Now claim what you came for.",
                            EndingCompleteText =
                                "So that's what we were sitting on. Good grief.\n\n" +
                                "And the other thing — the name you didn't say. I sat here ready to carry it to the other three, and you handed me nothing. Good. Maybe the paper war ends quieter this way, or maybe it never ends — but it won't be us who lit the match.\n\n" +
                                "The sheets will keep turning up in crates. Read them or don't. Wear what's in the vault, and keep the one secret this island ever managed to hold.\n\n" +
                                "You're not a customer any more. Come and go as you please.",
                            CanonLine = "Somebody earned the right to name whoever prints the pamphlets, and chose to keep it buried — the sheets keep coming, and nobody says the obvious out loud.",
                            Arc = "press.protected",
                            BonusRewards = new List<RewardDef>
                            {
                                new RewardDef { Item = "scrap", Amount = 100 },
                                new RewardDef { Item = "largemedkit", Amount = 2 },
                            },
                            // BACK PAY FINDS PEOPLE LIKE YOU, made literal
                            // (I.5) — the faction's thank-you arrives days
                            // after everyone's stopped watching.
                            Delayed = new List<DelayedPieceDef>
                            {
                                new DelayedPieceDef
                                {
                                    Id = "care",
                                    RealDayMin = 1, RealDayMax = 3,
                                    NoteTag = "unpaid_care_press",
                                    Items = new List<RewardDef>
                                    {
                                        new RewardDef { Item = "bandage", Amount = 5 },
                                        new RewardDef { Item = "syringe.medical", Amount = 3 },
                                        new RewardDef { Item = "lowgradefuel", Amount = 60 },
                                    },
                                    ChatText = "A parcel where you slept: oilcloth, tied off with clean wire. Nobody heard anything. Nobody ever does.",
                                },
                            },
                        },
                    },
                },
            },

            // ============ THE MANIFEST (v2 Phase H, template 3) ============
            // The evacuation manifest surfaces: forty typed seats for two
            // hundred people (resistance.md's oldest wound). One of the three
            // shuffled traders HAD a seat and stayed — her cover is that she
            // never rated one. One name went on the list and was struck off
            // at the dock. Somebody's carbon copy reached The Unpaid. Sonia
            // stays the chain head (welcome note + vault, same constraint as
            // The Press) and plays the witness: off-manifest by trade, she
            // watched the boats go from the treeline. The full copy waits in
            // the war chest, and the wipe ends deciding what paper is for.
            new TemplateDef
            {
                Id = "the_manifest",
                Name = "The Manifest",
                Weight = 1,
                Description = "Forty seats, two hundred people. The list surfaces in pieces — and one of the four never needed to stay.",
                Chain = new ChainSpec { Mode = "shuffle", First = "sonia", Fixed = new List<string> { "sonia", "rebecca", "alexa", "olivia" } },
                BaseArcs = new List<string> { "four_keys.core", "four_keys.vault", "four_keys.unpaid", "manifest.rumors" },
                RequiredMonuments = new List<MonumentReq>
                {
                    new MonumentReq
                    {
                        Key = "unpaid_drop",
                        Prefabs = "gas_station|supermarket|warehouse|water_treatment|trainyard|airfield|harbor|lighthouse|junkyard|sphere_tank",
                        OnMissing = "any_monument",
                    },
                    // Where the boats left from — the salvaged page washes up
                    // in its shadow.
                    new MonumentReq
                    {
                        Key = "manifest_dock",
                        Prefabs = "harbor|ferry_terminal|fishing_village|lighthouse|boat",
                        OnMissing = "any_monument",
                    },
                },
                // Same remix machinery four_keys carries (Phase E) — a
                // Manifest wipe shuffles AND varies.
                Slots = new List<QuestSlot>
                {
                    new QuestSlot { SlotId = "harvest", Variants = new List<string> { ClassicVariant, "four_keys.harvest.roots" } },
                    new QuestSlot { SlotId = "garage",  Variants = new List<string> { ClassicVariant, "four_keys.garage.scavenge" } },
                },
                Pools = new Dictionary<string, TurninPool>
                {
                    ["furnace_feed"] = new TurninPool
                    {
                        Options = new List<TurninOption>
                        {
                            new TurninOption { Item = "wood", Min = 2500, Max = 3500 },
                            new TurninOption { Item = "charcoal", Min = 1200, Max = 1800 },
                        },
                    },
                },
                // Time-only beat (the other gate shape): pages keep surfacing
                // whether or not anyone is reading.
                Beats = new List<BeatDef>
                {
                    new BeatDef { Id = "manifest_pages", Arc = "manifest.pages", RealDayMin = 2, RealDayMax = 4 },
                },
                // Assignment order matters (no repeats): seated and scratched
                // cast from the three shuffled traders, the witness is always
                // Sonia, the clerk takes whoever is left.
                Roles = new List<RoleSlot>
                {
                    new RoleSlot { Role = "seated", Candidates = new List<string> { "rebecca", "alexa", "olivia" } },
                    new RoleSlot { Role = "scratched", Candidates = new List<string> { "rebecca", "alexa", "olivia" } },
                    new RoleSlot { Role = "witness", Candidates = new List<string> { "sonia" } },
                    new RoleSlot { Role = "clerk", Candidates = new List<string> { "rebecca", "alexa", "olivia" } },
                },
                Secrets = new List<SecretDef>
                {
                    new SecretDef { Id = "seated_identity", RollFrom = new List<string> { "@role:seated" }, SlipTier = 2 },
                    // Whose carbon copy reached The Unpaid — the clerk's, or
                    // someone long gone? Rolled fresh each Manifest wipe.
                    new SecretDef { Id = "manifest_leak", RollFrom = new List<string> { "@role:clerk", "someone_gone" }, SlipTier = 3 },
                },
                RolePhrases = new Dictionary<string, string>
                {
                    ["seated"] = "the one who won't do manifest math",
                    ["scratched"] = "the one with a line through her name",
                    ["witness"] = "the one who watched the boats leave",
                    ["clerk"] = "the one who types without looking at the keys",
                },
                CanonFacts =
                    "True facts about the island: there are exactly four traders — Sonia the jungle hunter, Rebecca the temperate farmer and electrician, Alexa the desert mechanic, and Olivia the arctic munitions maker. " +
                    "The vouching chain this season runs {chain:1}, then {chain:2}, then {chain:3}, then {chain:4} — work travels in that order. " +
                    "The four of you were once on the same payroll, and together you buried one vault that only opens for someone all four of you have paid out. " +
                    "Pages of Cobalt's evacuation manifest — forty typed seats for two hundred people — have been surfacing this season; everyone has seen at least one page, and nobody has seen every name. " +
                    "Anti-Cobalt pamphlets signed THE UNPAID keep turning up in supply crates; nobody has ever seen a member. " +
                    "Scrap is the only currency. Monuments are patrolled by hostile scientists; wolves and bears roam the wilds. " +
                    "There are no other towns, factions, shops or named people — do not invent any.",
                RoleCanon = new Dictionary<string, string>
                {
                    // The seated's entry is her COVER — the seat stays out of
                    // the prompt entirely (keep-by-omission, same as the press).
                    ["seated"] = "When the manifest comes up, you say you never rated a seat — ground trades never did — and you say it evenly, every time, in the same words.",
                    ["scratched"] = "Your name went ON that list and came off it at the dock, one line of ink through a typed row. You speak of the manifest in short sentences or not at all.",
                    ["witness"] = "You watched the boats leave from the treeline and counted heads on the gangway. You think typed lists are how companies kill people politely, and you say so.",
                    ["clerk"] = "You typed the manifest — rows, seats, names — because typing was your job that week. You are quick to say the order was alphabetical. A little too quick.",
                },
                UnknownFacts = new Dictionary<string, string>
                {
                    ["seated"] = "You do not know every name on the manifest, you have never seen a member of The Unpaid, and you never speculate about who leaked the pages.",
                    ["scratched"] = "You do not know every name on the manifest, you have never seen a member of The Unpaid, and you never speculate about who leaked the pages.",
                    ["witness"] = "You do not know every name on the manifest, you have never seen a member of The Unpaid, and you never speculate about who leaked the pages.",
                    ["clerk"] = "You do not know every name on the manifest, you have never seen a member of The Unpaid, and you never speculate about who leaked the pages.",
                },
                // The verdict (Phase G engine): the war chest held paper too —
                // the only complete copy left. Sonia the witness asks.
                // {secret:seated_identity} is POST-REVEAL prose: the POST
                // branch only.
                Choice = new ChoiceDef
                {
                    Id = "manifest_verdict",
                    PromptQuestId = "grand_finale",
                    Prompt =
                        "There's a folder in that vault besides the guns. We never talked about it, the four of us — it just went in the box with everything else worth burying.\n\n" +
                        "The manifest. The whole thing, all forty seats, every typed name — not the loose pages the wind's been handing out. The only complete copy left on this island.\n\n" +
                        "I watched those boats load from the treeline. Counted the gangway. I've had years to decide what I'd do with the list and I never could — so it's yours to decide instead.\n\n" +
                        "Post it where every counter can read it. Burn it. Or leave it in that box out by the grey wood — the ones who count wipes keep better files than the company ever did.\n\n" +
                        "Paper decided who left. Now you decide about the paper.",
                    Options = new List<ChoiceOption>
                    {
                        new ChoiceOption
                        {
                            Id = "post",
                            Label = "Post it at every counter",
                            ResultText =
                                "Every name, in the open. Alright.\n\n" +
                                "I'll run copies to the other three myself — and yes, that means the seat column too. {secret:seated_identity} will know her row is public before sundown. That conversation was always coming; you just picked the day.\n\n" +
                                "Now claim what you came for.",
                            EndingCompleteText =
                                "So that's what we were sitting on. Good grief.\n\n" +
                                "The list is up. Four counters, forty seats, and the island doing arithmetic out loud — including the row with {secret:seated_identity}'s name on it, seat and all. She stayed anyway. Whatever else the list says about her, it says that too.\n\n" +
                                "Wear what's in the vault. And when the shouting starts at somebody's counter, remember paper doesn't shout — people do.\n\n" +
                                "You're not a customer any more. Come and go as you please.",
                            CanonLine = "The complete evacuation manifest is posted at every trader's counter — every seat and every name is public now, and the island is doing the arithmetic out loud.",
                            Arc = "manifest.posted",
                            BonusRewards = new List<RewardDef> { new RewardDef { Item = "scrap", Amount = 250 } },
                            // Her seat, her name, her unused row — on every
                            // counter, because of you. The Unpaid approved;
                            // she didn't.
                            Grudges = new List<GrudgeDef>
                            {
                                new GrudgeDef { Who = "@role:seated", Severity = "barred", Line = "This is the person who posted the seat column at every counter — your row, your name, your unused seat, made public arithmetic." },
                            },
                        },
                        new ChoiceOption
                        {
                            Id = "burn",
                            Label = "Burn it",
                            ResultText =
                                "Ash, then. I hoped you'd say that and I'd never have admitted it.\n\n" +
                                "It goes in Olivia's furnace or mine tonight, and the wind can keep the loose pages — a list with holes in it is a rumor, not a verdict.\n\n" +
                                "Now claim what you came for.",
                            EndingCompleteText =
                                "So that's what we were sitting on. Good grief.\n\n" +
                                "The folder's ash. Two hundred people got left and no typed column gets to say which forty almost weren't — the island can carry the wound without carrying the arithmetic.\n\n" +
                                "The wind will keep handing out its loose pages, and people will keep guessing. Let them. Guesses heal over. Lists don't.\n\n" +
                                "You're not a customer any more. Come and go as you please.",
                            CanonLine = "The only complete copy of the evacuation manifest was burned unread — the loose pages still circulate, but the full list of names is gone for good.",
                            Arc = "manifest.burned",
                            BonusRewards = new List<RewardDef>
                            {
                                new RewardDef { Item = "scrap", Amount = 100 },
                                new RewardDef { Item = "lowgradefuel", Amount = 100 },
                            },
                        },
                        new ChoiceOption
                        {
                            Id = "give",
                            Label = "Leave it in the box",
                            ResultText =
                                "The grey box. Filed with the people who never stopped keeping records.\n\n" +
                                "I'll walk it out there myself, tonight, and I won't knock. Whatever they are, they earned the evidence — it's their names on most of it.\n\n" +
                                "Now claim what you came for.",
                            EndingCompleteText =
                                "So that's what we were sitting on. Good grief.\n\n" +
                                "The folder went in the box by the grey wood, and by morning something had taken it — no note, no noise, just gone the way their things go. The company kept a manifest of people. Now the people keep a manifest of the company.\n\n" +
                                "If a reckoning ever comes to this island, it will come with page numbers.\n\n" +
                                "You're not a customer any more. Come and go as you please.",
                            CanonLine = "The complete evacuation manifest was handed to The Unpaid through their drop box — the island's ledger of who got left is in the keeping of the left-behind now.",
                            Arc = "manifest.given",
                            BonusRewards = new List<RewardDef>
                            {
                                new RewardDef { Item = "scrap", Amount = 150 },
                                new RewardDef { Item = "supply.signal", Amount = 1 },
                            },
                            // The archive pays its sources (I.5).
                            Delayed = new List<DelayedPieceDef>
                            {
                                new DelayedPieceDef
                                {
                                    Id = "care",
                                    RealDayMin = 1, RealDayMax = 3,
                                    NoteTag = "unpaid_care_manifest",
                                    Items = new List<RewardDef>
                                    {
                                        new RewardDef { Item = "bandage", Amount = 5 },
                                        new RewardDef { Item = "metal.fragments", Amount = 250 },
                                        new RewardDef { Item = "syringe.medical", Amount = 3 },
                                    },
                                    ChatText = "A parcel where you slept: oilcloth, corners squared like it was packed off a checklist. Nobody heard anything.",
                                },
                            },
                        },
                    },
                },
            },

            // ============ YOURS, C. (v2 Phase H.2, template 4) ============
            // Every newcomer arrives holding the same typed note, signed C.
            // This wipe, the island finally asks who's been writing it. The
            // answer rolls THREE ways: one of the shuffled traders (the
            // keeper, whose cover is that she merely "found" a bundle of
            // C.'s letters), Sonia herself (the note that vouches for her,
            // in her own hand), or someone who stopped writing years ago.
            // Secret values are prose where they aren't trader keys — the
            // {secret:} token renders either cleanly. Nobody's SLM prompt is
            // ever told the answer, C. included (keep-by-omission).
            new TemplateDef
            {
                Id = "yours_c",
                Name = "Yours, C.",
                Weight = 1,
                Description = "Every newcomer carries the same typed welcome. This season, the island asks who signs it.",
                Chain = new ChainSpec { Mode = "shuffle", First = "sonia", Fixed = new List<string> { "sonia", "rebecca", "alexa", "olivia" } },
                BaseArcs = new List<string> { "four_keys.core", "four_keys.vault", "four_keys.unpaid", "c.rumors" },
                RequiredMonuments = new List<MonumentReq>
                {
                    new MonumentReq
                    {
                        Key = "unpaid_drop",
                        Prefabs = "gas_station|supermarket|warehouse|water_treatment|trainyard|airfield|harbor|lighthouse|junkyard|sphere_tank",
                        OnMissing = "any_monument",
                    },
                    // Where C.'s paper turns up — a light that's always been
                    // kept, if the map has one.
                    new MonumentReq
                    {
                        Key = "c_postbox",
                        Prefabs = "lighthouse|gas_station|supermarket|harbor|water_treatment",
                        OnMissing = "any_monument",
                    },
                },
                Slots = new List<QuestSlot>
                {
                    new QuestSlot { SlotId = "harvest", Variants = new List<string> { ClassicVariant, "four_keys.harvest.roots" } },
                    new QuestSlot { SlotId = "garage",  Variants = new List<string> { ClassicVariant, "four_keys.garage.scavenge" } },
                },
                Pools = new Dictionary<string, TurninPool>
                {
                    ["furnace_feed"] = new TurninPool
                    {
                        Options = new List<TurninOption>
                        {
                            new TurninOption { Item = "wood", Min = 2500, Max = 3500 },
                            new TurninOption { Item = "charcoal", Min = 1200, Max = 1800 },
                        },
                    },
                },
                // Quest-gated beat: C. notices when a newcomer actually takes
                // the note's advice (the knife job is the chain's first
                // transaction) — days later, an unsent letter turns up.
                Beats = new List<BeatDef>
                {
                    new BeatDef { Id = "c_letter", Arc = "c.letter", RealDayMin = 3, RealDayMax = 6, RequiresQuest = "sonia_knife" },
                },
                Roles = new List<RoleSlot>
                {
                    new RoleSlot { Role = "keeper", Candidates = new List<string> { "rebecca", "alexa", "olivia" } },
                    new RoleSlot { Role = "denier", Candidates = new List<string> { "rebecca", "alexa", "olivia" } },
                    new RoleSlot { Role = "named", Candidates = new List<string> { "sonia" } },
                    new RoleSlot { Role = "collector", Candidates = new List<string> { "rebecca", "alexa", "olivia" } },
                },
                Secrets = new List<SecretDef>
                {
                    // The spine. Prose value for the third answer — displayed
                    // verbatim by {secret:c_author}, so it must read as prose.
                    new SecretDef { Id = "c_author", RollFrom = new List<string> { "@role:keeper", "sonia", "someone who stopped writing years ago" }, SlipTier = 3 },
                    new SecretDef { Id = "c_reason", RollFrom = new List<string> { "an unpaid debt", "an old promise", "practice for an apology that never got sent" }, SlipTier = 2 },
                },
                RolePhrases = new Dictionary<string, string>
                {
                    ["named"] = "the one the note vouches for",
                    ["keeper"] = "the one with a tin full of letters",
                    ["denier"] = "the one who says C. is long gone",
                    ["collector"] = "the one who trades for old welcome notes",
                },
                CanonFacts =
                    "True facts about the island: there are exactly four traders — Sonia the jungle hunter, Rebecca the temperate farmer and electrician, Alexa the desert mechanic, and Olivia the arctic munitions maker. " +
                    "The vouching chain this season runs {chain:1}, then {chain:2}, then {chain:3}, then {chain:4} — work travels in that order. " +
                    "The four of you were once on the same payroll, and together you buried one vault that only opens for someone all four of you have paid out. " +
                    "Every newcomer arrives carrying the same typed welcome note, signed only C. — it has circulated for as long as anyone remembers, and nobody says out loud who writes it. " +
                    "Anti-Cobalt pamphlets signed THE UNPAID keep turning up in supply crates; nobody has ever seen a member. " +
                    "Scrap is the only currency. Monuments are patrolled by hostile scientists; wolves and bears roam the wilds. " +
                    "There are no other towns, factions, shops or named people — do not invent any.",
                RoleCanon = new Dictionary<string, string>
                {
                    ["named"] = "C.'s note names YOU as the open door — every stranger who walks up already half-trusts you because of paper you never signed. You never agreed to that, and it itches.",
                    // The keeper's entry is her COVER — whether she wrote the
                    // letters stays out of the prompt entirely.
                    ["keeper"] = "You say you found a bundle of C.'s letters once, years back, and that you keep them safe because somebody should. That is all you say, in the same words every time.",
                    ["denier"] = "You tell people C. is long gone and the welcome note is just old paper that keeps circulating — and you change the subject with the efficiency of practice.",
                    ["collector"] = "You trade newcomers small favors for their welcome notes and you compare the typing — margins, ribbon wear, the weight of the full stop after the C.",
                },
                UnknownFacts = new Dictionary<string, string>
                {
                    ["named"] = "You do not know who C. is, you have never seen C., and you never speculate names — about C. or about The Unpaid.",
                    ["keeper"] = "You do not know who C. is, you have never seen C., and you never speculate names — about C. or about The Unpaid.",
                    ["denier"] = "You do not know who C. is, you have never seen C., and you never speculate names — about C. or about The Unpaid.",
                    ["collector"] = "You do not know who C. is, you have never seen C., and you never speculate names — about C. or about The Unpaid.",
                },
                // The verdict: the war chest held one more envelope — C.'s
                // last letter, sealed, addressed to nobody. READ ALOUD reveals
                // to the island (canon line names); READ ALONE reveals to the
                // PLAYER only (the ending names, the canon stays coy — the
                // one verdict whose world-truth is quieter than its player-
                // truth); BURN answers nothing forever.
                Choice = new ChoiceDef
                {
                    Id = "c_verdict",
                    PromptQuestId = "grand_finale",
                    Prompt =
                        "There's an envelope in that vault that none of us will touch.\n\n" +
                        "Sealed, no address, and the same typewriter as the note you walked in here carrying on your first day — we all know the face of that ribbon by now. C.'s last letter, as far as anyone can tell. It went into the box the day we buried everything else we couldn't deal with.\n\n" +
                        "The other three have opinions. One says burn it, one says read it, one says nothing at all, which is also an opinion. So it comes to you — the only person all four of us have paid out, and the last person C.'s note ever recruited.\n\n" +
                        "Read it out loud and the island finally knows. Read it alone and YOU know. Or burn it sealed, and the question keeps us company forever.\n\n" +
                        "Paper started this. Paper can end it however you like.",
                    Options = new List<ChoiceOption>
                    {
                        new ChoiceOption
                        {
                            Id = "aloud",
                            Label = "Read it aloud",
                            ResultText =
                                "Out loud it is. Give me a moment — some things you only get to hear once.\n\n" +
                                "...\n\n" +
                                "Under the signature, after all these years: {secret:c_author}. Let that settle however it needs to.\n\n" +
                                "Now claim what you came for.",
                            EndingCompleteText =
                                "So that's what we were sitting on. Good grief.\n\n" +
                                "And the letter, read in the open, every word. The hand behind every welcome this island ever gave a stranger: {secret:c_author}. The other three heard it from me before sundown — whatever gets rearranged between the four of us now, it was always going to, and at least it rearranges honest.\n\n" +
                                "The notes will keep turning up in newcomers' pockets. They mean the same thing they always meant. Now they just mean it signed.\n\n" +
                                "You're not a customer any more. Come and go as you please.",
                            CanonLine = "C.'s last letter was read aloud at the vault: the island now knows that behind every typed welcome note stood {secret:c_author} — it is public, and it is still settling.",
                            Arc = "c.read",
                            BonusRewards = new List<RewardDef> { new RewardDef { Item = "scrap", Amount = 250 } },
                            // Grief, not anger — and only if C. is living
                            // (@secret: resolves to nobody on a prose roll,
                            // so a dead name read aloud wounds no one).
                            Grudges = new List<GrudgeDef>
                            {
                                new GrudgeDef
                                {
                                    Who = "@secret:c_author", Severity = "grieving",
                                    DialogText = "She looks at the counter, not at you.\n\nI've said everything I'll ever say to you. The letters said the rest, and you gave those away too.\n\nTake whatever you came for. The door works the same as it always did.",
                                    Line = "This person read your last letter aloud to the whole island. There is nothing left to say to them, and if pressed you say exactly that, once.",
                                },
                            },
                        },
                        new ChoiceOption
                        {
                            Id = "alone",
                            Label = "Read it alone",
                            ResultText =
                                "Then take it to the treeline and take your time. I won't ask. I've had years to decide I don't want to know, and I've almost convinced myself.\n\n" +
                                "Come back when it's read.\n\n" +
                                "Now claim what you came for.",
                            EndingCompleteText =
                                "So that's what we were sitting on. Good grief.\n\n" +
                                "And you read the letter where nobody could watch your face. Looking at you, I can tell the knowing weighs about what I guessed it would.\n\n" +
                                "(What it said stays folded in your pocket. The signature, for you alone: {secret:c_author}.)\n\n" +
                                "You didn't say. I didn't ask. The island keeps its question and you keep the answer, and honestly? That might be the kindest ending that envelope ever had on offer.\n\n" +
                                "You're not a customer any more. Come and go as you please.",
                            CanonLine = "C.'s last letter was opened at the vault by one person who read it alone and has said nothing since — the island still does not know who C. is, only that somebody now does.",
                            Arc = "c.kept",
                            BonusRewards = new List<RewardDef> { new RewardDef { Item = "scrap", Amount = 200 } },
                            // C. prepared for this reader (I.5): a second
                            // envelope, typed years ago for whoever opened the
                            // last one and told no one. Pre-written, so it
                            // stays TRUE under all three c_author rolls (the
                            // H.2 authoring rule) — only the pencil on the
                            // envelope is fresh, and the keeper's hand exists
                            // on every yours_c roll.
                            Delayed = new List<DelayedPieceDef>
                            {
                                new DelayedPieceDef
                                {
                                    Id = "letter",
                                    RealDayMin = 2, RealDayMax = 4,
                                    NoteTag = "c_private_letter",
                                    ChatText = "An envelope in your pack you don't remember packing. Your name on the front — pencil, and recent.",
                                },
                            },
                        },
                        new ChoiceOption
                        {
                            Id = "burn",
                            Label = "Burn it sealed",
                            ResultText =
                                "Sealed into the fire. You're certain? ...You're certain.\n\n" +
                                "Alright. Some questions are better company than their answers. C. of all people would understand leaving a letter unread — half of writing is deciding what not to send.\n\n" +
                                "Now claim what you came for.",
                            EndingCompleteText =
                                "So that's what we were sitting on. Good grief.\n\n" +
                                "And the envelope went into the stove with its seal unbroken. Twenty seconds of good yellow flame and the island's oldest question got to keep its dignity.\n\n" +
                                "The welcome notes will keep coming — or they won't, and either way every newcomer who reads one gets what we got: a kindness with no name on it. Maybe that was the whole point. Maybe that's what the letter said.\n\n" +
                                "You're not a customer any more. Come and go as you please.",
                            CanonLine = "C.'s last letter was burned sealed and unread at the vault — whoever C. is or was, the island chose to keep the question instead of the answer.",
                            Arc = "c.burned",
                            BonusRewards = new List<RewardDef>
                            {
                                new RewardDef { Item = "scrap", Amount = 100 },
                                new RewardDef { Item = "syringe.medical", Amount = 5 },
                            },
                        },
                    },
                },
            },

            // ============ THE RECALL (v2 Phase H.3, template 5) ============
            // Spent casings with Cobalt QA headstamps keep turning up — fired
            // SINCE the company left. Somebody found the old buried magazine
            // and has been quietly working it. Two firsts for the pool:
            // OLIVIA holds the fixed story-role (examiner — they're her
            // stamps), which frees SONIA to join the rolled cast; and
            // digger_identity has NO scripted reveal anywhere — the verdict
            // is about the magazine, not the name, so the mystery stays
            // deniable forever unless players deduce it socially (a secret
            // the wipe is allowed to keep). That also keeps the vault prompt
            // value-safe when the roll makes Sonia — who fronts it — the
            // digger herself.
            new TemplateDef
            {
                Id = "the_recall",
                Name = "The Recall",
                Weight = 1,
                Description = "Spent casings with company stamps, fired years after the company left. Somebody found the magazine.",
                Chain = new ChainSpec { Mode = "shuffle", First = "sonia", Fixed = new List<string> { "sonia", "rebecca", "alexa", "olivia" } },
                BaseArcs = new List<string> { "four_keys.core", "four_keys.vault", "four_keys.unpaid", "recall.rumors" },
                RequiredMonuments = new List<MonumentReq>
                {
                    new MonumentReq
                    {
                        Key = "unpaid_drop",
                        Prefabs = "gas_station|supermarket|warehouse|water_treatment|trainyard|airfield|harbor|lighthouse|junkyard|sphere_tank",
                        OnMissing = "any_monument",
                    },
                    // Where the old magazine was dug in — fenced ground the
                    // company never marked on any public map.
                    new MonumentReq
                    {
                        Key = "magazine",
                        Prefabs = "military_tunnel|airfield|trainyard|sphere_tank|junkyard|water_treatment",
                        OnMissing = "any_monument",
                    },
                },
                Slots = new List<QuestSlot>
                {
                    new QuestSlot { SlotId = "harvest", Variants = new List<string> { ClassicVariant, "four_keys.harvest.roots" } },
                    new QuestSlot { SlotId = "garage",  Variants = new List<string> { ClassicVariant, "four_keys.garage.scavenge" } },
                },
                Pools = new Dictionary<string, TurninPool>
                {
                    ["furnace_feed"] = new TurninPool
                    {
                        Options = new List<TurninOption>
                        {
                            new TurninOption { Item = "wood", Min = 2500, Max = 3500 },
                            new TurninOption { Item = "charcoal", Min = 1200, Max = 1800 },
                        },
                    },
                },
                // Gated on Cordwood — Olivia's first paid job. Once you're in
                // her materiel chain, the watchers decide you should know what
                // to look for on the ground.
                Beats = new List<BeatDef>
                {
                    new BeatDef { Id = "casings_map", Arc = "recall.map", RealDayMin = 2, RealDayMax = 4, RequiresQuest = "olivia_wood" },
                },
                Roles = new List<RoleSlot>
                {
                    new RoleSlot { Role = "digger", Candidates = new List<string> { "sonia", "rebecca", "alexa" } },
                    new RoleSlot { Role = "buyer", Candidates = new List<string> { "sonia", "rebecca", "alexa" } },
                    new RoleSlot { Role = "examiner", Candidates = new List<string> { "olivia" } },
                    new RoleSlot { Role = "spotter", Candidates = new List<string> { "sonia", "rebecca", "alexa" } },
                },
                Secrets = new List<SecretDef>
                {
                    // Deliberately never revealed by any scripted text — see
                    // the template header. Slips only.
                    new SecretDef { Id = "digger_identity", RollFrom = new List<string> { "@role:digger" }, SlipTier = 2 },
                    // What's LEFT down there — prose values, stated by the
                    // verdict endings via {secret:magazine_state}.
                    new SecretDef { Id = "magazine_state", RollFrom = new List<string> { "still better than half full", "down to the last few crates", "flooding a little worse every season" }, SlipTier = 3 },
                },
                RolePhrases = new Dictionary<string, string>
                {
                    ["digger"] = "the one whose boots carry the wrong mud",
                    ["buyer"] = "the one who paid cash and asked nothing",
                    ["examiner"] = "the one who reads headstamps like invoices",
                    ["spotter"] = "the one with pins in a map",
                },
                CanonFacts =
                    "True facts about the island: there are exactly four traders — Sonia the jungle hunter, Rebecca the temperate farmer and electrician, Alexa the desert mechanic, and Olivia the arctic munitions maker. " +
                    "The vouching chain this season runs {chain:1}, then {chain:2}, then {chain:3}, then {chain:4} — work travels in that order. " +
                    "The four of you were once on the same payroll, and together you buried one vault that only opens for someone all four of you have paid out. " +
                    "Spent rifle casings with Cobalt QA headstamps keep turning up around the monuments this season — fired SINCE the company left, which means somebody found and opened one of the old buried magazines and has been working it quietly. " +
                    "Anti-Cobalt pamphlets signed THE UNPAID keep turning up in supply crates; nobody has ever seen a member. " +
                    "Scrap is the only currency. Monuments are patrolled by hostile scientists; wolves and bears roam the wilds. " +
                    "There are no other towns, factions, shops or named people — do not invent any.",
                RoleCanon = new Dictionary<string, string>
                {
                    ["examiner"] = "Every stamped casing that turns up is your signature coming back to you — your checkpoint, your sign-off, your batch numbers. You collect them off the ground like unpaid invoices and you read them the same way, and you want the digging STOPPED.",
                    // The digger's entry is her COVER (keep-by-omission).
                    ["digger"] = "When the casings come up, you say your own stock is honest salvage, barrel to bullet, and that you can account for every crate you've ever sold. You say it in the same words every time.",
                    ["buyer"] = "You bought a crate of ammunition this season that was cheaper than honest and heavier than salvage, and you didn't ask where it came from. The price has been bothering you ever since.",
                    ["spotter"] = "You keep a map of everywhere the stamped casings turn up, one pin at a time, and the pins are making a shape you don't like. You haven't shown the map to anyone.",
                },
                UnknownFacts = new Dictionary<string, string>
                {
                    ["examiner"] = "You do not know who opened the old magazine, you have never seen a member of The Unpaid, and you never speculate names about either.",
                    ["digger"] = "You do not know who opened the old magazine, you have never seen a member of The Unpaid, and you never speculate names about either.",
                    ["buyer"] = "You do not know who opened the old magazine, you have never seen a member of The Unpaid, and you never speculate names about either.",
                    ["spotter"] = "You do not know who opened the old magazine, you have never seen a member of The Unpaid, and you never speculate names about either.",
                },
                // The verdict is about the HOLE, not the name — the war chest
                // held the magazine's original site chart, and the island's
                // vault-opener decides what happens to what's down there.
                Choice = new ChoiceDef
                {
                    Id = "recall_verdict",
                    PromptQuestId = "grand_finale",
                    Prompt =
                        "One more thing in that vault, rolled up with the rest: a site chart. Company drafting, company stamps — the old buried magazine, the one somebody's been quietly working all season. We've had its location the whole time and sat on it, because none of us could agree what the hole deserves.\n\n" +
                        "Olivia wants it emptied properly — her stamps, her recall, batch by batch on a bench. Others say collapse the mouth and let the island forget it. And there's a third opinion nobody says above a whisper: the ones who count wipes have been owed an arsenal longer than anyone.\n\n" +
                        "Whoever's been digging there will read the outcome in the morning like everyone else. That's their problem.\n\n" +
                        "You opened the last door. This one's yours too.",
                    Options = new List<ChoiceOption>
                    {
                        new ChoiceOption
                        {
                            Id = "seal",
                            Label = "Collapse the mouth",
                            ResultText =
                                "Buried it stays. I'll take powder from Olivia and do the mouth myself — one clean drop, no marker, and the grass gets its ground back.\n\n" +
                                "Whoever's been digging can spend the rest of the season wondering which morning the door stopped being there.\n\n" +
                                "Now claim what you came for.",
                            EndingCompleteText =
                                "So that's what we were sitting on. Good grief.\n\n" +
                                "And the magazine's mouth came down at dawn — I heard it from here, one soft thud like the island clearing its throat. The chart says it was {secret:magazine_state} down there. It can stay that way forever now, under forty feet of honest rock.\n\n" +
                                "The casings on the ground will run out. The stamps will stop coming back. Olivia can finally close her ledger — every batch accounted for, one way or another.\n\n" +
                                "You're not a customer any more. Come and go as you please.",
                            CanonLine = "The old buried magazine's entrance was collapsed on the vault-opener's word — whatever was left down there is under the rock for good, and the stamped casings will stop turning up as the loose stock runs dry.",
                            Arc = "recall.sealed",
                            BonusRewards = new List<RewardDef> { new RewardDef { Item = "scrap", Amount = 250 } },
                            // The digger's income just went under forty feet
                            // of rock — and slamming her door would confess.
                            // Her forced civility IS the season's last clue.
                            Grudges = new List<GrudgeDef>
                            {
                                new GrudgeDef { Who = "@role:digger", Severity = "frozen", Line = "Something you can never explain: this person's choice at the vault closed a door in the ground that was quietly feeding you. Coldness would confess — so you are perfectly, briefly civil, and nothing more." },
                            },
                        },
                        new ChoiceOption
                        {
                            Id = "decommission",
                            Label = "Give the chart to Olivia",
                            ResultText =
                                "Proper paperwork, then. She'll like that more than she'll ever say.\n\n" +
                                "She gets the chart tonight, and by week's end there'll be a bench at that hole and every batch coming up in order — counted, stripped, and SIGNED for, which is how she'd bury a body if you let her.\n\n" +
                                "Now claim what you came for.",
                            EndingCompleteText =
                                "So that's what we were sitting on. Good grief.\n\n" +
                                "And Olivia has her recall at last — the whole magazine, {secret:magazine_state} by her own count, coming up crate by crate onto a bench with her stamp waiting at the end of it. First time in years I've seen her look like a woman whose books balance.\n\n" +
                                "She sent you your cut of the first batch, by the way. Says the recall pays its couriers. Whoever was digging down there can go back to buying ammunition like everyone else.\n\n" +
                                "You're not a customer any more. Come and go as you please.",
                            CanonLine = "The old magazine's site chart went to Olivia, who is decommissioning it properly — batch by batch, stamped and signed, her recall finally being answered.",
                            Arc = "recall.decommissioned",
                            BonusRewards = new List<RewardDef>
                            {
                                new RewardDef { Item = "ammo.rifle", Amount = 200 },
                                new RewardDef { Item = "scrap", Amount = 100 },
                            },
                            Grudges = new List<GrudgeDef>
                            {
                                new GrudgeDef { Who = "@role:digger", Severity = "frozen", Line = "Something you can never explain: this person put a bench and a ledger on the hole that was quietly feeding you. Coldness would confess — so you are perfectly, briefly civil, and nothing more." },
                            },
                        },
                        new ChoiceOption
                        {
                            Id = "arm",
                            Label = "Leave the chart in the box",
                            ResultText =
                                "The grey box. An arsenal for the people the manifest math forgot.\n\n" +
                                "I'll walk it out tonight. I won't pretend my hands are steady about it — arming people nobody's ever SEEN is a strange way to sleep well. But owed is owed, and nobody on this island is owed more.\n\n" +
                                "Now claim what you came for.",
                            EndingCompleteText =
                                "So that's what we were sitting on. Good grief.\n\n" +
                                "And the chart went in the box by the grey wood, and by morning it was gone the way their things go. The magazine was {secret:magazine_state}, if the old drawing tells it true — and now it belongs to the people who count wipes and keep files and never, ever forget a debt.\n\n" +
                                "I don't know what an army of nobody looks like. I suppose we'll all find out together. Keep your head down around the monuments for a while.\n\n" +
                                "You're not a customer any more. Come and go as you please.",
                            CanonLine = "The old magazine's location was handed to The Unpaid through their drop box — the island's biggest stockpile now belongs to the counted-out, and everyone is quietly recalculating.",
                            Arc = "recall.armed",
                            BonusRewards = new List<RewardDef>
                            {
                                new RewardDef { Item = "scrap", Amount = 150 },
                                new RewardDef { Item = "supply.signal", Amount = 1 },
                            },
                            // The armorer's respect is the price of arming
                            // the faceless — Olivia is scrupulously fair, so
                            // refusing your scrap would be a defect in HER
                            // books. Freezing you is her version of shouting.
                            Grudges = new List<GrudgeDef>
                            {
                                new GrudgeDef { Who = "olivia", Severity = "frozen", Line = "This person handed the old magazine — your batches, your stamps, your unanswered recall — to people nobody has ever seen. You serve them with exact courtesy and nothing else. The recall is closed. So is something else." },
                            },
                        },
                    },
                },
            },

            // ============ THE COUNT (v2 Phase H.4, template 6) ============
            // THEY COUNT THE WIPES becomes the plot. Everyone on this island
            // half-knows time restarts here (the meta-truth both plugins
            // share); The Unpaid are the only ones who write it down — and
            // this season their tally itself surfaces. This is the template
            // that reads the island's MEMORY aloud: its canon and its beat
            // sheet quote {wipes}, {lastwipe.template} and {lastwipe.choice}
            // (all first-wipe-safe). Rebecca holds the fixed role — she ran
            // the light cycles, sixteen on, eight off; keeping time is her
            // wound — so the rolled cast is Sonia, Alexa, Olivia, and the
            // vault prompt is written role-safe because Sonia ALWAYS carries
            // one of the rolled roles here.
            new TemplateDef
            {
                Id = "the_count",
                Name = "The Count",
                Weight = 1,
                Description = "Everyone half-knows the island restarts. The ones who write it down just went public with the number.",
                Chain = new ChainSpec { Mode = "shuffle", First = "sonia", Fixed = new List<string> { "sonia", "rebecca", "alexa", "olivia" } },
                BaseArcs = new List<string> { "four_keys.core", "four_keys.vault", "four_keys.unpaid", "count.rumors" },
                RequiredMonuments = new List<MonumentReq>
                {
                    new MonumentReq
                    {
                        Key = "unpaid_drop",
                        Prefabs = "gas_station|supermarket|warehouse|water_treatment|trainyard|airfield|harbor|lighthouse|junkyard|sphere_tank",
                        OnMissing = "any_monument",
                    },
                    // A big paintable surface the marks accumulate on.
                    new MonumentReq
                    {
                        Key = "tally_wall",
                        Prefabs = "sphere_tank|lighthouse|warehouse|water_treatment|silo",
                        OnMissing = "any_monument",
                    },
                },
                Slots = new List<QuestSlot>
                {
                    new QuestSlot { SlotId = "harvest", Variants = new List<string> { ClassicVariant, "four_keys.harvest.roots" } },
                    new QuestSlot { SlotId = "garage",  Variants = new List<string> { ClassicVariant, "four_keys.garage.scavenge" } },
                },
                Pools = new Dictionary<string, TurninPool>
                {
                    ["furnace_feed"] = new TurninPool
                    {
                        Options = new List<TurninOption>
                        {
                            new TurninOption { Item = "wood", Min = 2500, Max = 3500 },
                            new TurninOption { Item = "charcoal", Min = 1200, Max = 1800 },
                        },
                    },
                },
                // Gated on Good Soil — the timekeeper's chain opening. Once
                // somebody is keeping Rebecca's kind of time (planting
                // cycles), the watchers publish the sheet that keeps theirs.
                Beats = new List<BeatDef>
                {
                    // The first beat with a real PRINT: when the sheet
                    // publishes, this JPG joins the crate rotation through the
                    // F.2 manifest mechanism (arc-gated on count.sheet).
                    new BeatDef { Id = "count_sheet", Arc = "count.sheet", RealDayMin = 2, RealDayMax = 5, RequiresQuest = "rebecca_planter", Pamphlets = new List<string> { "the-count-published.jpg" } },
                },
                Roles = new List<RoleSlot>
                {
                    new RoleSlot { Role = "tallier", Candidates = new List<string> { "sonia", "alexa", "olivia" } },
                    new RoleSlot { Role = "eraser", Candidates = new List<string> { "sonia", "alexa", "olivia" } },
                    new RoleSlot { Role = "timekeeper", Candidates = new List<string> { "rebecca" } },
                    new RoleSlot { Role = "forgetter", Candidates = new List<string> { "sonia", "alexa", "olivia" } },
                },
                Secrets = new List<SecretDef>
                {
                    new SecretDef { Id = "tallier_identity", RollFrom = new List<string> { "@role:tallier" }, SlipTier = 2 },
                    // How the REAL count relates to the published one — prose
                    // values, stated by two of the three verdict endings.
                    new SecretDef { Id = "true_count", RollFrom = new List<string> { "exactly what the pamphlets say", "three wipes higher than the pamphlets admit", "past counting — the earliest marks washed out years ago" }, SlipTier = 3 },
                },
                RolePhrases = new Dictionary<string, string>
                {
                    ["tallier"] = "the one who knows today's number",
                    ["eraser"] = "the one who scrubs the marks away",
                    ["timekeeper"] = "the one the island asks what season it is",
                    ["forgetter"] = "the one the count keeps escaping",
                },
                CanonFacts =
                    "True facts about the island: there are exactly four traders — Sonia the jungle hunter, Rebecca the temperate farmer and electrician, Alexa the desert mechanic, and Olivia the arctic munitions maker. " +
                    "The vouching chain this season runs {chain:1}, then {chain:2}, then {chain:3}, then {chain:4} — work travels in that order. " +
                    "The four of you were once on the same payroll, and together you buried one vault that only opens for someone all four of you have paid out. " +
                    "Everyone on this island half-knows that time restarts here — things reset, and memory of the stretches between is unreliable for everybody. The Unpaid are the only ones who write it down, and this season their tally went public: the sheets say this is wipe {wipes}. The previous season is remembered as '{lastwipe.template}', and it ended with {lastwipe.choice}. " +
                    "Anti-Cobalt pamphlets signed THE UNPAID keep turning up in supply crates; nobody has ever seen a member. " +
                    "Scrap is the only currency. Monuments are patrolled by hostile scientists; wolves and bears roam the wilds. " +
                    "There are no other towns, factions, shops or named people — do not invent any.",
                RoleCanon = new Dictionary<string, string>
                {
                    ["timekeeper"] = "You ran the greenhouse light cycles — sixteen on, eight off — and you never stopped keeping time for this island: frost counts, sunrise counts, planting counts. When people ask what season it is, what they mean is: ask you.",
                    // The tallier's entry is her COVER (keep-by-omission) —
                    // the prompt never says she keeps a count of her own.
                    ["tallier"] = "When the wipe-counting comes up you call it morbid superstition — grave-digging with arithmetic — and you steer the talk somewhere useful. Every time, the same words.",
                    ["eraser"] = "Where you find tally marks you scrub them out, openly and without apology. Grief needs endings, not arithmetic, and you'll say exactly that to anyone who objects.",
                    ["forgetter"] = "You genuinely lose the count. Seasons blur at their edges for you worse than for most, and some mornings arrive with too many winters in them. It frightens you more than you let on.",
                },
                UnknownFacts = new Dictionary<string, string>
                {
                    ["timekeeper"] = "You do not know what the true count of the island's restarts is, you have never seen a member of The Unpaid, and you never speculate names about who keeps their tally.",
                    ["tallier"] = "You do not know what the true count of the island's restarts is, you have never seen a member of The Unpaid, and you never speculate names about who keeps their tally.",
                    ["eraser"] = "You do not know what the true count of the island's restarts is, you have never seen a member of The Unpaid, and you never speculate names about who keeps their tally.",
                    ["forgetter"] = "You do not know what the true count of the island's restarts is, you have never seen a member of The Unpaid, and you never speculate names about who keeps their tally.",
                },
                // The verdict: the war chest held the FOUNDERS' tally — the
                // count the four of them kept privately, from before The
                // Unpaid ever went public. Written role-safe: whatever rolled
                // role Sonia carries, these are lines she can say.
                Choice = new ChoiceDef
                {
                    Id = "count_verdict",
                    PromptQuestId = "grand_finale",
                    Prompt =
                        "Bottom of the vault, under everything: a board. Tally marks, four hands' worth — we each cut our own, back when we still met to do it. The count the four of us kept before anyone printed sheets about it.\n\n" +
                        "The pamphlets say this is wipe {wipes}. Our board says what it says. I'm not going to tell you whether they agree — that's rather the point of what you're deciding.\n\n" +
                        "Post them side by side and let the island do the arithmetic. Drop our board in the grey box and let the counters have the early marks. Or burn it, and this island keeps exactly one number — the public one — from here on out.\n\n" +
                        "People think counting is about the past. It never is. Pick.",
                    Options = new List<ChoiceOption>
                    {
                        new ChoiceOption
                        {
                            Id = "reconcile",
                            Label = "Post them side by side",
                            ResultText =
                                "Both numbers, in the open, at every counter. The arithmetic can be everyone's problem — that's what arithmetic is for.\n\n" +
                                "I'll have copies up by morning. Whatever the difference says about us, it says it in daylight.\n\n" +
                                "Now claim what you came for.",
                            EndingCompleteText =
                                "So that's what we were sitting on. Good grief.\n\n" +
                                "And the two counts are up, side by side at four counters, and the island has been squinting at them all day. The verdict of the boards: the truth is {secret:true_count}. Make of that what everyone else is making of it, loudly, at my counter, since sunrise.\n\n" +
                                "Numbers don't heal anything. But they stop the guessing, and the guessing was its own kind of wound.\n\n" +
                                "You're not a customer any more. Come and go as you please.",
                            CanonLine = "The founders' private wipe tally was posted beside The Unpaid's public count at every counter — the island has compared them, and knows the truth is {secret:true_count}.",
                            Arc = "count.reconciled",
                            BonusRewards = new List<RewardDef> { new RewardDef { Item = "scrap", Amount = 250 } },
                        },
                        new ChoiceOption
                        {
                            Id = "give",
                            Label = "Give them the early marks",
                            ResultText =
                                "The grey box, then. The people who never stopped counting get the marks from before they started.\n\n" +
                                "I'll carry the board out tonight. Four hands' worth of winters, filed with the only archive on this island that anyone maintains.\n\n" +
                                "Now claim what you came for.",
                            EndingCompleteText =
                                "So that's what we were sitting on. Good grief.\n\n" +
                                "And the board went into the box by the grey wood, and by morning it was gone the way their things go. Their line runs back to the first mark now — and for what it's worth, complete at last, the truth of it is {secret:true_count}.\n\n" +
                                "Somebody on this island keeps whole what the rest of us can't hold. I used to find that unsettling. Today it feels like the only kind of wealth that survives a reset.\n\n" +
                                "You're not a customer any more. Come and go as you please.",
                            CanonLine = "The founders' private tally — the marks from before the public count began — was handed to The Unpaid through their drop box; their ledger of the island's restarts now runs back to the first mark, and the truth of it is {secret:true_count}.",
                            Arc = "count.given",
                            BonusRewards = new List<RewardDef>
                            {
                                new RewardDef { Item = "scrap", Amount = 150 },
                                new RewardDef { Item = "techparts", Amount = 10 },
                            },
                            // The file remembers who made it whole (I.5).
                            Delayed = new List<DelayedPieceDef>
                            {
                                new DelayedPieceDef
                                {
                                    Id = "care",
                                    RealDayMin = 1, RealDayMax = 3,
                                    NoteTag = "unpaid_care_count",
                                    Items = new List<RewardDef>
                                    {
                                        new RewardDef { Item = "bandage", Amount = 5 },
                                        new RewardDef { Item = "lowgradefuel", Amount = 60 },
                                        new RewardDef { Item = "scrap", Amount = 50 },
                                    },
                                    ChatText = "A parcel where you slept: oilcloth, tied off with clean wire. Nobody heard anything. Nobody ever does.",
                                },
                            },
                        },
                        new ChoiceOption
                        {
                            Id = "stop",
                            Label = "Burn the board",
                            ResultText =
                                "Into the stove. Four hands' worth of winters, gone in twenty seconds of good flame.\n\n" +
                                "One number left on this island now — the public one — and whether it's right stops mattering the moment there's nothing to check it against. Maybe that's mercy. Maybe it's just tidy.\n\n" +
                                "Now claim what you came for.",
                            EndingCompleteText =
                                "So that's what we were sitting on. Good grief.\n\n" +
                                "And the board is ash, and the island keeps exactly one count now. Whether the pamphlets have it right is nobody's checkable business any more — including ours, including mine.\n\n" +
                                "Four of us cut those marks to hold on to something. You decided the holding was the harm. I've thought about it all day, and I still don't know if you were wrong — which, for me, is practically a compliment.\n\n" +
                                "You're not a customer any more. Come and go as you please.",
                            CanonLine = "The founders' private wipe tally was burned unread against the public count — the island keeps exactly one number now, and whether it is right can never be checked again.",
                            Arc = "count.stopped",
                            BonusRewards = new List<RewardDef>
                            {
                                new RewardDef { Item = "scrap", Amount = 100 },
                                new RewardDef { Item = "lowgradefuel", Amount = 100 },
                            },
                            // Four quiet chills, no slammed doors — they cut
                            // those marks together, and you burned them.
                            Grudges = new List<GrudgeDef>
                            {
                                new GrudgeDef { Who = "*", Severity = "reserved", Line = "this person burned the founders' tally board at the vault — marks the four of you cut together, winters nobody else can vouch for now. You don't punish customers for verdicts. But something stays folded away around them." },
                            },
                            // And the other ledger balances (I.5): burning the
                            // founders' board left ONE count on the island —
                            // theirs. They didn't ask. They pay anyway, in an
                            // itemized amount nobody would ever round to.
                            Delayed = new List<DelayedPieceDef>
                            {
                                new DelayedPieceDef
                                {
                                    Id = "debt",
                                    RealDayMin = 2, RealDayMax = 4,
                                    NoteTag = "unpaid_debt",
                                    Items = new List<RewardDef> { new RewardDef { Item = "scrap", Amount = 217 } },
                                    ChatText = "A tin box set squarely where you'd trip over it: scrap counted into paper rolls, bank-neat, under one typed sheet.",
                                },
                            },
                        },
                    },
                },
            },
        };

        // --- generator -----------------------------------------------------

        private class MonumentInstance
        {
            public string Name;                // prefab path, lowercased
            public string DisplayName;
            public Vector3 Pos;
        }

        private List<MonumentInstance> SurveyMonuments()
        {
            var list = new List<MonumentInstance>();
            var monuments = TerrainMeta.Path != null ? TerrainMeta.Path.Monuments : null;
            if (monuments == null) return list;
            foreach (var m in monuments)
            {
                if (m == null) continue;
                var display = m.displayPhrase != null ? m.displayPhrase.english : null;
                list.Add(new MonumentInstance
                {
                    Name = (m.name ?? "").ToLowerInvariant(),
                    DisplayName = string.IsNullOrEmpty(display) ? ShortMonumentName(m.name) : display,
                    Pos = m.transform.position,
                });
            }
            return list;
        }

        private static string ShortMonumentName(string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath)) return "a monument";
            var slash = prefabPath.LastIndexOf('/');
            var name = slash >= 0 ? prefabPath.Substring(slash + 1) : prefabPath;
            return name.Replace(".prefab", "").Replace('_', ' ');
        }

        private List<MonumentInstance> MonumentMatches(MonumentReq req, List<MonumentInstance> survey)
        {
            var matches = new List<MonumentInstance>();
            if (req == null || string.IsNullOrEmpty(req.Prefabs)) return matches;
            var wanted = req.Prefabs.ToLowerInvariant().Split('|');
            foreach (var m in survey)
                for (var i = 0; i < wanted.Length; i++)
                {
                    var w = wanted[i].Trim();
                    if (w.Length > 0 && m.Name.Contains(w)) { matches.Add(m); break; }
                }
            return matches;
        }

        private bool TemplateEligible(TemplateDef tpl, List<MonumentInstance> survey)
        {
            if (tpl == null || string.IsNullOrEmpty(tpl.Id) || tpl.Weight <= 0) return false;
            foreach (var req in tpl.RequiredMonuments)
                if (req != null && req.OnMissing == "veto" && MonumentMatches(req, survey).Count == 0) return false;
            return true;
        }

        // Raw shipped chain, walked from profile wiring (NOT the Effective*
        // helpers — this runs while the bible is still being built).
        private List<string> ShippedChainOrder()
        {
            var order = new List<string>();
            string head = null;
            foreach (var kv in _traders)
                if (kv.Value != null && string.IsNullOrEmpty(kv.Value.RequiresTrader)) { head = kv.Key; break; }
            if (head == null && _traders.ContainsKey(DefaultTraderKey)) head = DefaultTraderKey;
            var k = head;
            while (k != null && !order.Contains(k) && order.Count < _traders.Count)
            {
                order.Add(k);
                string next = null;
                foreach (var kv in _traders)
                    if (kv.Value != null && kv.Value.RequiresTrader == k) { next = kv.Key; break; }
                k = next;
            }
            return order;
        }

        private TemplateDef PickTemplate(int seed)
        {
            var survey = SurveyMonuments();
            var pool = new List<TemplateDef>();
            foreach (var t in _templates)
                if (TemplateEligible(t, survey)) pool.Add(t);
            if (pool.Count == 0) return null;
            // Same story two wipes running is the one repeat players notice.
            if (pool.Count > 1 && _state.Memory != null && !string.IsNullOrEmpty(_state.Memory.LastTemplateId))
                for (var i = pool.Count - 1; i >= 0; i--)
                    if (pool[i].Id == _state.Memory.LastTemplateId && pool.Count > 1) pool.RemoveAt(i);
            var rng = ChildRng(seed, "template");
            var total = 0;
            foreach (var t in pool) total += Math.Max(1, t.Weight);
            var roll = rng.Next(total);
            foreach (var t in pool)
            {
                roll -= Math.Max(1, t.Weight);
                if (roll < 0) return t;
            }
            return pool[pool.Count - 1];
        }

        // Bind the template's monument requirements against a survey of the
        // running map. suppressed != null is the fresh-roll contract: a
        // skip_arc miss records the arc to drop and a veto miss leaves the key
        // unbound (eligibility already vetted it; a forced template runs
        // without). suppressed == null is the carried-season contract (Phase
        // S): nothing can be dropped any more, so any miss binds to any
        // monument and warns.
        private void BindMonuments(WipeBible bible, TemplateDef tpl, List<MonumentInstance> survey, System.Random rngMon, HashSet<string> suppressed)
        {
            foreach (var req in tpl.RequiredMonuments)
            {
                if (req == null || string.IsNullOrEmpty(req.Key)) continue;
                var matches = MonumentMatches(req, survey);
                if (matches.Count == 0)
                {
                    if (req.OnMissing == "any_monument" && survey.Count > 0) matches = survey;
                    else if (suppressed == null && survey.Count > 0)
                    {
                        PrintWarning($"Season carried onto a map without a '{req.Key}' monument for template '{tpl.Id}' (OnMissing '{req.OnMissing}') — bound to any monument instead; the content stays reachable, the fiction is a little off.");
                        matches = survey;
                    }
                    else if (suppressed != null && req.OnMissing != null && req.OnMissing.StartsWith("skip_arc:", StringComparison.Ordinal))
                    { suppressed.Add(req.OnMissing.Substring("skip_arc:".Length)); continue; }
                    else continue; // veto was checked at eligibility; a forced template runs without the binding
                }
                var m = matches[rngMon.Next(matches.Count)];
                bible.Monuments[req.Key] = new MonumentBinding
                { Prefab = m.Name, DisplayName = m.DisplayName, X = m.Pos.x, Y = m.Pos.y, Z = m.Pos.z };
            }
        }

        // Roll this wipe's story. freshWipe=false is the mid-wipe adoption
        // path (v2 arriving on a live wipe): four_keys is forced and, being
        // roll-free, reproduces v1 exactly — in-flight progress never notices.
        private void GenerateWipeBible(bool freshWipe, string forceTemplate = null)
        {
            var bible = new WipeBible
            {
                Seed = BibleSeedNow(),
                WipeStartUtc = DateTime.UtcNow.ToString("o"),
            };

            TemplateDef tpl = null;
            if (!string.IsNullOrEmpty(forceTemplate))
            {
                tpl = FindTemplate(forceTemplate);
                if (tpl == null) PrintWarning($"Wipe bible: forced template '{forceTemplate}' not found — rolling instead.");
            }
            if (tpl == null && !freshWipe) tpl = FindTemplate("four_keys");
            if (tpl == null) tpl = PickTemplate(bible.Seed);
            if (tpl == null) tpl = FindTemplate("four_keys");
            if (tpl == null) tpl = DefaultTemplates()[0]; // templates.json broken mid-repair — the floor is always in memory
            bible.TemplateId = tpl.Id;

            // Monument bindings (and which optional arcs a sparse map drops).
            var suppressed = new HashSet<string>();
            BindMonuments(bible, tpl, SurveyMonuments(), ChildRng(bible.Seed, "monuments"), suppressed);

            // Chain order.
            var chain = new List<string>();
            if (tpl.Chain != null && tpl.Chain.Fixed != null)
                foreach (var key in tpl.Chain.Fixed)
                    if (_traders.ContainsKey(key) && !chain.Contains(key)) chain.Add(key);
            if (chain.Count == 0) chain = ShippedChainOrder();
            if (tpl.Chain != null && tpl.Chain.Mode == "shuffle" && chain.Count > 1)
            {
                var rngChain = ChildRng(bible.Seed, "chain");
                var start = 0;
                if (!string.IsNullOrEmpty(tpl.Chain.First) && chain.Contains(tpl.Chain.First))
                {
                    chain.Remove(tpl.Chain.First);
                    chain.Insert(0, tpl.Chain.First);
                    start = 1;
                }
                for (var i = chain.Count - 1; i > start; i--)
                {
                    var j = start + rngChain.Next(i - start + 1);
                    var tmp = chain[i]; chain[i] = chain[j]; chain[j] = tmp;
                }
            }
            bible.ChainOrder = chain;

            // Roles — no trader holds two.
            var rngRoles = ChildRng(bible.Seed, "roles");
            var cast = new HashSet<string>();
            foreach (var slot in tpl.Roles)
            {
                if (slot == null || string.IsNullOrEmpty(slot.Role)) continue;
                var candidates = new List<string>();
                if (slot.Candidates != null)
                    foreach (var c in slot.Candidates)
                        if (_traders.ContainsKey(c) && !cast.Contains(c)) candidates.Add(c);
                for (var n = 0; n < Math.Max(1, slot.Count) && candidates.Count > 0; n++)
                {
                    var pick = candidates[rngRoles.Next(candidates.Count)];
                    candidates.Remove(pick);
                    cast.Add(pick);
                    bible.Roles[pick] = slot.Role;
                }
            }

            // Secrets.
            var rngSecrets = ChildRng(bible.Seed, "secrets");
            foreach (var s in tpl.Secrets)
            {
                if (s == null || string.IsNullOrEmpty(s.Id) || s.RollFrom == null || s.RollFrom.Count == 0) continue;
                var val = s.RollFrom[rngSecrets.Next(s.RollFrom.Count)];
                if (val != null && val.StartsWith("@role:", StringComparison.Ordinal))
                {
                    var role = val.Substring("@role:".Length);
                    foreach (var kv in bible.Roles)
                        if (kv.Value == role) { val = kv.Key; break; }
                }
                bible.Secrets[s.Id] = val;
            }

            // Arcs: template base + one variant per slot.
            foreach (var arc in tpl.BaseArcs)
                if (!string.IsNullOrEmpty(arc) && !suppressed.Contains(arc) && !bible.ActiveArcs.Contains(arc))
                    bible.ActiveArcs.Add(arc);
            var rngSlots = ChildRng(bible.Seed, "slots");
            foreach (var slot in tpl.Slots)
            {
                if (slot == null || slot.Variants == null) continue;
                var variants = new List<string>();
                foreach (var v in slot.Variants)
                    if (!string.IsNullOrEmpty(v) && !suppressed.Contains(v)) variants.Add(v);
                if (variants.Count == 0) continue;
                var pick = variants[rngSlots.Next(variants.Count)];
                bible.VariantPicks[string.IsNullOrEmpty(slot.SlotId) ? pick : slot.SlotId] = pick;
                if (pick != ClassicVariant && !bible.ActiveArcs.Contains(pick)) bible.ActiveArcs.Add(pick);
            }

            // Beats — scheduled now, fired by Phase F's clock. Per-beat child
            // streams (not one shared stream): a beat adopted mid-wipe lands
            // on the same day a fresh roll would have given it.
            foreach (var b in tpl.Beats)
            {
                if (b == null || string.IsNullOrEmpty(b.Arc) || suppressed.Contains(b.Arc)) continue;
                bible.Beats.Add(ScheduleBeat(bible.Seed, b));
            }

            // The late-wipe choice (Phase G): the bible carries only the
            // island-level state — per-player picks live in progress.
            if (tpl.Choice != null && !string.IsNullOrEmpty(tpl.Choice.Id))
                bible.Choice = new ChoiceState { Id = tpl.Choice.Id };

            // FIELD NOTES numbering (resistance.md canon): the shipped sheets
            // are №3 and №5 and the gaps are deliberate — every wipe claims a
            // fresh number whether or not it prints one. An unpublished
            // number IS a gap, and gaps imply the larger run.
            bible.FieldNoteNumber = ClaimFieldNoteNumber();

            _state.Bible = bible;
            _bibleFreshlyRolled = true; // lets ApplyBible roll pools with RNG (a mid-wipe adoption pins instead)
            DLog($"Wipe bible rolled: template '{tpl.Id}', seed {bible.Seed}, chain {string.Join(" -> ", chain)}, " +
                 $"{bible.ActiveArcs.Count} arc(s), {bible.Roles.Count} role(s), {bible.Monuments.Count} monument binding(s), {bible.Beats.Count} beat(s).");
        }

        // Everything the bible changes at runtime flows from here. Chain
        // rewiring is deliberately NOT a profile mutation — reads go through
        // EffectiveRequiresTrader/EffectiveUnlockQuest so a SaveTraders() from
        // any calibration command can never leak the rolled chain into
        // traders.json.
        // True only between a fresh RollBible and the ApplyBible that follows
        // it: the one window where pooled turn-ins may roll with RNG. Any
        // other ApplyBible (boot on an existing wipe, post-sync reload) PINS
        // authored values instead — a live wipe's asks never change under a
        // player mid-story.
        private bool _bibleFreshlyRolled;

        private void ApplyBible()
        {
            var changed = AdoptNewSlots();
            changed |= AdoptNewBeats();
            BuildQuestIndex();
            changed |= EnsureTurninRolls(_bibleFreshlyRolled);
            _bibleFreshlyRolled = false;
            var b = _state.Bible;
            if (b != null && b.FieldNoteNumber == 0)
            {
                // A bible from before Phase F — claim its number now, same
                // ledger the roll path uses.
                b.FieldNoteNumber = ClaimFieldNoteNumber();
                changed = true;
                DLog($"FIELD NOTES №{b.FieldNoteNumber} claimed for the running wipe (pre-F bible adopted).");
            }
            var tplChoice = ActiveTemplate() != null ? ActiveTemplate().Choice : null;
            if (b != null && b.Choice == null && tplChoice != null && !string.IsNullOrEmpty(tplChoice.Id))
            {
                // Same mid-wipe contract as slots/beats: a template gaining a
                // choice after the roll adopts it un-picked.
                b.Choice = new ChoiceState { Id = tplChoice.Id };
                changed = true;
                DLog($"Choice '{tplChoice.Id}' adopted mid-wipe (template gained it after the roll).");
            }
            if (changed) SaveState();
            ValidateActiveChain();
            if (b != null)
                DLog($"Wipe bible applied: '{b.TemplateId}', chain {string.Join(" -> ", b.ChainOrder)}, active arcs: {string.Join(", ", b.ActiveArcs)}.");
        }

        // Lowest number never published: skips the canon prints (№3, №5) and
        // everything any earlier wipe claimed (CrossWipe.UsedFieldNoteNumbers,
        // which survives resets). Claiming records immediately — the caller's
        // SaveState persists both the bible's number and the ledger together.
        private int ClaimFieldNoteNumber()
        {
            var used = _state != null && _state.Memory != null ? _state.Memory.UsedFieldNoteNumbers : null;
            var n = 6;
            while (n == 3 || n == 5 || (used != null && used.Contains(n))) n++;
            if (used != null) used.Add(n);
            return n;
        }

        // A template can GAIN slots after this wipe's bible already rolled
        // (the four_keys retrofit deployed mid-wipe + rq.template.sync). Any
        // slot the bible has no pick for adopts the FIRST-listed variant —
        // the classic, by authoring convention — so a live wipe keeps playing
        // the story its players already started. Fresh wipes roll normally.
        private bool AdoptNewSlots()
        {
            var tpl = ActiveTemplate();
            var b = _state != null ? _state.Bible : null;
            if (tpl == null || b == null) return false;
            var changed = false;
            foreach (var slot in tpl.Slots)
            {
                if (slot == null || slot.Variants == null || slot.Variants.Count == 0) continue;
                var sid = string.IsNullOrEmpty(slot.SlotId) ? slot.Variants[0] : slot.SlotId;
                if (b.VariantPicks.ContainsKey(sid)) continue;
                var pick = slot.Variants[0];
                b.VariantPicks[sid] = pick;
                if (pick != ClassicVariant && !b.ActiveArcs.Contains(pick)) b.ActiveArcs.Add(pick);
                changed = true;
                DLog($"Slot '{sid}' had no pick in this wipe's bible — adopted first-listed '{pick}' (mid-wipe template change; the next wipe rolls it).");
            }
            return changed;
        }

        private BeatState ScheduleBeat(int seed, BeatDef def)
        {
            var min = Math.Max(0, def.RealDayMin);
            var max = Math.Max(min, def.RealDayMax);
            var rng = ChildRng(seed, $"beat:{def.Id}");
            return new BeatState { Id = def.Id, Arc = def.Arc, DueRealDay = min + rng.Next(max - min + 1), RequiresQuest = def.RequiresQuest };
        }

        // A template can also GAIN beats after this wipe's bible rolled (a
        // mid-wipe deploy + rq.template.sync). Missing ids are scheduled off
        // the same per-beat stream a fresh roll uses — same seed, same day.
        private bool AdoptNewBeats()
        {
            var tpl = ActiveTemplate();
            var b = _state != null ? _state.Bible : null;
            if (tpl == null || b == null) return false;
            var changed = false;
            foreach (var def in tpl.Beats)
            {
                if (def == null || string.IsNullOrEmpty(def.Id) || string.IsNullOrEmpty(def.Arc)) continue;
                var known = false;
                foreach (var s in b.Beats) if (s != null && s.Id == def.Id) { known = true; break; }
                if (known) continue;
                var scheduled = ScheduleBeat(b.Seed, def);
                b.Beats.Add(scheduled);
                changed = true;
                DLog($"Beat '{def.Id}' adopted mid-wipe: due day {scheduled.DueRealDay}, arc '{def.Arc}' (template gained it after the roll).");
            }
            return changed;
        }

        // Records an entry for every ACTIVE pooled turn-in that has none.
        // freshRoll (the tick after RollBible): rolled from the pool with a
        // child stream keyed per objective — deterministic for one seed
        // regardless of iteration order. Otherwise (a pooled quest arriving
        // mid-wipe via merge + sync): the AUTHORED values are pinned into the
        // bible, so no later reload can re-roll an ask a player already
        // started. Runs AFTER BuildQuestIndex — shadowing must be settled so
        // only the winning variant's objectives get entries.
        private bool EnsureTurninRolls(bool freshRoll)
        {
            var tpl = ActiveTemplate();
            var b = _state != null ? _state.Bible : null;
            if (tpl == null || b == null || tpl.Pools.Count == 0) return false;
            var changed = false;
            foreach (var q in _quests)
            {
                if (string.IsNullOrEmpty(q.Id) || !QuestActiveThisWipe(q)) continue;
                for (var i = 0; i < q.Objectives.Count; i++)
                {
                    var o = q.Objectives[i];
                    if (string.IsNullOrEmpty(o.Pool)) continue;
                    var key = $"{q.Id}:{i}";
                    if (b.TurninRolls.ContainsKey(key)) continue;
                    if (!freshRoll)
                    {
                        b.TurninRolls[key] = new TurninRoll { Item = o.Target, Count = o.Count };
                        changed = true;
                        DLog($"Turn-in pool '{o.Pool}' arrived mid-wipe — pinned authored values for {key} ({o.Count} x {o.Target}); the next wipe rolls it.");
                        continue;
                    }
                    TurninPool pool;
                    if (!tpl.Pools.TryGetValue(o.Pool, out pool) || pool.Options.Count == 0)
                    {
                        PrintWarning($"Quest '{q.Id}' objective {i}: pool '{o.Pool}' is not defined by template '{tpl.Id}' — authored Target/Count stand.");
                        continue;
                    }
                    var rng = ChildRng(b.Seed, $"pool:{key}");
                    var opt = pool.Options[rng.Next(pool.Options.Count)];
                    var min = Math.Max(1, opt.Min);
                    var max = Math.Max(min, opt.Max);
                    b.TurninRolls[key] = new TurninRoll { Item = opt.Item, Count = min + rng.Next(max - min + 1) };
                    changed = true;
                    DLog($"Turn-in pool '{o.Pool}' rolled for {key}: {b.TurninRolls[key].Count} x {b.TurninRolls[key].Item}.");
                }
            }
            return changed;
        }

        // Every prerequisite of an active quest must itself be active — the
        // one structural rule that keeps a rolled story playable. Variants
        // share the classic's Id, so RequiresQuest links survive any slot
        // pick by construction; this catches what authoring can still break
        // (a picked arc no quest carries, a variant tree-gated away).
        private void ValidateActiveChain()
        {
            foreach (var q in _quests)
            {
                if (string.IsNullOrEmpty(q.Id) || !QuestActiveThisWipe(q)) continue;
                if (q.RequiresQuest != null && FindQuest(q.RequiresQuest) == null)
                    PrintWarning($"Active quest '{q.Id}' requires '{q.RequiresQuest}', which is NOT active this wipe — the chain is broken here.");
                if (q.RequiresQuests != null)
                    foreach (var rq in q.RequiresQuests)
                        if (FindQuest(rq) == null)
                            PrintWarning($"Active quest '{q.Id}' requires '{rq}', which is NOT active this wipe — the chain is broken here.");
            }
            var bb = _state != null ? _state.Bible : null;
            if (bb == null) return;
            foreach (var kv in bb.VariantPicks)
            {
                if (kv.Value == ClassicVariant) continue;
                var found = false;
                foreach (var q in _quests)
                    if (q.Arc == kv.Value && QuestActiveThisWipe(q)) { found = true; break; }
                if (!found) PrintWarning($"Slot '{kv.Key}' picked arc '{kv.Value}' but no active quest carries it — check the variant's Arc tag and Tree.");
            }
            // The choice's anchor quests must exist in the live story (G) —
            // and the prompt can't hang off an Unpaid auto-claim, which never
            // renders a counter dialog for the buttons to live in.
            var tplChoiceCheck = ActiveTemplate() != null ? ActiveTemplate().Choice : null;
            if (tplChoiceCheck != null && !string.IsNullOrEmpty(tplChoiceCheck.Id))
            {
                var promptQ = FindQuest(tplChoiceCheck.PromptQuestId);
                if (promptQ == null)
                    PrintWarning($"Choice '{tplChoiceCheck.Id}': prompt quest '{tplChoiceCheck.PromptQuestId}' is not active this wipe — the fork can never be offered.");
                else if (promptQ.Giver == UnpaidGiverKey)
                    PrintWarning($"Choice '{tplChoiceCheck.Id}': prompt quest '{tplChoiceCheck.PromptQuestId}' auto-claims (Unpaid) — the fork dialog can never render. Anchor it to a counter quest.");
                if (FindQuest(ChoiceEndingQuestId(tplChoiceCheck)) == null)
                    PrintWarning($"Choice '{tplChoiceCheck.Id}': ending quest '{ChoiceEndingQuestId(tplChoiceCheck)}' is not active this wipe.");
            }
        }

        // --- beats (v2 Phase F) --------------------------------------------
        // Real days after WipeStartUtc, a beat's arc joins the wipe: strictly
        // additive. Nothing is ever deactivated, no def or progress record is
        // ever mutated — the story only grows.

        private Timer _beatTimer;

        private double RealDaysSinceWipeStart()
        {
            var b = _state != null ? _state.Bible : null;
            DateTime start;
            if (b == null || string.IsNullOrEmpty(b.WipeStartUtc) ||
                !DateTime.TryParse(b.WipeStartUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out start))
                return 0;
            return (DateTime.UtcNow - start.ToUniversalTime()).TotalDays;
        }

        // The clock runs only while a beat is still pending (hot-hook
        // discipline, timer edition). Callers re-arm after anything that can
        // create beats (boot, reroll) or fire them (rq.beat.fire).
        private void RecomputeBeatTimer()
        {
            var b = _state != null ? _state.Bible : null;
            var pending = false;
            if (b != null)
                foreach (var beat in b.Beats)
                    if (beat != null && !beat.Fired) { pending = true; break; }
            if (!pending) { StopBeatTimer(); return; }
            if (_beatTimer == null || _beatTimer.Destroyed)
                _beatTimer = timer.Every(300f, BeatTick);
        }

        private void StopBeatTimer()
        {
            if (_beatTimer != null && !_beatTimer.Destroyed) _beatTimer.Destroy();
            _beatTimer = null;
        }

        // Fires every due beat whose quest gate (if any) somebody on the
        // server has satisfied. The boot path calls this once directly, so
        // past-due beats catch up immediately — in due order.
        private void BeatTick()
        {
            var b = _state != null ? _state.Bible : null;
            if (b == null || b.Beats.Count == 0) { StopBeatTimer(); return; }
            var days = RealDaysSinceWipeStart();
            List<BeatState> due = null;
            var pending = 0;
            foreach (var beat in b.Beats)
            {
                if (beat == null || beat.Fired) continue;
                pending++;
                if (beat.DueRealDay > days) continue;
                if (!string.IsNullOrEmpty(beat.RequiresQuest) && !_state.WipeCompleted.Contains(beat.RequiresQuest)) continue;
                if (due == null) due = new List<BeatState>();
                due.Add(beat);
            }
            if (due != null)
            {
                due.Sort((x, y) => x.DueRealDay.CompareTo(y.DueRealDay));
                foreach (var beat in due) { FireBeat(beat); pending--; }
            }
            if (pending <= 0) StopBeatTimer();
        }

        // One beat goes live (F.2, additive-only): append arc → rebuild the
        // index → pin any new pooled turn-ins → open the beat's pamphlets →
        // idempotent world placement → mark fired, save. Placement is the
        // same Ensure* pass boot runs, so a failed spot retries next boot
        // with the beat safely marked fired.
        // Additively brings one arc live mid-wipe — the shared tail of a beat
        // firing and a choice's FirstPick flavor. Never deactivates anything.
        private void ActivateStoryArc(string arc, string reason)
        {
            var b = _state.Bible;
            if (b == null || string.IsNullOrEmpty(arc)) return;
            if (!b.ActiveArcs.Contains(arc)) b.ActiveArcs.Add(arc);
            BuildQuestIndex();
            EnsureTurninRolls(false); // mid-wipe arrivals pin authored values — same rule as Phase E
            try { EnsureAllFinaleStashes(); EnsureAllDrops(); EnsurePlacedNotes(); }
            catch (Exception e) { PrintWarning($"{reason}: world placement failed ({e.Message}) — the arc is live, placement retries next boot."); }
            RecomputeEngineHooks();
            ValidateActiveChain();
        }

        private void FireBeat(BeatState beat)
        {
            var b = _state.Bible;
            if (b == null || beat == null || beat.Fired) return;
            var def = FindBeatDef(beat.Id);
            if (def != null && def.Pamphlets.Count > 0)
            {
                // Additive manifest entries put the beat's prints into the
                // loot rotation through the same arc gate as everything else;
                // an operator's own entry for a file is never overwritten.
                var added = 0;
                foreach (var file in def.Pamphlets)
                {
                    if (string.IsNullOrEmpty(file)) continue;
                    var inPress = false;
                    if (_pamphletCache != null)
                        for (var i = 0; i < _pamphletCache.Count; i++)
                            if (_pamphletCache[i].File == file) { inPress = true; break; }
                    if (!inPress) PrintWarning($"Beat '{beat.Id}': pamphlet '{file}' is not in the press (oxide/data/{DataRoot}/pamphlets) — nothing will circulate.");
                    if (_pamphletMeta.ContainsKey(file)) continue;
                    _pamphletMeta[file] = new PamphletMeta { Arc = beat.Arc };
                    added++;
                }
                if (added > 0) Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/pamphlets_meta", _pamphletMeta);
            }
            ActivateStoryArc(beat.Arc, $"Beat '{beat.Id}'");
            beat.Fired = true;
            SaveState();
            DLog($"Beat '{beat.Id}' fired (day {RealDaysSinceWipeStart():0.#}, due day {beat.DueRealDay}): arc '{beat.Arc}' is live.");
        }

        private BeatDef FindBeatDef(string id)
        {
            var tpl = ActiveTemplate();
            if (tpl == null || string.IsNullOrEmpty(id)) return null;
            foreach (var d in tpl.Beats)
                if (d != null && d.Id == id) return d;
            return null;
        }

        // Dev/test: force a beat past its clock and gate.
        [ConsoleCommand("rq.beat.fire")]
        private void CmdBeatFire(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var id = arg.HasArgs() ? arg.GetString(0) : null;
            BeatState beat = null;
            if (_state != null && _state.Bible != null)
                foreach (var x in _state.Bible.Beats)
                    if (x != null && x.Id == id) { beat = x; break; }
            if (beat == null) { arg.ReplyWith($"No beat '{id ?? "?"}' in this wipe's bible — rq.bible lists them."); return; }
            if (beat.Fired) { arg.ReplyWith($"Beat '{id}' already fired."); return; }
            FireBeat(beat);
            RecomputeBeatTimer();
            arg.ReplyWith($"Beat '{id}' force-fired: arc '{beat.Arc}' is live, placement attempted (see log).");
        }

        // --- the late-wipe choice (v2 Phase G, decision 0004.5) -------------
        // One fork near the finale: dialog buttons at the prompt quest's
        // claim. The ending quest stays ONE QuestDef — the pick recolors its
        // text at display time and appends bonus rewards at claim. The
        // island's FIRST pick sets world flavor (an additive arc + one SLM
        // canon line) and is distilled into next wipe's memory.

        // The active template's choice, only if this wipe's bible actually
        // carries its state (rolled or adopted) and it is authorable-sane.
        private ChoiceDef ActiveChoice()
        {
            var tpl = ActiveTemplate();
            var c = tpl != null ? tpl.Choice : null;
            var cs = _state != null && _state.Bible != null ? _state.Bible.Choice : null;
            if (c == null || cs == null || string.IsNullOrEmpty(c.Id) || cs.Id != c.Id) return null;
            if (string.IsNullOrEmpty(c.PromptQuestId) || c.Options == null || c.Options.Count < 2) return null;
            return c;
        }

        private string ChoiceEndingQuestId(ChoiceDef c) =>
            string.IsNullOrEmpty(c.EndingQuestId) ? c.PromptQuestId : c.EndingQuestId;

        private ChoiceOption FindChoiceOption(ChoiceDef c, string optionId)
        {
            if (c == null || string.IsNullOrEmpty(optionId)) return null;
            foreach (var o in c.Options)
                if (o != null && o.Id == optionId) return o;
            return null;
        }

        // Does this player still owe a pick before quest `questId` may be
        // claimed at the counter?
        private bool ChoicePendingFor(PlayerProgress prog, string questId)
        {
            var c = ActiveChoice();
            return c != null && c.PromptQuestId == questId &&
                   (prog == null || !prog.Choices.ContainsKey(c.Id));
        }

        // The option this player picked, IF it recolors quest `questId`.
        private ChoiceOption PickedOption(PlayerProgress prog, string questId)
        {
            var c = ActiveChoice();
            if (c == null || prog == null || ChoiceEndingQuestId(c) != questId) return null;
            string pick;
            return prog.Choices.TryGetValue(c.Id, out pick) ? FindChoiceOption(c, pick) : null;
        }

        // --- verdict grudges (v2 Phase I) ----------------------------------
        // How a trader holds YOUR verdict against you. Derived at read time
        // from the player's own picks + the active template — never stored,
        // so it wipes with progress and self-heals if content changes. All
        // of it lands post-vault by construction (you pick at the claim), so
        // it costs relationship, never earned content.
        private const int GrudgeNone = 0, GrudgeReserved = 1, GrudgeFrozen = 2, GrudgeGrieving = 3, GrudgeBarred = 4;

        private static int GrudgeSeverity(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "barred": return GrudgeBarred;
                case "grieving": return GrudgeGrieving;
                case "frozen": return GrudgeFrozen;
                case "reserved": return GrudgeReserved;
                default: return GrudgeNone;
            }
        }

        private string ResolveGrudgeWho(string who)
        {
            if (string.IsNullOrEmpty(who)) return null;
            if (who == "*") return "*";
            if (who.StartsWith("@role:", StringComparison.Ordinal))
            {
                var role = who.Substring("@role:".Length);
                var b = _state != null ? _state.Bible : null;
                if (b != null)
                    foreach (var kv in b.Roles)
                        if (kv.Value == role) return kv.Key;
                return null;
            }
            if (who.StartsWith("@secret:", StringComparison.Ordinal))
            {
                var id = who.Substring("@secret:".Length);
                string val = null;
                if (_state != null && _state.Bible != null) _state.Bible.Secrets.TryGetValue(id, out val);
                // Only a living answer holds a grudge.
                return val != null && Profile(val) != null ? val : null;
            }
            return Profile(who) != null ? who : null;
        }

        private int GrudgeAgainst(PlayerProgress prog, string traderKey, out GrudgeDef strongest)
        {
            strongest = null;
            var worst = GrudgeNone;
            var c = ActiveChoice();
            if (c == null || prog == null || prog.Choices.Count == 0) return GrudgeNone;
            string pick;
            if (!prog.Choices.TryGetValue(c.Id, out pick)) return GrudgeNone;
            var opt = FindChoiceOption(c, pick);
            if (opt == null) return GrudgeNone;
            foreach (var g in opt.Grudges)
            {
                if (g == null) continue;
                var who = ResolveGrudgeWho(g.Who);
                if (who == null || (who != "*" && who != traderKey)) continue;
                var sev = GrudgeSeverity(g.Severity);
                if (sev > worst) { worst = sev; strongest = g; }
            }
            return worst;
        }

        private bool BarredAnywhere(PlayerProgress prog)
        {
            if (prog == null || prog.Choices.Count == 0) return false;
            foreach (var kv in _traders)
            {
                GrudgeDef g;
                if (GrudgeAgainst(prog, kv.Key, out g) >= GrudgeBarred) return true;
            }
            return false;
        }

        private void RecordChoicePick(BasePlayer player, PlayerProgress prog, ChoiceDef c, ChoiceOption opt)
        {
            prog.Choices[c.Id] = opt.Id;
            prog.ChoicePickedUtc[c.Id] = DateTime.UtcNow.ToString("o"); // the delayed pieces count from here (I.5)
            var cs = _state.Bible.Choice;
            int n;
            cs.Counts.TryGetValue(opt.Id, out n);
            cs.Counts[opt.Id] = n + 1;
            var first = cs.FirstPick == null;
            if (first)
            {
                // The island's verdict is whoever decided first (0004.5) —
                // world flavor keys off it exactly once.
                cs.FirstPick = opt.Id;
                cs.FirstPickLabel = opt.Label;
                if (!string.IsNullOrEmpty(opt.Arc)) ActivateStoryArc(opt.Arc, $"Choice '{c.Id}'");
                DLog($"Choice '{c.Id}': FIRST pick '{opt.Id}' by {player.displayName}{(string.IsNullOrEmpty(opt.Arc) ? "" : $" — arc '{opt.Arc}' is live")}.");
            }
            else DLog($"Choice '{c.Id}': {player.displayName} picked '{opt.Id}'.");
            MarkProgressDirty(player);
            SaveState();
            RecomputeGrudgeHooks(); // the pick may have just barred them somewhere
            RecomputePieceTimer();  // and may have just scheduled them mail (I.5)
            RecomputeCrewTimer();   // or a night call (I.6)
        }

        // --- the gambling lockout (Phase I) --------------------------------
        // A barred player's scrap is no good at HER caboose: slots open
        // through StorageContainer.PlayerOpenLoot (CanLootEntity, verified
        // against the live assembly), poker/blackjack seats mount through
        // BaseMountable (CanMountEntity, ditto). Both hooks live only while
        // a barred player is online, and the first check is an O(1) reject.
        private readonly HashSet<ulong> _barredOnline = new HashSet<ulong>();

        private void RecomputeGrudgeHooks()
        {
            _barredOnline.Clear();
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || p.IsNpc) continue;
                var prog = GetProgress(p, create: false);
                if (prog != null && BarredAnywhere(prog)) _barredOnline.Add((ulong)p.userID);
            }
            if (_barredOnline.Count > 0) { Subscribe(nameof(CanLootEntity)); Subscribe(nameof(CanMountEntity)); }
            else { Unsubscribe(nameof(CanLootEntity)); Unsubscribe(nameof(CanMountEntity)); }
        }

        // Whose caboose is this machine standing in? Same radius as the
        // no-build cylinder — the house extends exactly as far as her land.
        private string TraderShopAt(Vector3 pos)
        {
            var r = _config.ShopNoBuildRadius > 0f ? _config.ShopNoBuildRadius : 25f;
            var r2 = r * r;
            foreach (var kv in _state.Shops)
            {
                var s = kv.Value;
                if (!s.ShopPlaced) continue;
                if ((s.ShopPos - pos).sqrMagnitude <= r2) return kv.Key;
            }
            return null;
        }

        private bool GamblingBarredHere(BasePlayer player, Vector3 pos, out string traderKey)
        {
            traderKey = TraderShopAt(pos);
            if (traderKey == null) return false;
            GrudgeDef g;
            return GrudgeAgainst(GetProgress(player, create: false), traderKey, out g) >= GrudgeBarred;
        }

        private object CanLootEntity(BasePlayer player, StorageContainer container)
        {
            if (player == null || container == null || !_barredOnline.Contains((ulong)player.userID)) return null;
            var isMachine = container is SlotMachineStorage || container is CardGamePlayerStorage ||
                            container.GetParentEntity() is SlotMachine || container.GetParentEntity() is BaseCardGameEntity;
            if (!isMachine) return null;
            string key;
            if (!GamblingBarredHere(player, container.transform.position, out key)) return null;
            PrintToChat(player, L("Grudge.Machines", player, TraderName(key)));
            return false;
        }

        private object CanMountEntity(BasePlayer player, BaseMountable mount)
        {
            if (player == null || mount == null || !_barredOnline.Contains((ulong)player.userID)) return null;
            if (!(mount is CardTableSeat) && !(mount.GetParentEntity() is BaseCardGameEntity)) return null;
            string key;
            if (!GamblingBarredHere(player, mount.transform.position, out key)) return null;
            PrintToChat(player, L("Grudge.Machines", player, TraderName(key)));
            return false;
        }

        // --- delayed pieces (v2 Phase I.5) ---------------------------------
        // What a verdict sends the picker LATER: paper and parcels due a
        // rolled number of real days after their own pick, handed over the
        // next time they're awake past due. The due day is derived from
        // (bible seed, choice, piece, player) — nothing scheduled is stored;
        // the delivered ledger on progress is the only state, and it wipes
        // with everything else. Delivery is checked on wake, on connect, and
        // by a 10-minute clock that runs only while an online player still
        // has mail coming (hot-hook discipline, timer edition).

        private Timer _pieceTimer;

        private int PieceDueDay(string choiceId, DelayedPieceDef p, ulong playerId)
        {
            var min = Math.Max(0, p.RealDayMin);
            var max = Math.Max(min, p.RealDayMax);
            var rng = ChildRng(_state.Bible.Seed, $"piece:{choiceId}:{p.Id}:{playerId}");
            return min + rng.Next(max - min + 1);
        }

        private bool HasUndeliveredPieces(PlayerProgress prog)
        {
            var c = ActiveChoice();
            if (c == null || prog == null || prog.Choices.Count == 0) return false;
            string pick;
            if (!prog.Choices.TryGetValue(c.Id, out pick)) return false;
            var opt = FindChoiceOption(c, pick);
            if (opt == null) return false;
            foreach (var p in opt.Delayed)
                if (p != null && !string.IsNullOrEmpty(p.Id) && !prog.DeliveredPieces.Contains($"{c.Id}:{p.Id}"))
                    return true;
            return false;
        }

        private void RecomputePieceTimer()
        {
            var pending = false;
            if (_state != null && _state.Bible != null && ActiveChoice() != null)
                foreach (var pl in BasePlayer.activePlayerList)
                {
                    if (pl == null || pl.IsNpc || !pl.userID.IsSteamId()) continue;
                    var prog = GetProgress(pl, create: false);
                    if (prog != null && HasUndeliveredPieces(prog)) { pending = true; break; }
                }
            if (!pending)
            {
                if (_pieceTimer != null && !_pieceTimer.Destroyed) _pieceTimer.Destroy();
                _pieceTimer = null;
                return;
            }
            if (_pieceTimer == null || _pieceTimer.Destroyed) _pieceTimer = timer.Every(600f, PieceTick);
        }

        private void PieceTick()
        {
            foreach (var pl in BasePlayer.activePlayerList)
            {
                if (pl == null || pl.IsNpc || !pl.userID.IsSteamId() || pl.IsSleeping()) continue;
                DeliverDuePieces(pl);
            }
            RecomputePieceTimer();
        }

        private void DeliverDuePieces(BasePlayer player, bool force = false)
        {
            if (player == null || player.IsNpc || !player.userID.IsSteamId()) return;
            if (_state == null || _state.Bible == null) return;
            var c = ActiveChoice();
            if (c == null) return;
            var prog = GetProgress(player, create: false);
            if (prog == null) return;
            string pick;
            if (!prog.Choices.TryGetValue(c.Id, out pick)) return;
            var opt = FindChoiceOption(c, pick);
            if (opt == null || opt.Delayed.Count == 0) return;
            // A pick recorded before this shipped has no timestamp — stamp
            // it now, so its pieces arrive delayed from THIS boot rather
            // than all at once.
            string pickedUtc;
            if (!prog.ChoicePickedUtc.TryGetValue(c.Id, out pickedUtc))
            {
                pickedUtc = DateTime.UtcNow.ToString("o");
                prog.ChoicePickedUtc[c.Id] = pickedUtc;
                MarkProgressDirty(player);
                if (!force) return;
            }
            DateTime picked;
            if (!DateTime.TryParse(pickedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out picked))
                picked = DateTime.UtcNow;
            foreach (var p in opt.Delayed)
            {
                if (p == null || string.IsNullOrEmpty(p.Id)) continue;
                var ledger = $"{c.Id}:{p.Id}";
                if (prog.DeliveredPieces.Contains(ledger)) continue;
                // AfterVisit pieces (I.6) count their days from the night the
                // crew called, and simply don't exist until it has — the
                // denial can never precede the thing it denies. Force skips
                // the clock but never the anchor (rq.crew.visit first).
                var anchor = picked;
                if (p.AfterVisit)
                {
                    string visitUtc;
                    if (!prog.VisitsDone.TryGetValue(c.Id, out visitUtc)) continue;
                    if (!DateTime.TryParse(visitUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out anchor))
                        anchor = DateTime.UtcNow;
                }
                if (!force && DateTime.UtcNow < anchor.ToUniversalTime().AddDays(PieceDueDay(c.Id, p, (ulong)player.userID))) continue;
                DeliverPiece(player, prog, p, ledger);
            }
        }

        private void DeliverPiece(BasePlayer player, PlayerProgress prog, DelayedPieceDef p, string ledger)
        {
            // Claim the ledger slot before the handout — a kick mid-delivery
            // must not double-pay on the next wake (same rule as the welcome
            // note's slot).
            prog.DeliveredPieces.Add(ledger);
            MarkProgressDirty(player);
            if (!string.IsNullOrEmpty(p.NoteTag))
            {
                NoteContent content;
                if (_noteContent.TryGetValue(p.NoteTag, out content) && content != null)
                {
                    var note = ItemManager.CreateByName("note", 1);
                    if (note != null)
                    {
                        note.text = ExpandTokens(content.Text, prog: prog);
                        if (!string.IsNullOrEmpty(content.Title)) note.name = ExpandTokens(content.Title, prog: prog);
                        if (!player.inventory.GiveItem(note)) note.Drop(player.GetDropPosition(), player.GetDropVelocity());
                    }
                }
                else PrintWarning($"Delayed piece '{ledger}': NoteTag '{p.NoteTag}' has no notes.json entry — paper skipped.");
            }
            foreach (var r in p.Items)
            {
                if (r == null || string.IsNullOrEmpty(r.Item)) continue;
                var def = ItemManager.FindItemDefinition(r.Item);
                if (def == null) { PrintWarning($"Delayed piece '{ledger}': item '{r.Item}' unknown — skipped."); continue; }
                GrantRewardItem(player, def, r);
            }
            if (!string.IsNullOrEmpty(p.ChatText)) PrintToChat(player, ExpandTokens(p.ChatText, prog: prog));
            DLog($"Delayed piece '{ledger}' delivered to {player.displayName}.");
        }

        // Dev/test: what's scheduled for a player, and a force-deliver lever
        // (the drill can't wait a real day). Name argument for server-console
        // use; otherwise the caller is the target.
        private BasePlayer PieceCmdTarget(ConsoleSystem.Arg arg)
        {
            if (arg.HasArgs())
            {
                var name = arg.GetString(0);
                foreach (var pl in BasePlayer.activePlayerList)
                    if (pl != null && pl.displayName != null && pl.displayName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                        return pl;
                return null;
            }
            return arg.Player();
        }

        [ConsoleCommand("rq.piece.status")]
        private void CmdPieceStatus(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var player = PieceCmdTarget(arg);
            if (player == null) { arg.ReplyWith("No such online player (from server console: rq.piece.status <name>)."); return; }
            var c = ActiveChoice();
            if (c == null) { arg.ReplyWith("No active choice this wipe — nothing schedules pieces."); return; }
            var prog = GetProgress(player, create: false);
            string pick = null;
            if (prog != null) prog.Choices.TryGetValue(c.Id, out pick);
            if (pick == null) { arg.ReplyWith($"{player.displayName} has not picked at '{c.Id}' — nothing scheduled."); return; }
            var opt = FindChoiceOption(c, pick);
            if (opt == null || opt.Delayed.Count == 0) { arg.ReplyWith($"{player.displayName} picked '{pick}' — no delayed pieces on that option."); return; }
            var sb = new StringBuilder();
            string pickedUtc = null;
            prog.ChoicePickedUtc.TryGetValue(c.Id, out pickedUtc);
            sb.Append($"{player.displayName} picked '{pick}' at {pickedUtc ?? "(unstamped — stamps on next delivery check)"}\n");
            foreach (var p in opt.Delayed)
            {
                if (p == null || string.IsNullOrEmpty(p.Id)) continue;
                var done = prog.DeliveredPieces.Contains($"{c.Id}:{p.Id}");
                var anchorLabel = p.AfterVisit ? "after the visit" : "after the pick";
                sb.Append($"  {p.Id}: due day {PieceDueDay(c.Id, p, (ulong)player.userID)} {anchorLabel} — {(done ? "DELIVERED" : "pending")}\n");
            }
            if (opt.Visit != null)
            {
                string visitUtc;
                var visited = prog.VisitsDone.TryGetValue(c.Id, out visitUtc);
                sb.Append($"  visit: night of day {opt.Visit.RealDay}+ — {(visited ? $"CALLED at {visitUtc}" : "pending")}\n");
            }
            arg.ReplyWith(sb.ToString().TrimEnd());
        }

        [ConsoleCommand("rq.piece.deliver")]
        private void CmdPieceDeliver(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var player = PieceCmdTarget(arg);
            if (player == null) { arg.ReplyWith("No such online player (from server console: rq.piece.deliver <name>)."); return; }
            DeliverDuePieces(player, force: true);
            RecomputePieceTimer();
            arg.ReplyWith($"Forced every pending delayed piece for {player.displayName} (see log for what landed).");
        }

        // --- the collections crew (v2 Phase I.6) ---------------------------
        // The one verdict that sends people instead of paper. Unsigned guns
        // in company hazmats call on the picker by night, real days after
        // the pick — telegraphed by the day-1 warning note (I.5), survivable
        // by doctrine (damage-scaled, engagement window bounded, they never
        // path into a base — menace at the walls is the whole performance),
        // lootable when killed, and denied in print afterward. NPC recipe is
        // FakeFriends' live-proven custom-brain scientist (their CLAUDE.md
        // §Key findings: off-monument vanilla scientists are inert, so the
        // brain is manual — component swap, terrain-typed agent, movement
        // pump), stripped of everything the visit doesn't need: no loot
        // states, no patrol economy, target assigned rather than sensed.

        // agentTypeID of the navmesh surface that answers typed queries at
        // off-monument terrain ('Animal' on the live server — never
        // hardcode; the Humanoid bake exists only inside monuments). Probed
        // once per session; visits are off-monument by design.
        private static int _terrainAgentTypeId = int.MinValue;
        private static bool _terrainAgentTypeProbed;

        public static int TerrainAgentTypeId(Vector3 near)
        {
            if (_terrainAgentTypeProbed) return _terrainAgentTypeId;
            _terrainAgentTypeProbed = true;
            for (var i = 0; i < NavMesh.GetSettingsCount(); i++)
            {
                var s = NavMesh.GetSettingsByIndex(i);
                var filter = new NavMeshQueryFilter { agentTypeID = s.agentTypeID, areaMask = NavMesh.AllAreas };
                NavMeshHit hit;
                if (NavMesh.SamplePosition(near, out hit, 6f, filter)) { _terrainAgentTypeId = s.agentTypeID; break; }
            }
            return _terrainAgentTypeId;
        }

        public class CrewNpc : ScientistNPC
        {
            public ulong TargetId;             // the picker being called on
            public Vector3 SpawnPos;
            public float CombatRange = 25f;
            // Default scientist identity everywhere the client looks —
            // "Scientist" on the death screen, scientist corpse. Unsigned by
            // construction; the deniability IS the disguise.

            // The AI manager parks NPCs with no player nearby and a parked
            // navigator re-targets to its own feet (FakeFriends probe 5P3).
            // A visit runs near a real player, but the wind-down walk must
            // finish even if the target logs off mid-leave.
            public override bool IsDormant { get { return false; } set { } }

            // Shot from a blind spot: professionals return fire.
            public override void OnAttacked(HitInfo info)
            {
                base.OnAttacked(info);
                var attacker = info?.InitiatorPlayer;
                if (attacker != null && attacker != this && !attacker.IsDestroyed)
                    GetComponent<CrewBrain>()?.NotifyAttacked(attacker);
            }
        }

        public class CrewBrain : ScientistBrain
        {
            private CrewNpc _npc;
            private NavMeshAgent _agent;
            private float _lastMoveTick;
            private BasePlayer _retaliator;
            private float _retaliateUntil;
            private float _nextFireAt, _nextPathAt, _nextPrivCheckAt, _nextLoiterAt;
            private bool _targetInPriv;
            private bool _leaving;
            public string StateName = "approach";

            public override void AddStates()
            {
                states = new Dictionary<AIState, BasicAIState>(); // no AIDesign graph — Think is manual
            }

            public override void InitializeAI()
            {
                // No base call: the vanilla path needs AIInformationZone move
                // points that don't exist off-monument (FakeFriends recon).
                _npc = GetComponent<CrewNpc>();
                ThinkMode = AIThinkMode.Interval;
                thinkRate = 0.25f;
                Navigator = GetComponent<BaseNavigator>();
                _agent = GetComponent<NavMeshAgent>();
                if (_npc == null || Navigator == null || _agent == null) return;
                // Verified spawn recipe: configure the raw Unity agent while
                // disabled (raw speed is 0 otherwise), typed to the surface
                // that answers here, then enable-in-place at a sampled spot.
                _agent.enabled = false;
                var typeId = TerrainAgentTypeId(_npc.transform.position);
                if (typeId != int.MinValue && _npc.NavAgent != null)
                {
                    _npc.NavAgent.agentTypeID = typeId;
                    _npc.NavAgent.areaMask = NavMesh.AllAreas;
                }
                _agent.speed = 5f;
                _agent.acceleration = 8f;
                _agent.angularSpeed = 120f;
                Navigator.MaxWaterDepth = 0.5f;
                _agent.updatePosition = false;  // Navigator.Think copies nextPosition → ServerPosition
                _agent.updateRotation = false;
                NavMeshHit snap;
                if (NavMesh.SamplePosition(_npc.transform.position, out snap, 6f, NavMesh.AllAreas))
                    _npc.transform.position = snap.position;
                _agent.enabled = true;
                Navigator.SetNavMeshEnabled(true);
                Navigator.PlaceOnNavMesh(0f);
                _lastMoveTick = UnityEngine.Time.realtimeSinceStartup;
                InvokeRandomized(MovementPump, 1f, 0.1f, 0.01f); // clones vanilla StartMovementTick (private)
                Senses.Init(owner: _npc, brain: this, memoryDuration: 10f, range: 60f, targetLostRange: 120f,
                    visionCone: -1f, checkVision: false, checkLOS: false, ignoreNonVisionSneakers: true,
                    listenRange: 20f, hostileTargetsOnly: false, senseFriendlies: false,
                    ignoreSafeZonePlayers: true, senseTypes: EntityType.Player, refreshKnownLOS: false);
            }

            // InvokeRandomized delegates outlive a killed entity if the kill
            // aborts mid-pool-return — always cancel before despawn.
            public void StopPump() => CancelInvoke(MovementPump);

            public void NotifyAttacked(BasePlayer attacker)
            {
                if (_npc == null || _npc.IsDestroyed || attacker == null || attacker.IsDead() || _leaving) return;
                if (!((ulong)attacker.userID).IsSteamId()) return; // animals, scientists: not tonight's business
                if ((ulong)attacker.userID == _npc.TargetId) return; // already the appointment
                _retaliator = attacker;
                _retaliateUntil = UnityEngine.Time.realtimeSinceStartup + 20f;
            }

            // The wind-down: walk off into the dark, despawn follows.
            public void OrderLeave()
            {
                _leaving = true;
                StateName = "leave";
                if (_npc == null || _npc.IsDestroyed || Navigator == null) return;
                if (_npc.modelState != null && _npc.modelState.ducked) SetDucked(false);
                var back = _npc.SpawnPos - _npc.transform.position;
                back.y = 0f;
                var dir = back.sqrMagnitude > 1f ? back.normalized : -_npc.transform.forward;
                var goal = _npc.transform.position + dir * 80f;
                goal.y = TerrainMeta.HeightMap.GetHeight(goal);
                NavMeshHit hit;
                if (NavMesh.SamplePosition(goal, out hit, 10f, NavMesh.AllAreas))
                    Navigator.SetDestination(hit.position, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
            }

            private void MovementPump()
            {
                var now = UnityEngine.Time.realtimeSinceStartup;
                var delta = now - _lastMoveTick;
                _lastMoveTick = now;
                Navigator?.Think(delta);
                // Ground snap: the Animal-bake navmesh floats a few tenths
                // above the surface; small offsets glide, real drops snap
                // (FakeFriends 5A2 smoothing — the instant snap read as
                // bouncing from a distance). Same mask as DelayedForceToGround.
                if (_npc == null || _npc.IsDestroyed) return;
                var p = _npc.ServerPosition;
                RaycastHit ground;
                if (UnityEngine.Physics.Raycast(p + Vector3.up * 0.6f, Vector3.down, out ground, 4f, 10551296))
                {
                    var dy = ground.point.y - p.y;
                    if (Mathf.Abs(dy) > 0.03f)
                    {
                        p.y = Mathf.Abs(dy) > 0.5f ? ground.point.y : Mathf.MoveTowards(p.y, ground.point.y, 1.5f * delta);
                        _npc.ServerPosition = p;
                    }
                }
            }

            private void SetDucked(bool v)
            {
                if (_npc == null || _npc.IsDestroyed || _npc.modelState == null) return;
                _npc.modelState.ducked = v;
                _npc.SendNetworkUpdate();
            }

            // Retaliator while fresh and standing, else the appointment.
            private BasePlayer ResolveTarget(float now)
            {
                if (_retaliator != null &&
                    (now > _retaliateUntil || _retaliator.IsDestroyed || _retaliator.IsDead() || _retaliator.IsSleeping()))
                    _retaliator = null;
                if (_retaliator != null) return _retaliator;
                var t = BasePlayer.FindByID(_npc.TargetId);
                return t != null && !t.IsDestroyed && t.IsConnected && !t.IsDead() && !t.IsSleeping() ? t : null;
            }

            public override void Think(float delta)
            {
                if (!ConVar.AI.think) return;
                if (_npc == null || _npc.IsDestroyed || Navigator == null) return;
                Senses.Update();
                if (_leaving) return; // committed to the walk-off; despawn is on its way
                var now = UnityEngine.Time.realtimeSinceStartup;
                var target = ResolveTarget(now);
                if (target == null)
                {
                    // Appointment dead, offline or asleep: hold near the door.
                    // The visit window's end orders the actual walk-off.
                    StateName = "wait";
                    LoiterTick(now, _npc.transform.position);
                    return;
                }
                // The FakeFriends 5D doctrine, inverted for a stakeout: never
                // path INTO building privilege, but don't leave either — the
                // menace at the walls is the visit. Any privilege counts (the
                // target hiding in a neighbor's compound reads the same).
                if (now >= _nextPrivCheckAt)
                {
                    _nextPrivCheckAt = now + 1f;
                    _targetInPriv = target.GetBuildingPrivilege(true) != null;
                }
                var aimPoint = target.eyes != null ? target.eyes.position - Vector3.up * 0.15f : target.CenterPoint();
                var dist = Vector3.Distance(_npc.transform.position, target.transform.position);
                var visible = _npc.IsVisible(aimPoint);
                if (visible && dist <= _npc.CombatRange)
                {
                    // Hold, face, fire on a ragged cadence — steadier crouch
                    // at range, standing for close quarters (FakeFriends 5A2).
                    StateName = "combat";
                    Navigator.Stop();
                    if (_npc.modelState != null && _npc.modelState.ducked != dist > 12f) SetDucked(dist > 12f);
                    _npc.SetAimDirection((aimPoint - _npc.eyes.position).normalized);
                    if (now >= _nextFireAt)
                    {
                        _nextFireAt = now + UnityEngine.Random.Range(0.7f, 1.3f); // survivable cadence
                        _npc.EquipWeapon();
                        _npc.ShotTest(dist);
                    }
                }
                else if (_targetInPriv)
                {
                    // The stakeout: circle outside at standoff distance.
                    StateName = "watch";
                    if (_npc.modelState != null && _npc.modelState.ducked) SetDucked(false);
                    LoiterTick(now, target.transform.position);
                }
                else
                {
                    StateName = "approach";
                    if (_npc.modelState != null && _npc.modelState.ducked) SetDucked(false);
                    if (now >= _nextPathAt)
                    {
                        _nextPathAt = now + 1f;
                        Navigator.SetDestination(target.transform.position, BaseNavigator.NavigationSpeed.Fast, 0f, 0f);
                    }
                }
            }

            private void LoiterTick(float now, Vector3 around)
            {
                if (now < _nextLoiterAt) return;
                _nextLoiterAt = now + UnityEngine.Random.Range(6f, 12f);
                var offset = UnityEngine.Random.insideUnitCircle.normalized * UnityEngine.Random.Range(18f, 30f);
                var goal = around + new Vector3(offset.x, 0f, offset.y);
                goal.y = TerrainMeta.HeightMap.GetHeight(goal);
                NavMeshHit hit;
                if (NavMesh.SamplePosition(goal, out hit, 6f, NavMesh.AllAreas))
                    Navigator.SetDestination(hit.position, BaseNavigator.NavigationSpeed.Slow, 0f, 0f);
            }
        }

        private const string CrewScientistPrefab = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab";
        private readonly List<CrewNpc> _crewActive = new List<CrewNpc>();
        private ulong _crewTargetId;
        private float _crewDamageScale = 1f;
        private Timer _crewTimer;
        private Timer _crewEndTimer;

        private CrewNpc SpawnCrewNpc(Vector3 pos, ulong targetId, int index)
        {
            var proto = GameManager.server.CreateEntity(CrewScientistPrefab, pos, Quaternion.identity, startActive: false) as ScientistNPC;
            if (proto == null) return null;
            var protoBrain = proto.GetComponent<ScientistBrain>();
            var npc = proto.gameObject.AddComponent<CrewNpc>();
            proto.gameObject.AddComponent<CrewBrain>();
            // Serialized fields lost in the component swap, copied from the
            // proto (public fields — no reflection). prefabID=0 would mean
            // "Couldn't create network entity" and a Kill()-path NRE.
            npc.prefabID = proto.prefabID;
            npc.bounds = proto.bounds;
            UnityEngine.Object.DestroyImmediate(proto, true);
            UnityEngine.Object.DestroyImmediate(protoBrain, true);
            npc.enableSaving = false;      // the visit is ours; never engine-persisted
            npc.LegacyNavigation = false;  // lost serialized field — gates Navigator.Init
            npc.syncPosition = true;       // lost serialized field — gates movement + client sync
            npc.TargetId = targetId;
            npc.SpawnPos = pos;
            npc.gameObject.AwakeFromInstantiate();
            npc.Spawn();
            npc.InitializeHealth(100f, 100f);
            // The swap loses the prefab loadout — dress and arm explicitly.
            // Company hazmats (the whole message), one real gun, two side
            // arms; pockets carry their pay, which is what "lootable" means.
            GiveCrewItem(npc, "hazmatsuit_scientist", 1, npc.inventory.containerWear);
            GiveCrewItem(npc, index == 0 ? "smg.mp5" : "pistol.semiauto", 1, npc.inventory.containerBelt);
            GiveCrewItem(npc, "scrap", index == 0 ? 57 : 38 + index * 5, npc.inventory.containerMain);
            GiveCrewItem(npc, "bandage", 2, npc.inventory.containerMain);
            return npc;
        }

        private void GiveCrewItem(CrewNpc npc, string shortname, int amount, ItemContainer container)
        {
            if (npc == null || container == null) return;
            var item = ItemManager.CreateByName(shortname, amount);
            if (item == null) { PrintWarning($"Crew item '{shortname}' unknown — skipped."); return; }
            if (!item.MoveToContainer(container)) item.Remove();
        }

        private Vector3? CrewSpawnPoint(Vector3 center, float minR, float maxR)
        {
            for (var i = 0; i < 12; i++)
            {
                var ang = UnityEngine.Random.value * Mathf.PI * 2f;
                var r = UnityEngine.Random.Range(minR, maxR);
                var p = center + new Vector3(Mathf.Sin(ang) * r, 0f, Mathf.Cos(ang) * r);
                p.y = TerrainMeta.HeightMap.GetHeight(p);
                if (TerrainMeta.WaterMap != null && p.y < TerrainMeta.WaterMap.GetHeight(p)) continue;
                NavMeshHit hit;
                var typeId = TerrainAgentTypeId(p);
                if (typeId != int.MinValue)
                {
                    var filter = new NavMeshQueryFilter { agentTypeID = typeId, areaMask = NavMesh.AllAreas };
                    if (NavMesh.SamplePosition(p, out hit, 6f, filter)) return hit.position;
                }
                else if (NavMesh.SamplePosition(p, out hit, 6f, NavMesh.AllAreas)) return hit.position;
            }
            return null;
        }

        // The option this player picked IF it carries a visit.
        private ChoiceOption PickedVisitOption(PlayerProgress prog, ChoiceDef c)
        {
            string pick;
            if (prog == null || !prog.Choices.TryGetValue(c.Id, out pick)) return null;
            var opt = FindChoiceOption(c, pick);
            return opt != null && opt.Visit != null ? opt : null;
        }

        private bool VisitDue(PlayerProgress prog, ChoiceDef c, VisitDef def)
        {
            string pickedUtc;
            if (!prog.ChoicePickedUtc.TryGetValue(c.Id, out pickedUtc)) return false; // stamps on their next wake (I.5)
            DateTime picked;
            if (!DateTime.TryParse(pickedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out picked)) return false;
            return DateTime.UtcNow >= picked.ToUniversalTime().AddDays(Math.Max(0, def.RealDay));
        }

        // The 2-minute night watch: armed only while an online picker still
        // owes a visit (or a crew is out). Nights are ~10 real minutes, so
        // the piece timer's 10-minute cadence could sleep through one.
        private void RecomputeCrewTimer()
        {
            var needed = _crewActive.Count > 0;
            if (!needed)
            {
                var c = ActiveChoice();
                if (c != null)
                    foreach (var pl in BasePlayer.activePlayerList)
                    {
                        if (pl == null || pl.IsNpc || !pl.userID.IsSteamId()) continue;
                        var prog = GetProgress(pl, create: false);
                        if (prog == null || prog.VisitsDone.ContainsKey(c.Id)) continue;
                        if (PickedVisitOption(prog, c) != null) { needed = true; break; }
                    }
            }
            if (!needed)
            {
                if (_crewTimer != null && !_crewTimer.Destroyed) _crewTimer.Destroy();
                _crewTimer = null;
                return;
            }
            if (_crewTimer == null || _crewTimer.Destroyed) _crewTimer = timer.Every(120f, CrewTick);
        }

        private void CrewTick()
        {
            if (_crewActive.Count > 0)
            {
                // All three down early: nothing left to wind down.
                var alive = false;
                foreach (var npc in _crewActive)
                    if (npc != null && !npc.IsDestroyed && !npc.IsDead()) { alive = true; break; }
                if (!alive) { _crewEndTimer?.Destroy(); _crewEndTimer = null; DespawnCrew(); }
                return;
            }
            var c = ActiveChoice();
            if (c == null) { RecomputeCrewTimer(); return; }
            var sky = TOD_Sky.Instance;
            if (sky == null || !sky.IsNight) return; // the crew only works nights
            foreach (var pl in BasePlayer.activePlayerList)
            {
                if (pl == null || pl.IsNpc || !pl.userID.IsSteamId() || pl.IsSleeping() || pl.IsDead()) continue;
                if (pl.InSafeZone()) continue; // not in front of witnesses
                var prog = GetProgress(pl, create: false);
                if (prog == null || prog.VisitsDone.ContainsKey(c.Id)) continue;
                var opt = PickedVisitOption(prog, c);
                if (opt == null || !VisitDue(prog, c, opt.Visit)) continue;
                StartVisit(pl, c, opt);
                break; // one call a night
            }
            RecomputeCrewTimer();
        }

        private void StartVisit(BasePlayer target, ChoiceDef c, ChoiceOption opt)
        {
            var def = opt.Visit;
            var prog = GetProgress(target, create: false);
            if (prog == null || def == null) return;
            // Only a real pick writes the once-per-wipe ledger — a forced
            // drill visit on someone who never picked stays repeatable and
            // never blocks their real consequence.
            string pick;
            var realPick = prog.Choices.TryGetValue(c.Id, out pick) && pick == opt.Id;
            if (realPick)
            {
                prog.VisitsDone[c.Id] = DateTime.UtcNow.ToString("o"); // claim before the spawn (crash-safe once-each)
                MarkProgressDirty(target);
            }
            var spawned = 0;
            var count = Mathf.Clamp(def.Count, 1, 5);
            for (var i = 0; i < count; i++)
            {
                var pos = CrewSpawnPoint(target.transform.position, 35f, 55f);
                if (pos == null) continue;
                var npc = SpawnCrewNpc(pos.Value, (ulong)target.userID, i);
                if (npc != null) { _crewActive.Add(npc); spawned++; }
            }
            if (spawned == 0)
            {
                // No spawnable ground tonight (deep water, cliff nest) — put
                // the appointment back; the target moves, another night comes.
                if (realPick) { prog.VisitsDone.Remove(c.Id); MarkProgressDirty(target); }
                DLog($"Collections crew found no ground near {target.displayName} — the call is postponed.");
                return;
            }
            _crewTargetId = (ulong)target.userID;
            _crewDamageScale = Mathf.Clamp(def.DamageScale, 0.1f, 1f);
            RecomputeProtectionHook(); // arms OnEntityTakeDamage for the damage scale
            if (!string.IsNullOrEmpty(def.ChatText)) PrintToChat(target, ExpandTokens(def.ChatText, prog: prog));
            var mins = Mathf.Clamp(def.DurationMinutes, 1, 30);
            _crewEndTimer?.Destroy();
            _crewEndTimer = timer.Once(mins * 60f, () => EndVisit(true));
            DLog($"Collections crew: {spawned} caller(s) on {target.displayName} (choice '{c.Id}', {mins} min window{(realPick ? "" : ", DRILL — no ledger")}).");
        }

        // walkAway: they finish the performance and fade; false = despawn now
        // (unload, dev lever, target gone).
        private void EndVisit(bool walkAway)
        {
            _crewEndTimer?.Destroy();
            _crewEndTimer = null;
            if (_crewActive.Count == 0) return;
            if (walkAway)
            {
                foreach (var npc in _crewActive)
                    if (npc != null && !npc.IsDestroyed && !npc.IsDead())
                        npc.GetComponent<CrewBrain>()?.OrderLeave();
                timer.Once(25f, DespawnCrew);
            }
            else DespawnCrew();
        }

        private void DespawnCrew()
        {
            foreach (var npc in _crewActive)
            {
                if (npc == null) continue;
                npc.GetComponent<CrewBrain>()?.StopPump();
                try { if (!npc.IsDestroyed) npc.Kill(); }
                catch { /* ResetState→LookupPrefab NRE on swapped scientists (FakeFriends probe caveat) */ }
                if (npc.gameObject != null) UnityEngine.Object.DestroyImmediate(npc.gameObject);
            }
            _crewActive.Clear();
            _crewTargetId = 0;
            RecomputeProtectionHook();
            RecomputeCrewTimer();
        }

        // Dev/test: force the call now (skips day, night and safe-zone gates;
        // a target who never actually picked gets a DRILL visit that writes
        // no ledger). rq.crew.end winds an active visit down.
        [ConsoleCommand("rq.crew.visit")]
        private void CmdCrewVisit(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var player = PieceCmdTarget(arg);
            if (player == null) { arg.ReplyWith("No such online player (from server console: rq.crew.visit <name>)."); return; }
            if (_crewActive.Count > 0) { arg.ReplyWith("A visit is already underway — rq.crew.end first."); return; }
            var c = ActiveChoice();
            if (c == null) { arg.ReplyWith("No active choice this wipe — no verdict, no crew."); return; }
            ChoiceOption opt = PickedVisitOption(GetProgress(player, create: false), c);
            if (opt == null)
                foreach (var o in c.Options)
                    if (o != null && o.Visit != null) { opt = o; break; }
            if (opt == null) { arg.ReplyWith($"Choice '{c.Id}' has no option with a Visit — nothing to send."); return; }
            StartVisit(player, c, opt);
            arg.ReplyWith(_crewActive.Count > 0
                ? $"The crew is out: {_crewActive.Count} caller(s) on {player.displayName}."
                : "No spawnable ground near the target — move somewhere flatter and retry.");
        }

        [ConsoleCommand("rq.crew.end")]
        private void CmdCrewEnd(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (_crewActive.Count == 0) { arg.ReplyWith("No visit underway."); return; }
            EndVisit(walkAway: arg.GetString(0) != "now");
            arg.ReplyWith("The crew is leaving (pass 'now' to despawn instantly).");
        }

        // In-memory bible for when generation itself throws — the engine must
        // never boot storyless (same rule as the guarded world setup).
        private WipeBible FallbackBible()
        {
            var tpl = DefaultTemplates()[0];
            var bible = new WipeBible
            {
                Seed = BibleSeedNow(),
                WipeStartUtc = DateTime.UtcNow.ToString("o"),
                TemplateId = tpl.Id,
            };
            foreach (var arc in tpl.BaseArcs) bible.ActiveArcs.Add(arc);
            var chain = new List<string>();
            foreach (var key in tpl.Chain.Fixed) if (_traders.ContainsKey(key)) chain.Add(key);
            bible.ChainOrder = chain.Count > 0 ? chain : ShippedChainOrder();
            return bible;
        }

        // --- effective chain wiring ---------------------------------------
        // The bible's ChainOrder overrides profile RequiresTrader/UnlockQuest.
        // An operator's explicit "" (deliberately open, the documented v1.5
        // semantics) still wins. NOTE for Phase D: shipped defaults also use
        // "" for the chain head, so "operator-set" and "shipped" empties are
        // currently indistinguishable — revisit before the first shuffle
        // template ships (PLAN risk 2).
        private string EffectiveRequiresTrader(string traderKey)
        {
            var p = Profile(traderKey);
            if (p == null) return null;
            if (p.RequiresTrader == "") return "";
            var order = _state != null && _state.Bible != null ? _state.Bible.ChainOrder : null;
            if (order != null && order.Count > 0)
            {
                var idx = order.IndexOf(traderKey);
                if (idx == 0) return "";
                if (idx > 0) return order[idx - 1];
                // Trader outside the rolled chain — her profile wiring stands.
            }
            return p.RequiresTrader;
        }

        // The quest whose claim opens this trader's door: the previous chain
        // trader's personal finale (profiles carry ChainFinaleQuest so the
        // wiring survives any chain order).
        private string EffectiveUnlockQuest(string traderKey)
        {
            var p = Profile(traderKey);
            if (p == null) return null;
            if (p.RequiresTrader == "") return null;
            var order = _state != null && _state.Bible != null ? _state.Bible.ChainOrder : null;
            if (order != null && order.Count > 0)
            {
                var idx = order.IndexOf(traderKey);
                if (idx == 0) return null;
                if (idx > 0)
                {
                    var prev = Profile(order[idx - 1]);
                    if (prev != null && !string.IsNullOrEmpty(prev.ChainFinaleQuest)) return prev.ChainFinaleQuest;
                }
            }
            return p.UnlockQuest;
        }

        // --- role overlays (v2 Phase C) ------------------------------------
        // oxide/data/RustQuests/overlays.json, keyed "<traderKey>.<roleId>".
        // When the bible rolls a trader into a role, her overlay recolors the
        // scripted dialog and feeds the SLM — applied at READ time, never by
        // mutating the profile (same rule as chain rewiring: SaveTraders must
        // never leak rolled state into traders.json). No roles rolled (all of
        // four_keys) = overlays inert.
        private class DialogOverlay
        {
            // Scripted-dialog REPLACEMENTS — empty keeps the profile's line.
            public string GreetingText = "";
            public string ProgressText = "";
            public string ReadyText = "";
            public string ChainDoneText = "";
            public string ChainPendingText = "";
            public string LockedGreetingText = "";
            // SLM ADDITIONS — appended to the profile's Persona / SmallTalk.
            public string PersonaAdd = "";
            public string SmallTalkAdd = "";
            // Warmth-gated slip lines by secret id: joins the chat prompt once
            // the player's tier with this trader reaches that secret's
            // SlipTier. A slip is an authored breadcrumb — NEVER the value.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> Slips = new Dictionary<string, string>();
        }

        private Dictionary<string, DialogOverlay> _overlays = new Dictionary<string, DialogOverlay>();

        // "The Press" overlays (v2 Phase D). One per (trader, role) the
        // template can roll. Closings use {onward}, which renders a mid-chain
        // referral or the back-to-the-head vault line by itself — one
        // authored text, true in any rolled order.
        //
        // FORMAT HAZARD: Greeting/Progress/Ready replacements pass through
        // string.Format ({0} = name / objectives / complete-text), so any
        // OTHER curly token in those three fields throws. Keep {onward} and
        // friends to ChainDone/ChainPending, which never format.
        //
        // Slip rules: a slip is a tell in HER voice, never a name, never the
        // fact. The press's own slips are the strongest signal by design —
        // that's the deduction game.
        private Dictionary<string, DialogOverlay> DefaultOverlays()
        {
            const string rebeccaClosing =
                "The homestead stands and there's nothing left I could teach you.\n\n{onward}\n\nKettle's on whenever you pass through the green.";
            const string alexaClosing =
                "Nothing left on the lift and nothing left owing.\n\n{onward}\n\nBay's open. Break something and I'll fix it, for scrap.";
            const string oliviaClosing =
                "You've bought everything I sell and earned everything I gave. There is nothing left on the shelf.\n\n{onward}\n\nFurnace stays lit. Come warm your hands when the island gets unfriendly.";

            return new Dictionary<string, DialogOverlay>
            {
                ["sonia.skeptic"] = new DialogOverlay
                {
                    PersonaAdd = "The pamphlets worry you — not because they're wrong, but because Cobalt's kind never forgets a debt in their favor. You want no part of the paper war.",
                    SmallTalkAdd = "Who reads the pamphlets around here, and who only pretends not to.",
                    ChainPendingText =
                        "Nothing left on my list, hunter. You cleared it and dug up my ghosts besides.\n\n" +
                        "But we're not done. Four of us came off that payroll, and what the four of us put in the ground doesn't open for one name.\n\n" +
                        "{onward}\n\n" +
                        "When all three have paid you out, come back. Then we talk about the last door.",
                    Slips = new Dictionary<string, string>
                    {
                        ["press_identity"] = "you can smell ink under somebody's counter the way you smell a snare on a trail — you just haven't decided whether to say whose.",
                    },
                },

                // --- Rebecca -----------------------------------------------
                ["rebecca.press"] = new DialogOverlay
                {
                    PersonaAdd = "When the pamphlets come up you change the subject to weather or seedlings, a little too smoothly.",
                    SmallTalkAdd = "Paper, oddly: how it yellows, how it takes ink, what keeps it dry in a damp cellar.",
                    ChainDoneText = rebeccaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["press_identity"] = "the stains on your fingers aren't all berry dye, and you know exactly how a ribbon press sounds at three in the morning.",
                        ["c_author"] = "you've written more welcome letters in your life than anyone would guess from your calluses.",
                    },
                },
                ["rebecca.bitter"] = new DialogOverlay
                {
                    PersonaAdd = "The pamphlets make you quietly angry — they mourn a payroll you were on, and you'd rather the dead stay buried.",
                    SmallTalkAdd = "What the greenhouses were like before the lights went out, told carefully around the sore parts.",
                    ChainDoneText = rebeccaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["c_author"] = "you recognize the hand behind the note every newcomer carries, and you have never once said so.",
                    },
                },
                ["rebecca.sympathizer"] = new DialogOverlay
                {
                    PersonaAdd = "You agree with more of the pamphlets than you admit, and you leave them lying where guests will read them.",
                    SmallTalkAdd = "Which sheets said something true, without ever saying who might have typed them.",
                    ChainDoneText = rebeccaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["press_identity"] = "you've seen who restocks a certain crate, and you make a point of looking away.",
                    },
                },

                // --- Alexa -------------------------------------------------
                ["alexa.press"] = new DialogOverlay
                {
                    PersonaAdd = "You joke that whoever prints those sheets should tune their roller tension — and then you drop the subject fast.",
                    SmallTalkAdd = "Rollers, ribbons and platens — the little machines with moving parts nobody thinks about.",
                    ChainDoneText = alexaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["press_identity"] = "the solvent smell in your bay isn't all engine — some of it is ink, and you know it.",
                        ["c_author"] = "your handwriting goes neat and careful when nobody's watching, nothing like a mechanic's scrawl.",
                    },
                },
                ["alexa.bitter"] = new DialogOverlay
                {
                    PersonaAdd = "You laughed at the first pamphlet and haven't laughed at one since — forty seats for two hundred people isn't a joke to you.",
                    SmallTalkAdd = "The motor pool crew, by nickname only, and where each of them didn't end up.",
                    ChainDoneText = alexaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["c_author"] = "you could name the hand that types those welcome notes, if anyone ever asked the right way.",
                    },
                },
                ["alexa.sympathizer"] = new DialogOverlay
                {
                    PersonaAdd = "You think whoever prints the sheets has more guts than the rest of the island combined, and you'd shake her hand if you knew it.",
                    SmallTalkAdd = "Machines that get borrowed and come back cleaner than they left.",
                    ChainDoneText = alexaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["press_identity"] = "somebody asked you to keep a small machine running last winter, and you didn't ask what it stamps.",
                    },
                },

                // --- Olivia ------------------------------------------------
                ["olivia.press"] = new DialogOverlay
                {
                    PersonaAdd = "You call the pamphlets a waste of good paper, in the flat tone of someone reciting a line.",
                    SmallTalkAdd = "Print tolerances, of all things — kerning, platen pressure, why cheap ink freezes.",
                    ChainDoneText = oliviaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["press_identity"] = "there is a machine in your back room you oil more carefully than any rifle, and paper dust in your gloves.",
                        ["c_author"] = "for a woman of few words you own a surprising number of ribbons.",
                    },
                },
                ["olivia.bitter"] = new DialogOverlay
                {
                    PersonaAdd = "The sheets read like an audit of the people who left you behind — you were owed more than money.",
                    SmallTalkAdd = "The severance that never cleared, in exact figures, once and never again.",
                    ChainDoneText = oliviaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["c_author"] = "you knew C. when C. still clocked in. That is all you will ever say, and only once.",
                    },
                },
                ["olivia.sympathizer"] = new DialogOverlay
                {
                    PersonaAdd = "The pamphlets state facts. You approve of facts.",
                    SmallTalkAdd = "Which claims in the sheets check out against your own ledgers.",
                    ChainDoneText = oliviaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["press_identity"] = "a crate of ribbon and paper crossed your counter once, mislabeled as machine parts — you let it pass.",
                    },
                },

                // ===== "The Manifest" overlays (v2 Phase H) =====
                // Slip rules unchanged: a tell in HER voice, never a name,
                // never the fact. The seated's own slips are the strongest
                // signal by design — same deduction game as the press.
                ["sonia.witness"] = new DialogOverlay
                {
                    PersonaAdd = "You watched the evacuation boats load from the treeline, and you count things now — heads, seats, pages — the way other people pray.",
                    SmallTalkAdd = "What the gangway looked like from the trees, and why you never do arithmetic about it out loud.",
                    ChainPendingText =
                        "Nothing left on my list, hunter. You cleared it and dug up my ghosts besides.\n\n" +
                        "But we're not done. Four of us came off that payroll, and what the four of us put in the ground doesn't open for one name.\n\n" +
                        "{onward}\n\n" +
                        "When all three have paid you out, come back. Then we talk about the last door — and about some paper that's been waiting in it.",
                    Slips = new Dictionary<string, string>
                    {
                        ["seated_identity"] = "you counted heads on that gangway twice, and one head you expected to board never did — the count has bothered you for years.",
                    },
                },

                // --- Rebecca -----------------------------------------------
                ["rebecca.seated"] = new DialogOverlay
                {
                    PersonaAdd = "When the manifest pages come up you repeat, evenly, that ground trades never rated seats — the same words every time, like a row learned by heart.",
                    SmallTalkAdd = "How you decide what to take and what to leave when there's only room for one bag.",
                    ChainDoneText = rebeccaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["seated_identity"] = "you know exactly how much a boarding chit weighs in an apron pocket, and you have never once joined the seat arithmetic out loud.",
                    },
                },
                ["rebecca.scratched"] = new DialogOverlay
                {
                    PersonaAdd = "Your name went on the manifest and came off it at the dock — one line of ink through a typed row. You garden like someone who watched the wake fade.",
                    SmallTalkAdd = "What people planted the spring after the boats left, told carefully around the sore parts.",
                    ChainDoneText = rebeccaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["seated_identity"] = "you stood in the dock queue long enough to see the seat column with your own eyes — you know which claims about it are lies.",
                    },
                },
                ["rebecca.clerk"] = new DialogOverlay
                {
                    PersonaAdd = "You typed the manifest the week it was drawn up — rows, seats, names — and you mention the alphabet quickly whenever anyone wonders how the order fell.",
                    SmallTalkAdd = "Typewriters, of all things: stuck keys, carbon paper, how a ribbon dries out.",
                    ChainDoneText = rebeccaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["seated_identity"] = "you typed every row of that list, and when somebody swears she was never on it your hands remember the keys.",
                        ["manifest_leak"] = "there were two carbons under every page you typed, and only one of them ever went in the company file.",
                    },
                },

                // --- Alexa -------------------------------------------------
                ["alexa.seated"] = new DialogOverlay
                {
                    PersonaAdd = "You joke that nobody hands a mechanic a boat ticket while anything on the island still has wheels — the joke lands the same way every time, worn smooth.",
                    SmallTalkAdd = "What you'd have taken in one duffel bag, hypothetically, if it had ever come to that.",
                    ChainDoneText = alexaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["seated_identity"] = "there's a paper chit at the bottom of a toolbox you never open, and you re-torque that latch more than any latch needs.",
                    },
                },
                ["alexa.scratched"] = new DialogOverlay
                {
                    PersonaAdd = "Your name was typed onto the manifest and inked off it at the gangway, and you've hated queues, clipboards and dock foremen with a flat calm ever since.",
                    SmallTalkAdd = "The motor pool crew, by nickname only, and which of them made the rail and which didn't.",
                    ChainDoneText = alexaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["seated_identity"] = "you were close enough to the clipboard to read the seat column upside down, and one even row has never sat right with you.",
                    },
                },
                ["alexa.clerk"] = new DialogOverlay
                {
                    PersonaAdd = "You typed the manifest because the office ran out of clerks and you type fast — and you've been telling yourself the order was alphabetical ever since.",
                    SmallTalkAdd = "Machines with keys and levers; you can strip a typewriter as fast as a carburetor and you don't advertise it.",
                    ChainDoneText = alexaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["seated_identity"] = "you remember which rows you typed twice because somebody upstairs kept changing them, and whose rows those were.",
                        ["manifest_leak"] = "the spare carbon from your typing week never made it into the burn barrel, and you've never asked where it walked to.",
                    },
                },

                // --- Olivia ------------------------------------------------
                ["olivia.seated"] = new DialogOverlay
                {
                    PersonaAdd = "You state, flatly, that QA never rated evacuation seats — tolerances don't board boats. You state it in the same words every time, like a spec sheet.",
                    SmallTalkAdd = "What a fair allocation would have looked like, described entirely in batch numbers and tolerances.",
                    ChainDoneText = oliviaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["seated_identity"] = "there is one document in your foundry you keep at powder-storage humidity, and it is not a powder spec.",
                    },
                },
                ["olivia.scratched"] = new DialogOverlay
                {
                    PersonaAdd = "Your row on the manifest ends in a single line of ink, and you have exact opinions about people who sign things and then cross them out.",
                    SmallTalkAdd = "The difference between a defect and a decision, and which one the dock was.",
                    ChainDoneText = oliviaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["seated_identity"] = "you kept your struck-through row as a quality record, and you've compared enough pages to know whose row has no line at all.",
                    },
                },
                ["olivia.clerk"] = new DialogOverlay
                {
                    PersonaAdd = "You typed the manifest under a foreman's watch — QA hands, no typos, forty rows — and you audit that week in your head more than you'd ever admit.",
                    SmallTalkAdd = "Recordkeeping done properly: duplicates, carbons, and what a complete file owes the people in it.",
                    ChainDoneText = oliviaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["seated_identity"] = "forty rows passed through your platen and you proofed every one — you know the seat column better than anyone still on this island.",
                        ["manifest_leak"] = "a QA clerk always files a duplicate. Always. You are not going to say where.",
                    },
                },

                // ===== "Yours, C." overlays (v2 Phase H.2) =====
                // The three-way roll means slips must stay TRUE whatever the
                // answer is — they corroborate that C.'s paper exists and who
                // handles it, never who signs it. The reveal is scripted
                // (verdict only), per 0002.4.
                ["sonia.named"] = new DialogOverlay
                {
                    PersonaAdd = "C.'s welcome note names YOU as the friendly door, and nobody asked you first. You are polite about it the way you're polite about weather — because arguing changes nothing.",
                    SmallTalkAdd = "What it's like when every stranger already knows your name from a piece of paper, and you know nothing of theirs.",
                    ChainPendingText =
                        "Nothing left on my list, hunter. You cleared it and dug up my ghosts besides.\n\n" +
                        "But we're not done. Four of us came off that payroll, and what the four of us put in the ground doesn't open for one name.\n\n" +
                        "{onward}\n\n" +
                        "When all three have paid you out, come back. Then we talk about the last door — and about an envelope nobody's had the nerve to open.",
                    Slips = new Dictionary<string, string>
                    {
                        ["c_reason"] = "whoever writes those notes owes this island something — you've read enough of them to hear it under the kindness.",
                        ["c_author"] = "every copy of that note still knows to send strangers to a railcar in the green — whoever writes it, or wrote it, kept track of you.",
                    },
                },

                // --- Rebecca -----------------------------------------------
                ["rebecca.keeper"] = new DialogOverlay
                {
                    PersonaAdd = "You keep a bundle of C.'s letters safe — found, you say, years back — and you deflect questions about them gently, the way you'd steer someone off a seedbed.",
                    SmallTalkAdd = "Letters as a habit: who still writes them, what paper keeps, why nobody burns a kind word even when they should.",
                    ChainDoneText = rebeccaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["c_author"] = "there's a biscuit tin in your cellar with more paper than biscuits in it, tied in garden twine — and the twine gets re-tied fresher than 'found years back' explains.",
                        ["c_reason"] = "you've read every letter in that tin more than once, and you believe you know what C. is paying down — kindness that regular is always paying something down.",
                    },
                },
                ["rebecca.denier"] = new DialogOverlay
                {
                    PersonaAdd = "You tell anyone who asks that C. is long gone and the notes are just old paper still circulating — firmly, kindly, and always in the same words.",
                    SmallTalkAdd = "How things persist after the people who made them: seed lines, footpaths, habits — and, yes, letters.",
                    ChainDoneText = rebeccaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["c_author"] = "you say C. is gone with the certainty of someone who checked — and you have never once said HOW you checked.",
                    },
                },
                ["rebecca.collector"] = new DialogOverlay
                {
                    PersonaAdd = "You trade newcomers a meal or a seed packet for their welcome notes, and you press each one flat in a drawer like a specimen. It's not sentiment. It's evidence.",
                    SmallTalkAdd = "The little differences between welcome notes: margins, ribbon wear, the season the paper changed.",
                    ChainDoneText = rebeccaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["c_author"] = "the ribbon face on the welcome notes has changed twice since you started collecting — dead hands don't change ribbons, and you've drawn your conclusions.",
                        ["c_reason"] = "laid side by side, the notes get gentler year on year — whatever C. owes, you'd say the debt is winning.",
                    },
                },

                // --- Alexa -------------------------------------------------
                ["alexa.keeper"] = new DialogOverlay
                {
                    PersonaAdd = "You keep a stack of C.'s letters in the one drawer of your toolchest that locks — found them, you say, and the subject closes like a hood.",
                    SmallTalkAdd = "Why a person keeps paper that isn't theirs, asked hypothetically and answered badly.",
                    ChainDoneText = alexaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["c_author"] = "the locked drawer in your chest gets oiled more than any lock in the bay — and whatever's in there, you 'found' it the way a magpie finds things.",
                        ["c_reason"] = "you've read the letters enough to know C. isn't writing out of friendliness — nobody services an obligation that regularly unless it's structural.",
                    },
                },
                ["alexa.denier"] = new DialogOverlay
                {
                    PersonaAdd = "Your line is that C. is gone — rolled off this island years back — and that people who romanticize old paper should try fixing something instead.",
                    SmallTalkAdd = "Things this island believes that aren't so, ranked by how much they annoy you.",
                    ChainDoneText = alexaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["c_author"] = "for someone who says C. is gone, you go quiet at the exact same beat in every conversation about the notes — like an engine missing on one cylinder, same cylinder every time.",
                    },
                },
                ["alexa.collector"] = new DialogOverlay
                {
                    PersonaAdd = "You buy welcome notes off newcomers for spare parts and you've got them clipped to a board like work orders — you say you're 'diagnosing' the typewriter.",
                    SmallTalkAdd = "What a machine tells on its owner: wear patterns, alignment, the letters that strike heaviest.",
                    ChainDoneText = alexaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["c_author"] = "the machine that types those notes drags its lowercase e — you'd know that typewriter's voice anywhere on this island, if you ever heard it working.",
                        ["c_reason"] = "the notes always tell a stranger where to be SAFE first and useful second — that's not recruiting, that's somebody making amends.",
                    },
                },

                // --- Olivia ------------------------------------------------
                ["olivia.keeper"] = new DialogOverlay
                {
                    PersonaAdd = "You hold a sheaf of C.'s letters in the foundry's document box — found, you state, and stored at proper humidity, because paper is paper regardless of provenance.",
                    SmallTalkAdd = "Proper archival practice, recited with slightly more feeling than the subject warrants.",
                    ChainDoneText = oliviaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["c_author"] = "you file C.'s letters by DATE RECEIVED, in your own hand — an odd field to be so precise about, for paper you only ever 'found' once.",
                        ["c_reason"] = "the letters read to you like an audit trail — someone documenting, payment by payment, a debt the company's books never carried.",
                    },
                },
                ["olivia.denier"] = new DialogOverlay
                {
                    PersonaAdd = "Your position is on file: C. is gone, the notes are legacy stock circulating on their own momentum, and speculation is a defect of discipline.",
                    SmallTalkAdd = "The difference between evidence and momentum, with examples that are never about C.",
                    ChainDoneText = oliviaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["c_author"] = "you declared C. 'gone' the way QA declares a batch closed — which anyone who worked your line knows means the paperwork exists, somewhere, signed.",
                    },
                },
                ["olivia.collector"] = new DialogOverlay
                {
                    PersonaAdd = "You pay newcomers scrap for their welcome notes and log each one like an incoming batch — sample, compare, file. You do not explain the project and nobody asks twice.",
                    SmallTalkAdd = "Batch comparison as a way of knowing things: what changes, what holds, what that proves.",
                    ChainDoneText = oliviaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["c_author"] = "your note log shows fresh ink in unbroken sequence, every season, no gap — whatever the island says about C., your ledger says CONTINUITY.",
                        ["c_reason"] = "line up ten years of those notes and the same apology is under all of them, worded forty ways — you've stopped wondering IF C. owes and started wondering what.",
                    },
                },

                // ===== "The Recall" overlays (v2 Phase H.3) =====
                // Olivia holds the fixed role; Sonia rolls with the others
                // for the first time. digger_identity is never revealed by
                // script — these slips are the ONLY thread, so each digger
                // tell is physical and checkable, and buyer/spotter tells
                // corroborate without pointing at a biome.
                ["olivia.examiner"] = new DialogOverlay
                {
                    PersonaAdd = "The stamped casings coming off the ground are YOUR sign-offs coming home. You keep a jar of them on the counter and you read headstamps the way other people read faces — and you want the digging stopped.",
                    SmallTalkAdd = "Batch numbers: what a headstamp tells you about the year, the line, the shift, and the shelf it slept on.",
                    ChainDoneText = oliviaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["digger_identity"] = "the batches coming back span one shelf of one magazine — whoever is digging goes back to the same hole, on a schedule, like a customer.",
                        ["magazine_state"] = "you signed the original inventory for that magazine, and you could recite what SHOULD still be down there in your sleep.",
                    },
                },

                // --- Sonia (rolled, a first — the vault line rides along) ---
                ["sonia.digger"] = new DialogOverlay
                {
                    PersonaAdd = "You say your ammunition stock is honest salvage, barrel to bullet — the same words every time, delivered while checking a trap that doesn't need checking.",
                    SmallTalkAdd = "Ground that lies: which hillsides sound wrong underfoot, spoken of a little too knowledgeably.",
                    ChainPendingText =
                        "Nothing left on my list, hunter. You cleared it and dug up my ghosts besides.\n\n" +
                        "But we're not done. Four of us came off that payroll, and what the four of us put in the ground doesn't open for one name.\n\n" +
                        "{onward}\n\n" +
                        "When all three have paid you out, come back. Then we talk about the last door — and about a chart that's been rolled up in it too long.",
                    Slips = new Dictionary<string, string>
                    {
                        ["digger_identity"] = "your snare lines run past the same fenced hillside every week, and you have never once set a snare there.",
                        ["magazine_state"] = "you know how much is left under that hillside to the crate, though you'd bite your tongue off before saying how you know.",
                    },
                },
                ["sonia.buyer"] = new DialogOverlay
                {
                    PersonaAdd = "You bought a crate of rifle rounds this season at a price that should have made you ask questions, and didn't. You've been generous with ammunition lately, the way guilty people are generous.",
                    SmallTalkAdd = "What a fair price for ammunition is, argued with slightly too much energy.",
                    ChainPendingText =
                        "Nothing left on my list, hunter. You cleared it and dug up my ghosts besides.\n\n" +
                        "But we're not done. Four of us came off that payroll, and what the four of us put in the ground doesn't open for one name.\n\n" +
                        "{onward}\n\n" +
                        "When all three have paid you out, come back. Then we talk about the last door — and about a chart that's been rolled up in it too long.",
                    Slips = new Dictionary<string, string>
                    {
                        ["digger_identity"] = "the crate you bought was cheaper than honest and heavier than salvage, and the seller's hands had fresh rope burn — you've stopped letting yourself finish that thought.",
                    },
                },
                ["sonia.spotter"] = new DialogOverlay
                {
                    PersonaAdd = "You've been picking stamped casings out of the leaf litter all season and marking where, because tracking is tracking whether it walks on legs or gets carried in crates.",
                    SmallTalkAdd = "Reading ground: drag marks, boot depth, what a heavy load does to a trail's edges.",
                    ChainPendingText =
                        "Nothing left on my list, hunter. You cleared it and dug up my ghosts besides.\n\n" +
                        "But we're not done. Four of us came off that payroll, and what the four of us put in the ground doesn't open for one name.\n\n" +
                        "{onward}\n\n" +
                        "When all three have paid you out, come back. Then we talk about the last door — and about a chart that's been rolled up in it too long.",
                    Slips = new Dictionary<string, string>
                    {
                        ["digger_identity"] = "your pin map has a center of gravity, and you've caught yourself avoiding one counter since you noticed where it sits.",
                    },
                },

                // --- Rebecca -----------------------------------------------
                ["rebecca.digger"] = new DialogOverlay
                {
                    PersonaAdd = "You say everything you sell is honest salvage, accounted crate by crate — the same words every time, while your hands find something to scrub.",
                    SmallTalkAdd = "Root cellars and shoring: how much timber it takes to hold a ceiling up, quoted from memory.",
                    ChainDoneText = rebeccaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["digger_identity"] = "there's fresh shoring timber in your cellar that never held a single preserve jar, and lamp oil going missing by the quart.",
                        ["magazine_state"] = "you water nothing on one corner of your land, and you know to the season how long that corner will keep paying.",
                    },
                },
                ["rebecca.buyer"] = new DialogOverlay
                {
                    PersonaAdd = "You bought a crate you shouldn't have — cheap rounds for the perimeter, no questions — and you've been overfeeding everyone who visits ever since, the way worry cooks.",
                    SmallTalkAdd = "What it costs to keep a homestead defended, and whether thrift can be a sin.",
                    ChainDoneText = rebeccaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["digger_identity"] = "the crate came to you with subsoil still packed in its handles — dug ground, not scavenged ground — and you knew the difference and paid anyway.",
                    },
                },
                ["rebecca.spotter"] = new DialogOverlay
                {
                    PersonaAdd = "You've been finding stamped casings in your hedgerows and marking each one on a seed chart, because a tidy record is how you keep fear useful.",
                    SmallTalkAdd = "What turns up in soil that shouldn't: brass, bones, and where exactly.",
                    ChainDoneText = rebeccaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["digger_identity"] = "your seed chart's casing marks thin out the closer they get to one stretch of the island, the way footprints thin out near a den.",
                    },
                },

                // --- Alexa -------------------------------------------------
                ["alexa.digger"] = new DialogOverlay
                {
                    PersonaAdd = "You say your ammunition is honest salvage, every crate accountable — same words every time, delivered from under a truck that's been 'almost fixed' for a month.",
                    SmallTalkAdd = "Winches and load ratings, which you know suspiciously precisely for someone who fixes engines.",
                    ChainDoneText = alexaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["digger_identity"] = "the winch on your truck is rated for crates, re-greased weekly since spring, and the tire treads pack a soil your bay's never sat on.",
                        ["magazine_state"] = "you keep a tally scratched inside the toolchest lid — crates out, crates left — and you flip the lid shut fast when anyone leans in.",
                    },
                },
                ["alexa.buyer"] = new DialogOverlay
                {
                    PersonaAdd = "You took a crate of rounds in part-trade for engine work and asked exactly zero questions, which you'd tell anyone is standard practice, twice, unprompted.",
                    SmallTalkAdd = "Barter etiquette: when asking where something came from is rude, and when it's overdue.",
                    ChainDoneText = alexaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["digger_identity"] = "whoever paid you in ammunition wanted the muffler done QUIET and the job done at night, and you billed extra instead of thinking about it.",
                    },
                },
                ["alexa.spotter"] = new DialogOverlay
                {
                    PersonaAdd = "You've got a board in the bay with casings nailed up by where they were found, like parts off a mystery engine — you're reverse-engineering a supply line and you know it.",
                    SmallTalkAdd = "Failure analysis: start from what's on the ground, work back to what must be moving.",
                    ChainDoneText = alexaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["digger_identity"] = "your casing board plots a route, the route has a home end, and you've stopped updating the board where anyone can watch you do it.",
                    },
                },

                // ===== "The Count" overlays (v2 Phase H.4) =====
                // Rebecca holds the fixed role; the rolled three land on
                // Sonia, Alexa, Olivia — all of them, every Count wipe. The
                // tallier's slips corroborate that she counts, never what
                // the count IS (true_count's tier-3 slips hint at the gap).
                ["rebecca.timekeeper"] = new DialogOverlay
                {
                    PersonaAdd = "You kept the greenhouse light cycles and you never stopped keeping time — frost dates, sunrise drift, planting counts. The island's calendar lives in your head, and it weighs what calendars weigh.",
                    SmallTalkAdd = "How you actually keep time out here: frost, bud-break, the sun's angle over the water tower — and what people really mean when they ask the date.",
                    ChainDoneText = rebeccaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["tallier_identity"] = "somebody trades you the same odd question every few weeks — 'what's today's frost count' — and never once plants anything after asking.",
                        ["true_count"] = "your planting records go back further than the pamphlets' tally does, and the two lines don't meet where they should.",
                    },
                },

                // --- Sonia -------------------------------------------------
                ["sonia.tallier"] = new DialogOverlay
                {
                    PersonaAdd = "You call the wipe-counting morbid superstition — grave-digging with arithmetic — in the flat tone of a line rehearsed against being asked twice.",
                    SmallTalkAdd = "Why hunters don't count seasons, delivered with slightly too much theory behind it.",
                    ChainPendingText =
                        "Nothing left on my list, hunter. You cleared it and dug up my ghosts besides.\n\n" +
                        "But we're not done. Four of us came off that payroll, and what the four of us put in the ground doesn't open for one name.\n\n" +
                        "{onward}\n\n" +
                        "When all three have paid you out, come back. Then we talk about the last door — and about a board none of us has looked at in years.",
                    Slips = new Dictionary<string, string>
                    {
                        ["tallier_identity"] = "there are notches on your railcar's doorframe, high up where nobody reaches by accident, and the newest one is recent.",
                        ["true_count"] = "your notches and the pamphlets' number aren't the same, and knowing WHICH is wrong is the thing you won't buy back with sleep.",
                    },
                },
                ["sonia.eraser"] = new DialogOverlay
                {
                    PersonaAdd = "Where you find tally marks you scrub them off — rocks, walls, tree bark — openly, like clearing deadfall from a trail. Grief needs endings, not arithmetic.",
                    SmallTalkAdd = "What a trail looks like when someone can't stop walking it, and why you break those trails on purpose.",
                    ChainPendingText =
                        "Nothing left on my list, hunter. You cleared it and dug up my ghosts besides.\n\n" +
                        "But we're not done. Four of us came off that payroll, and what the four of us put in the ground doesn't open for one name.\n\n" +
                        "{onward}\n\n" +
                        "When all three have paid you out, come back. Then we talk about the last door — and about a board none of us has looked at in years.",
                    Slips = new Dictionary<string, string>
                    {
                        ["tallier_identity"] = "one set of marks you scrub keeps coming back within days, fresh-cut and precise — you're in a quiet argument with a very patient hand.",
                    },
                },
                ["sonia.forgetter"] = new DialogOverlay
                {
                    PersonaAdd = "Seasons blur at the edges for you worse than you admit — some mornings arrive carrying too many winters, and you go check your traps just to have something that counts cleanly.",
                    SmallTalkAdd = "The tricks a tracker uses when memory won't hold: landmarks, habits, things the hands remember that the head lost.",
                    ChainPendingText =
                        "Nothing left on my list, hunter. You cleared it and dug up my ghosts besides.\n\n" +
                        "But we're not done. Four of us came off that payroll, and what the four of us put in the ground doesn't open for one name.\n\n" +
                        "{onward}\n\n" +
                        "When all three have paid you out, come back. Then we talk about the last door — and about a board none of us has looked at in years.",
                    Slips = new Dictionary<string, string>
                    {
                        ["tallier_identity"] = "when the count comes up you've caught yourself glancing at the same person's hands, the way you'd check a compass you trust more than your own bearings.",
                    },
                },

                // --- Alexa -------------------------------------------------
                ["alexa.tallier"] = new DialogOverlay
                {
                    PersonaAdd = "You say counting resets is morbid superstition and a waste of good chalk — same words every time, said while wiping your hands on the same rag.",
                    SmallTalkAdd = "Odometers, hour meters, and why machines get to count when people shouldn't.",
                    ChainDoneText = alexaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["tallier_identity"] = "there's an hour-meter on your bench wired to nothing, and its number goes up by exactly one on mornings after the world feels rearranged.",
                        ["true_count"] = "your meter and the pamphlets' sheet disagree, and you've rebuilt the meter twice looking for the fault before admitting meters don't lie.",
                    },
                },
                ["alexa.eraser"] = new DialogOverlay
                {
                    PersonaAdd = "You grind tally marks off whatever they're cut into — 'defacing my island', you call it — and you keep a wire brush on your belt for exactly that.",
                    SmallTalkAdd = "Surface prep, honestly: how to take a mark off steel, stone or paint, and leave nothing.",
                    ChainDoneText = alexaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["tallier_identity"] = "you could name the counter-scratcher by tool marks alone — the same blade, stropped sharp, every time — you just haven't matched the blade to a belt yet.",
                    },
                },
                ["alexa.forgetter"] = new DialogOverlay
                {
                    PersonaAdd = "The stretches between resets slip through you like a stripped thread — you date everything by which engine was on the lift, and lately the engines argue with each other.",
                    SmallTalkAdd = "Dating memories by machines: which rig, which rebuild, which noise it made — and what to do when two memories claim the same engine.",
                    ChainDoneText = alexaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["tallier_identity"] = "when you lose the thread you ask one particular person a 'casual' question about seasons, and her answer always comes too exact to be casual.",
                    },
                },

                // --- Olivia ------------------------------------------------
                ["olivia.tallier"] = new DialogOverlay
                {
                    PersonaAdd = "You state that counting the island's resets is superstition unbecoming of adults — flatly, identically, every time — which is itself a kind of record-keeping.",
                    SmallTalkAdd = "What deserves counting and what doesn't, argued like a specification.",
                    ChainDoneText = oliviaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["tallier_identity"] = "there is a row of punch marks inside your furnace-room doorframe, spaced like a gauge, and the punch lives in your apron pocket.",
                        ["true_count"] = "your marks disagree with the published sheet, and disagreement between two records is, in your experience, never the instrument's fault.",
                    },
                },
                ["olivia.eraser"] = new DialogOverlay
                {
                    PersonaAdd = "You file tally marks off any surface you own and some you don't — defects in the material, you call them. Records belong in ledgers, and grief doesn't get a ledger.",
                    SmallTalkAdd = "The difference between a record and a scar, stated once, precisely, and not revisited.",
                    ChainDoneText = oliviaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["tallier_identity"] = "one hand's marks keep reappearing after you file them — cut with a QA striker's discipline, which narrows the island considerably and you know it.",
                    },
                },
                ["olivia.forgetter"] = new DialogOverlay
                {
                    PersonaAdd = "Your memory of the stretches between resets has gaps you can measure — batches you remember pouring that no ledger records. For a QA hand, that is the frightening kind of defect.",
                    SmallTalkAdd = "Reconciling a memory against a ledger and finding the ledger short — spoken of in batch numbers, never feelings.",
                    ChainDoneText = oliviaClosing,
                    Slips = new Dictionary<string, string>
                    {
                        ["tallier_identity"] = "when your own records gap, you know whose count you'd trust to backfill them, and it isn't the pamphlets'.",
                    },
                },
            };
        }

        private void LoadOverlays()
        {
            try { _overlays = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, DialogOverlay>>($"{DataRoot}/overlays"); }
            catch (Exception e)
            {
                PrintError($"overlays.json failed to parse ({e.Message}) — running without overlays, FILE LEFT UNTOUCHED for repair.");
                _overlays = new Dictionary<string, DialogOverlay>();
                return;
            }
            if (_overlays == null)
            {
                _overlays = DefaultOverlays();
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/overlays", _overlays);
            }
            else
            {
                var added = 0;
                foreach (var kv in DefaultOverlays())
                    if (!_overlays.ContainsKey(kv.Key)) { _overlays[kv.Key] = kv.Value; added++; }
                if (added > 0)
                {
                    Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/overlays", _overlays);
                    DLog($"Added {added} dialog overlay(s) to overlays.json.");
                }
            }
        }

        // The overlay in force for this trader right now, or null.
        private DialogOverlay ActiveOverlay(string traderKey)
        {
            if (_state == null || _state.Bible == null || _overlays.Count == 0) return null;
            string role;
            if (!_state.Bible.Roles.TryGetValue(traderKey, out role) || string.IsNullOrEmpty(role)) return null;
            DialogOverlay ov;
            return _overlays.TryGetValue($"{traderKey}.{role}", out ov) ? ov : null;
        }

        // Overlay text wins when authored; base text otherwise.
        private static string Ov(string overlayText, string baseText) =>
            string.IsNullOrEmpty(overlayText) ? baseText : overlayText;

        // Fourth member of the sync family: pulls shipped overlays over
        // overlays.json by key (deliberate discard for what it touches).
        [ConsoleCommand("rq.overlay.synctext")]
        private void CmdOverlaySyncText(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var only = arg.HasArgs() ? arg.GetString(0) : null; // "<trader>.<role>", or all
            var synced = 0;
            var added = 0;
            foreach (var kv in DefaultOverlays())
            {
                if (only != null && kv.Key != only) continue;
                if (_overlays.ContainsKey(kv.Key)) synced++; else added++;
                _overlays[kv.Key] = kv.Value;
            }
            if (synced + added > 0)
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/overlays", _overlays);
            arg.ReplyWith(synced + added == 0
                ? $"No matching shipped overlay{(only != null ? $" '{only}'" : "")} found."
                : $"Synced {synced} and added {added} overlay(s) from the plugin's shipped set. Live edits to those keys are gone (that's the contract).");
        }

        private TemplateDef ActiveTemplate() =>
            _state != null && _state.Bible != null ? FindTemplate(_state.Bible.TemplateId) : null;

        // --- migration (v1 -> v2 data files) ------------------------------
        // The (Id,Tree,Arc) merge key means un-stamped live entries would
        // mismatch the arc-tagged defaults and every quest would duplicate on
        // first load. So before the first v2 merge: back the file up, stamp
        // Arc onto every entry the shipped defaults know, leave operator-
        // authored quests Arc=null (= active every wipe, their v1 behavior).
        private void MigrateDataFiles()
        {
            if (_state.DataVersion >= 2) return;
            List<QuestDef> defs = null;
            try { defs = Interface.Oxide.DataFileSystem.ReadObject<List<QuestDef>>($"{DataRoot}/quests"); }
            catch (Exception e)
            {
                // Broken file: never write, never bump the version — the
                // migration retries on the next load after the repair.
                PrintError($"v2 migration: quests.json failed to parse ({e.Message}) — migration deferred, FILE LEFT UNTOUCHED.");
                return;
            }
            if (defs == null || defs.Count == 0)
            {
                _state.DataVersion = 2;
                SaveState();
                DLog("v2 migration: no live quests.json to stamp — DataVersion set to 2.");
                return;
            }
            Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/quests.pre-v2-backup", defs);
            var stamped = MigrateStampArcs(defs);
            if (stamped > 0)
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/quests", defs);
            _state.DataVersion = 2;
            SaveState();
            DLog($"v2 migration: stamped Arc on {stamped} quest(s) (backup: {DataRoot}/quests.pre-v2-backup.json). DataVersion 2.");
        }

        private int MigrateStampArcs(List<QuestDef> defs)
        {
            // First-wins: slot variants share an Id with their classic (Phase
            // E) and are authored after it — a pre-v2 file being stamped
            // today must land on the classic's arc, never a variant's.
            var arcByKey = new Dictionary<string, string>();
            foreach (var def in DefaultQuests())
                if (def.Arc != null && !arcByKey.ContainsKey($"{def.Id}#{def.Tree}")) arcByKey[$"{def.Id}#{def.Tree}"] = def.Arc;
            var stamped = 0;
            foreach (var q in defs)
            {
                if (q == null || q.Arc != null) continue;
                string arc;
                if (arcByKey.TryGetValue($"{q.Id}#{q.Tree}", out arc)) { q.Arc = arc; stamped++; }
            }
            return stamped;
        }

        // --- admin surface -------------------------------------------------

        [ConsoleCommand("rq.bible")]
        private void CmdBible(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var b = _state != null ? _state.Bible : null;
            if (b == null) { arg.ReplyWith("No wipe bible yet — it rolls on the first boot of a wipe (or rq.bible.reroll)."); return; }
            var showSecrets = arg.HasArgs() && arg.GetString(0) == "secrets";
            var sb = new StringBuilder();
            sb.Append($"Wipe bible — template '{b.TemplateId}', seed {b.Seed}, wipe {_state.WipeCounter}, tree {_state.QuestTree}, day {RealDaysSinceWipeStart():0.#}\n");
            sb.Append($"  started: {b.WipeStartUtc}\n");
            sb.Append($"  {SeasonStatusLine()}\n");
            sb.Append($"  chain:   {string.Join(" -> ", b.ChainOrder)}\n");
            sb.Append($"  arcs:    {(b.ActiveArcs.Count > 0 ? string.Join(", ", b.ActiveArcs) : "(none)")}\n");
            if (b.VariantPicks.Count > 0)
            {
                sb.Append("  slots:   ");
                var firstPick = true;
                foreach (var kv in b.VariantPicks) { if (!firstPick) sb.Append(", "); sb.Append($"{kv.Key}={kv.Value}"); firstPick = false; }
                sb.Append('\n');
            }
            foreach (var kv in b.TurninRolls)
                sb.Append($"  turn-in {kv.Key}: {kv.Value.Count} x {kv.Value.Item}\n");
            if (b.FieldNoteNumber > 0)
                sb.Append($"  field notes: №{b.FieldNoteNumber} (used: {string.Join(", ", _state.Memory?.UsedFieldNoteNumbers ?? new List<int>())})\n");
            if (b.Roles.Count > 0)
            {
                sb.Append("  roles:   ");
                var first = true;
                foreach (var kv in b.Roles) { if (!first) sb.Append(", "); sb.Append($"{kv.Key}={kv.Value}"); first = false; }
                sb.Append('\n');
            }
            // The admin plays here too — values stay behind an extra arg.
            if (b.Secrets.Count > 0)
            {
                if (showSecrets)
                {
                    sb.Append("  secrets: ");
                    var first = true;
                    foreach (var kv in b.Secrets) { if (!first) sb.Append(", "); sb.Append($"{kv.Key}={kv.Value}"); first = false; }
                    sb.Append('\n');
                }
                else sb.Append($"  secrets: {b.Secrets.Count} rolled (spoilers — 'rq.bible secrets' to show)\n");
            }
            foreach (var kv in b.Monuments)
                sb.Append($"  monument {kv.Key}: {kv.Value.DisplayName} @ {MapHelper.PositionToString(kv.Value.Pos)}\n");
            foreach (var beat in b.Beats)
                sb.Append($"  beat '{beat.Id}': arc {beat.Arc}, day {beat.DueRealDay}{(beat.RequiresQuest != null ? $", after {beat.RequiresQuest}" : "")}{(beat.Fired ? " [fired]" : "")}\n");
            if (b.Choice != null)
            {
                sb.Append($"  choice '{b.Choice.Id}': first pick {(b.Choice.FirstPick ?? "(none yet)")}");
                if (b.Choice.Counts.Count > 0)
                {
                    sb.Append(" — votes: ");
                    var firstCount = true;
                    foreach (var kv in b.Choice.Counts) { if (!firstCount) sb.Append(", "); sb.Append($"{kv.Key}={kv.Value}"); firstCount = false; }
                }
                sb.Append('\n');
            }
            if (_state.Memory != null && !string.IsNullOrEmpty(_state.Memory.LastTemplateId))
                sb.Append($"  last wipe: template '{_state.Memory.LastTemplateId}'{(_state.Memory.LastChoiceOutcome != null ? $", choice '{_state.Memory.LastChoiceOutcome}'" : "")}\n");
            arg.ReplyWith(sb.ToString());
        }

        // Dev/test: re-roll the story. Mid-wipe this can strand progress made
        // on arcs that stop existing — the drill tool, not a live lever.
        [ConsoleCommand("rq.bible.reroll")]
        private void CmdBibleReroll(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var force = arg.HasArgs() ? arg.GetString(0) : null;
            _state.Bible = null;
            try { GenerateWipeBible(true, force); }
            catch (Exception e)
            {
                PrintError($"Bible reroll failed ({e.Message}) — falling back to four_keys.\n{e}");
                _state.Bible = FallbackBible();
            }
            ApplyBible();
            SaveState();
            try { EnsureAllFinaleStashes(); EnsureAllDrops(); EnsurePlacedNotes(); }
            catch (Exception e) { PrintWarning($"World placement after reroll failed ({e.Message}) — rq.stash.reroll to retry."); }
            RecomputeEngineHooks();
            RecomputeGrudgeHooks(); // a reroll can change who holds what against whom
            BeatTick();          // a rerolled story may schedule beats already past due
            RecomputeBeatTimer();
            RecomputePieceTimer();
            if (_crewActive.Count > 0) EndVisit(false); // a rerolled story recalls its guns
            RecomputeCrewTimer();
            arg.ReplyWith($"Wipe bible rerolled: template '{_state.Bible.TemplateId}', chain {string.Join(" -> ", _state.Bible.ChainOrder)}. " +
                          "Same seed = same roll unless you forced a template. Mid-wipe, players on now-inactive arcs are stranded until the next real wipe — dev use only.");
        }

        // Replaces live entries with the shipped ones by Id (deliberate
        // discard, same contract as rq.quest.syncrewards) and adds anything new.
        [ConsoleCommand("rq.template.sync")]
        private void CmdTemplateSync(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var only = arg.HasArgs() ? arg.GetString(0) : null;
            var synced = 0;
            var added = 0;
            foreach (var def in DefaultTemplates())
            {
                if (only != null && def.Id != only) continue;
                var idx = _templates.FindIndex(t => t != null && t.Id == def.Id);
                if (idx >= 0) { _templates[idx] = def; synced++; }
                else { _templates.Add(def); added++; }
            }
            if (synced > 0 || added > 0)
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/templates", _templates);
            arg.ReplyWith(synced + added == 0
                ? $"No matching template{(only != null ? $" '{only}'" : "")} found."
                : $"Synced {synced} and added {added} template(s) from the plugin's shipped set. Live edits to those templates are gone (that's the contract). The bible re-reads templates on the next roll, not retroactively.");
        }

        [ConsoleCommand("rq.migrate.check")]
        private void CmdMigrateCheck(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (_state.DataVersion >= 2)
            {
                var tagged = 0;
                foreach (var q in _quests) if (q.Arc != null) tagged++;
                arg.ReplyWith($"Already migrated (DataVersion {_state.DataVersion}). {tagged}/{_quests.Count} quests carry an Arc tag; " +
                              $"pre-migration backup at oxide/data/{DataRoot}/quests.pre-v2-backup.json if it ran against a live file.");
                return;
            }
            List<QuestDef> defs = null;
            try { defs = Interface.Oxide.DataFileSystem.ReadObject<List<QuestDef>>($"{DataRoot}/quests"); }
            catch (Exception e) { arg.ReplyWith($"quests.json failed to parse ({e.Message}) — migration would be deferred until the file is repaired."); return; }
            if (defs == null || defs.Count == 0) { arg.ReplyWith("No live quests.json — migration would only set DataVersion to 2."); return; }
            var wouldStamp = MigrateStampArcs(defs); // dry run: the list is a fresh parse, nothing is written
            arg.ReplyWith($"DRY RUN — migration would back up quests.json and stamp Arc on {wouldStamp} of {defs.Count} quest(s); " +
                          "entries the shipped defaults don't know keep Arc=null (active every wipe). Nothing was written.");
        }

        #endregion

        #region Data — quest definitions

        private class QuestDef
        {
            public string Id;
            public string Giver = "sonia";
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string Tree;                // null = any wipe; "jungle"/"temperate" = only that tree
            // v2 story-arc tag: null = active every wipe (exactly like Tree),
            // else active only when the wipe bible lists it. Variants of one
            // chain slot share an Id and differ by Arc — only one is ever
            // active, so RequiresQuest keeps pointing at stable ids.
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string Arc;
            public string Name;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string RequiresQuest;
            // Every one of these must be completed too (the cross-trader vault
            // finale needs all four chains done).
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore, ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> RequiresQuests;
            public int AcceptCostScrap;
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<RewardDef> GrantOnAccept = new List<RewardDef>();
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ObjectiveDef> Objectives = new List<ObjectiveDef>();
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<RewardDef> Rewards = new List<RewardDef>();
            public string OfferText = "";
            public string CompleteText = "";
            // Set by BuildQuestIndex each wipe: a picked variant with this Id
            // retired this def. Never serialized — shadowing is bible state.
            [JsonIgnore] public bool ShadowedThisWipe;
        }

        private class ObjectiveDef
        {
            public string Type;                // kill | turnin | visit | readnote | code | drop | plant
            public string Target;              // kill: Categorize() species, '|' = any-of; turnin: item shortname; visit: "x y z"; readnote/code: tag; drop: drop-box tag (usually a monument binding key); plant: unused
            public int Count = 1;              // drop: items to deposit; plant: distinct monuments
            public float Radius = 6f;          // visit only
            // drop only: item shortname to deposit in the box.
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string Item;
            // v2 Phase E, turnin only: name of a turn-in pool in the active
            // template. When the bible carries a roll for this objective, the
            // rolled Item/Count override Target/Count at every READ — the def
            // is never mutated, because the sync commands write _quests back
            // to quests.json and a mutation would leak the roll into the file.
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string Pool;
            public string Text = "";
            // Pre-split at load: OnEntityDeath runs for every kill on the server,
            // and Split+Trim there allocated on each one.
            [JsonIgnore] public string[] TargetsCached;
        }

        private class RewardDef
        {
            public string Item;
            public int Amount = 1;
            public ulong Skin;
            // Millilitres of water loaded into EACH granted container item — a
            // full jug instead of an empty one. Clamped to what the container
            // actually holds (a water jug caps at 5000 ml), so `waterjug` x4
            // with Water 5000 is how you hand over 20 000. Omitted from
            // quests.json entirely when 0.
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int Water;
            // Weapon mods seated in EACH granted weapon (same contents mechanism
            // as Water — a weapon is a container item whose contents hold its
            // attachments). A mod the weapon can't take, or won't fit once the
            // slots are full, is handed over loose instead of vanishing.
            // Shortname trap: the Variable Zoom Scope is `weapon.mod.8x.scope`
            // and `weapon.mod.small.scope` is the 8x — inverted display names.
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore, ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Attachments;
            // v2 Phase H.5: for Item "note" — stock the granted note with this
            // notes.json entry (title + text, tokens expanded). How quests
            // hand out lore as loot: the recovered FIELD NOTES archives.
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string NoteTag;
        }

        private List<QuestDef> _quests = new List<QuestDef>();
        private static readonly string[] KnownObjectiveTypes = { "kill", "turnin", "visit", "readnote", "code", "drop", "plant" };

        private void LoadQuestDefs()
        {
            List<QuestDef> defs = null;
            var parseFailed = false;
            try { defs = Interface.Oxide.DataFileSystem.ReadObject<List<QuestDef>>($"{DataRoot}/quests"); }
            catch (Exception e)
            {
                // Never overwrite a broken file — the operator's hand-tuned
                // rewards and dialog are irreplaceable; run on defaults instead
                // so they can fix the typo and reload (same rule as LoadConfig).
                parseFailed = true;
                PrintError($"quests.json failed to parse ({e.Message}) — running on built-in defaults, FILE LEFT UNTOUCHED for repair.");
            }
            if (parseFailed) { _quests = DefaultQuests(); BuildQuestIndex(); return; }
            if (defs == null || defs.Count == 0)
            {
                defs = DefaultQuests();
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/quests", defs);
                DLog($"Wrote default quest definitions ({defs.Count} quests).");
            }
            else
            {
                // Merge in newly authored quests (by Id+Tree+Arc) without touching
                // anything already in the file — live tuning survives updates.
                // Arc joined the key in v2 (variants share an Id); MigrateDataFiles
                // stamps live entries BEFORE this runs, or everything would double.
                var added = 0;
                foreach (var def in DefaultQuests())
                {
                    if (defs.FindIndex(d => d.Id == def.Id && d.Tree == def.Tree && d.Arc == def.Arc) >= 0) continue;
                    defs.Add(def);
                    added++;
                }
                if (added > 0)
                {
                    Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/quests", defs);
                    DLog($"Added {added} new quest definition(s) to quests.json (existing entries untouched).");
                }
            }

            // Validation — bad content should complain loudly at load, not at claim time.
            // Shipped defaults keyed for the pooled-objective live-edit check:
            // a tuned Target/Count on a pooled objective is dead tuning (the
            // bible's roll wins) — point the operator at the pool instead.
            var shippedByKey = new Dictionary<string, QuestDef>();
            foreach (var d in DefaultQuests()) shippedByKey[$"{d.Id}#{d.Tree}#{d.Arc}"] = d;
            var seen = new HashSet<string>();
            foreach (var q in defs)
            {
                if (string.IsNullOrEmpty(q.Id)) { PrintWarning("Quest with empty Id skipped."); continue; }
                if (!seen.Add($"{q.Id}#{q.Tree}#{q.Arc}")) PrintWarning($"Duplicate quest id '{q.Id}' (tree {q.Tree ?? "any"}, arc {q.Arc ?? "any"}).");
                var pooled = 0;
                for (var oi = 0; oi < q.Objectives.Count; oi++)
                {
                    var o = q.Objectives[oi];
                    if (Array.IndexOf(KnownObjectiveTypes, o.Type) < 0)
                        PrintWarning($"Quest '{q.Id}': unknown objective type '{o.Type}'.");
                    if (o.Type == "drop" && (string.IsNullOrEmpty(o.Item) || ItemManager.FindItemDefinition(o.Item) == null))
                        PrintWarning($"Quest '{q.Id}': drop objective needs a valid Item shortname (got '{o.Item ?? "null"}').");
                    if (string.IsNullOrEmpty(o.Pool)) continue;
                    pooled++;
                    if (o.Type != "turnin")
                        PrintWarning($"Quest '{q.Id}': Pool is for turnin objectives only — '{o.Type}' keeps its authored Target/Count.");
                    var known = false;
                    foreach (var t in _templates)
                        if (t != null && t.Pools != null && t.Pools.ContainsKey(o.Pool)) { known = true; break; }
                    if (!known)
                        PrintWarning($"Quest '{q.Id}': turn-in pool '{o.Pool}' is not defined by any template.");
                    QuestDef sq;
                    if (shippedByKey.TryGetValue($"{q.Id}#{q.Tree}#{q.Arc}", out sq) && oi < sq.Objectives.Count)
                    {
                        var so = sq.Objectives[oi];
                        if (!string.IsNullOrEmpty(so.Pool) && (so.Target != o.Target || so.Count != o.Count))
                            PrintWarning($"Quest '{q.Id}': objective {oi} is pooled ('{o.Pool}') — its edited Target/Count is overridden when the wipe rolls the pool. Tune the pool in templates.json instead.");
                    }
                }
                if (pooled > 1)
                    PrintWarning($"Quest '{q.Id}': {pooled} pooled objectives — the {{target}}/{{count}} tokens bind to the first one only.");
                if (q.Giver != UnpaidGiverKey && Profile(q.Giver) == null && _traders.Count > 0)
                    PrintWarning($"Quest '{q.Id}': giver '{q.Giver}' is neither a trader profile nor '{UnpaidGiverKey}'.");
                if (q.Giver == UnpaidGiverKey)
                {
                    if (q.AcceptCostScrap > 0)
                        PrintWarning($"Quest '{q.Id}': The Unpaid charge nothing — paper grants can't collect AcceptCostScrap.");
                    foreach (var o in q.Objectives)
                        if (o.Type == "turnin")
                            PrintWarning($"Quest '{q.Id}': The Unpaid have no counter — a turnin objective can never be claimed. Use drop instead.");
                }
                foreach (var r in q.Rewards)
                    if (ItemManager.FindItemDefinition(r.Item) == null)
                        PrintWarning($"Quest '{q.Id}': unknown reward item '{r.Item}'.");
                foreach (var g in q.GrantOnAccept)
                    if (ItemManager.FindItemDefinition(g.Item) == null)
                        PrintWarning($"Quest '{q.Id}': unknown grant item '{g.Item}'.");
            }
            foreach (var q in defs)
                if (q.RequiresQuest != null && defs.FindIndex(d => d.Id == q.RequiresQuest) < 0)
                    PrintWarning($"Quest '{q.Id}': RequiresQuest '{q.RequiresQuest}' not found.");

            _quests = defs;
            BuildQuestIndex();
        }

        // Sonia's hunting mastery chain (docs/content/sonia-arc.md). Shipped as
        // data-file defaults so quantities/text are tuned live without a redeploy.
        private List<QuestDef> DefaultQuests()
        {
            RewardDef R(string item, int amount = 1) => new RewardDef { Item = item, Amount = amount };
            // Container reward carrying liquid — `water` is per ITEM, not per reward.
            RewardDef RWater(string item, int amount, int water) => new RewardDef { Item = item, Amount = amount, Water = water };
            ObjectiveDef Kill(string species, string text) => new ObjectiveDef { Type = "kill", Target = species, Count = 1, Text = text };
            ObjectiveDef Turnin(string item, int amount, string text) => new ObjectiveDef { Type = "turnin", Target = item, Count = amount, Text = text };

            var list = new List<QuestDef>
            {
                new QuestDef
                {
                    Id = "sonia_knife", Name = "A Hunter's Edge",
                    AcceptCostScrap = 50,
                    GrantOnAccept = new List<RewardDef> { R("knife.combat") },
                    OfferText = "See this knife? Took it off a poacher who thought my valley was his. It's yours for 50 scrap — and with it, my respect, and my work.\n\nEvery hunter needs an edge. This one's proven.",
                    CompleteText = "Keep it sharp and it'll keep you fed. Now — let's see if you can use it.",
                },
                new QuestDef
                {
                    Id = "sonia_hunt_1", Name = "Feathers First", RequiresQuest = "sonia_knife",
                    Objectives = new List<ObjectiveDef> { Kill("Chicken", "Kill a chicken") },
                    Rewards = new List<RewardDef> { R("bow.hunting"), R("arrow.wooden", 48), R("scrap", 20) },
                    OfferText = "Everyone starts somewhere. Bring down a chicken — go on, laugh — but a hunter who can't feed herself on small game starves before the big hunts.\n\n{rewards} for your trouble.",
                    CompleteText = "Dinner and a lesson in one. Here — a proper bow. You've earned bigger prey.",
                },
                new QuestDef
                {
                    Id = "sonia_hunt_2", Name = "Tusk", RequiresQuest = "sonia_hunt_1",
                    Objectives = new List<ObjectiveDef> { Kill("Boar", "Kill a boar") },
                    Rewards = new List<RewardDef> { R("bow.compound"), R("arrow.wooden", 48), R("scrap", 40) },
                    OfferText = "Boars look dumb right up until one opens your leg with a tusk. Put one down and I'll trade up that bow of yours — smoother draw, harder hit.\n\nYours on delivery: {rewards}.",
                    CompleteText = "Clean kill. The compound's yours — treat her well.",
                },
                new QuestDef
                {
                    Id = "sonia_hunt_3", Name = "Crowned", RequiresQuest = "sonia_hunt_2",
                    Objectives = new List<ObjectiveDef> { Kill("Stag", "Kill a stag") },
                    Rewards = new List<RewardDef> { R("pistol.python"), R("ammo.pistol", 60), R("scrap", 60) },
                    OfferText = "Stags hear you thinking about them. Take one and you'll have learned patience — the only lesson that matters.\n\nDo it and I'll put real iron in your hand: {rewards}.",
                    CompleteText = "Patience pays. This Python's put down things with more teeth than any stag — she's yours now.",
                },
                // Quest 4 — big cat (jungle) / wolf (temperate fallback)
                new QuestDef
                {
                    Id = "sonia_hunt_4", Tree = "jungle", Name = "Shadow in the Canopy", RequiresQuest = "sonia_hunt_3",
                    Objectives = new List<ObjectiveDef> { Kill("Tiger|Panther", "Kill a big cat (tiger or panther)") },
                    Rewards = new List<RewardDef> { R("rifle.semiauto"), R("ammo.rifle", 60), R("scrap", 80) },
                    OfferText = "There's a cat in that jungle that watches me work. Tiger, panther — I don't care which you find first. When you've stood eye to eye with one and walked away, you're a hunter.\n\n{rewards}, for when you do.",
                    CompleteText = "You saw it before it saw you — that's the whole trick. Take the SAR. You're past bows now.",
                },
                new QuestDef
                {
                    Id = "sonia_hunt_4", Tree = "temperate", Name = "The Pack's Price", RequiresQuest = "sonia_hunt_3",
                    Objectives = new List<ObjectiveDef> { Kill("Wolf", "Kill a wolf") },
                    Rewards = new List<RewardDef> { R("rifle.semiauto"), R("ammo.rifle", 60), R("scrap", 80) },
                    OfferText = "Wolves hunt in packs, which means killing one fair is killing it scared, fast, or clever. Show me you can.\n\n{rewards}, for when you do.",
                    CompleteText = "One less set of eyes in the treeline. Take the SAR — you're past bows now.",
                },
                // Quest 5 — crocodile (jungle) / bear (temperate fallback)
                new QuestDef
                {
                    Id = "sonia_hunt_5", Tree = "jungle", Name = "River King", RequiresQuest = "sonia_hunt_4",
                    Objectives = new List<ObjectiveDef> { Kill("Crocodile", "Kill a crocodile") },
                    Rewards = new List<RewardDef> { R("rifle.lr300"), R("ammo.rifle", 120), R("scrap", 100) },
                    OfferText = "Last one. There's a croc in the river that's older than both of us and twice as mean. Every hunter who's gone after him is part of the riverbed now.\n\nBring him down and it's all yours — {rewards}. I won't need it after you do.",
                    CompleteText = "The river's quiet... I honestly didn't think anyone could. The LR is yours. You've out-hunted me.",
                },
                new QuestDef
                {
                    Id = "sonia_hunt_5", Tree = "temperate", Name = "The Old One", RequiresQuest = "sonia_hunt_4",
                    Objectives = new List<ObjectiveDef> { Kill("Bear", "Kill a bear") },
                    Rewards = new List<RewardDef> { R("rifle.lr300"), R("ammo.rifle", 120), R("scrap", 100) },
                    OfferText = "Last one. There's a bear on this island that's older than both of us and twice as mean.\n\nBring him down and it's all yours — {rewards}. I won't need it after you do.",
                    CompleteText = "So the old one's finally down... The LR is yours. You've out-hunted me.",
                },
                // --- Finale: her story, and the stash ---------------------------
                // {code} and {grid} resolve to the wipe's hidden stash at runtime,
                // so the clue is always true. Objective completes when the player
                // opens it; claim back at Sonia for the epilogue.
                new QuestDef
                {
                    Id = "sonia_finale", Name = "The Long Way Round", RequiresQuest = "sonia_hunt_5",
                    Objectives = new List<ObjectiveDef>
                    {
                        new ObjectiveDef { Type = "code", Target = FinaleStashTag, Count = 1, Text = "Find the stash near {grid} and open it (code {code})" },
                    },
                    Rewards = new List<RewardDef> { R("scrap", 250) },
                    OfferText =
                        "Sit down. You've earned the story, and I've been holding it too long.\n\n" +
                        "I wasn't always the woman behind this window. I tracked for a crew that ran the rails — six of us, this car, and a route nobody else was mad enough to take. We hunted what the island threw at us and we sold what we didn't eat. Good years.\n\n" +
                        "Then the rails went quiet. One by one they went out for something and didn't come back, until it was me, an empty car, and a lot of unopened bottles. So I put down the rifle, turned the lights on, and let the machines do the killing. People come for the slots. They stay because someone's still here.\n\n" +
                        "But I kept one thing back. Their share — the crew's. Buried it out at **{grid}** when I stopped believing anyone was coming for it. Code's **{code}**. Write it down; I'm not repeating it.\n\n" +
                        "Go dig it up. Someone ought to have it who earned it the same way we did.",
                    CompleteText =
                        "You found it. Good.\n\n" +
                        "That's the last of the old crew off my back, and I sleep better for it. Take the scrap too — the house can afford it tonight.\n\n" +
                        "Doors stay open, hunter. Machines are always warm, and there's a chair at the table with your name on it.",
                },

                // ===== Olivia — the arctic foundry: raw materials for ammunition =====
                new QuestDef
                {
                    Id = "olivia_bench", Giver = "olivia", Name = "Tools of the Trade",
                    AcceptCostScrap = 50,
                    GrantOnAccept = new List<RewardDef> { R("research.table"), R("icepick.salvaged"), R("axe.salvaged") },
                    OfferText =
                        "You look cold and you look poor. I can fix one of those.\n\n" +
                        "Fifty scrap buys you {rewards}. The table because you'll need one if you're going to be any use out here, the pick and the axe because everything I sell starts as something you dug or felled.\n\n" +
                        "I don't deal in trinkets. I deal in what goes bang. Bring me raw material and I'll show you.",
                    CompleteText = "Table goes somewhere dry, tools go on your belt. Now — I need wood, and a lot of it.",
                },
                new QuestDef
                {
                    Id = "olivia_wood", Giver = "olivia", Name = "Cordwood", RequiresQuest = "olivia_bench",
                    // Pooled (Phase E): the wipe rolls what the furnaces want
                    // this time — raw cordwood or burned-down charcoal — and
                    // how much. {count}/{target} keep the prose honest.
                    Objectives = new List<ObjectiveDef> { new ObjectiveDef { Type = "turnin", Target = "wood", Count = 3000, Pool = "furnace_feed", Text = "Bring {count} {target}" } },
                    Rewards = new List<RewardDef> { R("ammo.pistol", 100), R("ammo.pistol.hv", 100), R("ammo.pistol.fire", 100), R("scrap", 50) },
                    OfferText =
                        "{count} {target}. The furnaces don't feed themselves and neither do I, and this season they're asking for exactly that — don't argue with a furnace.\n\n" +
                        "Do that and you leave with {rewards}. Ball for practice, high velocity for the ones that run, incendiary for the ones that hide.",
                    CompleteText = "That'll keep the fires lit a while. Three hundred rounds of pistol — aim better than you haul.",
                },
                new QuestDef
                {
                    Id = "olivia_stone", Giver = "olivia", Name = "Quarry Work", RequiresQuest = "olivia_wood",
                    Objectives = new List<ObjectiveDef> { Turnin("stones", 3000, "Bring 3000 stone") },
                    Rewards = new List<RewardDef> { R("ammo.shotgun", 75), R("ammo.shotgun.fire", 75), R("ammo.shotgun.slug", 75), R("scrap", 100) },
                    OfferText =
                        "Three thousand stone next. I'm lining a smelter, not building a monument, but it still eats.\n\n" +
                        "Bring it and I'll load you out: {rewards}. Something for every kind of door.",
                    CompleteText = "Good. Buck for crowds, fire for cover, slug for anything wearing metal. Don't waste the slugs.",
                },
                new QuestDef
                {
                    Id = "olivia_metal", Giver = "olivia", Name = "Feed the Furnace", RequiresQuest = "olivia_stone",
                    Objectives = new List<ObjectiveDef> { Turnin("metal.ore", 2000, "Bring 2000 metal ore") },
                    Rewards = new List<RewardDef> { R("ammo.rifle", 100), R("ammo.rifle.explosive", 100), R("ammo.rifle.hv", 100), R("ammo.rifle.incendiary", 100), R("scrap", 150) },
                    OfferText =
                        "Two thousand metal ore. Dig it, don't buy it — I can tell the difference.\n\n" +
                        "This is where it gets serious: {rewards}. Four hundred rounds of five-five-six — the kind of ammunition that ends arguments about walls.",
                    CompleteText = "Now you're carrying something worth carrying. Explosive rounds chew structures — remember that.",
                },
                new QuestDef
                {
                    Id = "olivia_hqm", Giver = "olivia", Name = "The Good Stuff", RequiresQuest = "olivia_metal",
                    Objectives = new List<ObjectiveDef> { Turnin("hq.metal.ore", 100, "Bring 100 high quality metal ore") },
                    // The full Bradley loadout, handed out one quest BEFORE the
                    // Bradley job (owner's call) — she equips you, then points you at it.
                    Rewards = new List<RewardDef>
                    {
                        R("hazmatsuit"), R("rocket.launcher"), R("ammo.rocket.hv", 10),
                        R("jackhammer"), R("grenade.f1", 50), R("syringe.medical", 10), R("scrap", 200),
                    },
                    OfferText =
                        "A hundred high quality ore. I know what that costs you — that's the point.\n\n" +
                        "In exchange: {rewards}. The suit because where you're going is unhealthy, the launcher because knocking politely stopped working years ago, grenades and a jackhammer for whatever's left standing, and the syringes because I've seen how you people fly rockets.\n\n" +
                        "There's one more job after this. Keep all of it handy.",
                    CompleteText = "The launcher's zeroed and the rockets fly flat. Suit up before you do anything the syringes can't fix — and don't waste grenades on things the jackhammer opens quietly.",
                },
                new QuestDef
                {
                    // Id kept from the old scientist quest — content replaced v1.8.0
                    // (the 5-scientist kill moved to Alexa's Right of Way; the
                    // Bradley kill moved HERE from Alexa's old chain — it's the
                    // munitions QA's product recall).
                    Id = "olivia_scientists", Giver = "olivia", Name = "Recall Notice", RequiresQuest = "olivia_hqm",
                    Objectives = new List<ObjectiveDef> { new ObjectiveDef { Type = "kill", Target = "Bradley", Count = 1, Text = "Destroy the Bradley APC" } },
                    Rewards = new List<RewardDef>
                    {
                        R("multiplegrenadelauncher"),
                        R("ammo.grenadelauncher.he", 50), R("ammo.grenadelauncher.buckshot", 50), R("ammo.grenadelauncher.smoke", 50),
                        R("scrap", 250),
                        // The sheet that watched this tank before anyone dared
                        // touch it — annotated the day you filed the recall.
                        new RewardDef { Item = "note", Amount = 1, NoteTag = "archive_sheet_2" },
                    },
                    OfferText =
                        "Last job, and it's the one I've wanted done for years.\n\n" +
                        "The tank at the launch site. Every shell it fires went through my checkpoint — my stamp, my signature, my sign-off. I'm issuing a recall, and you're the courier.\n\n" +
                        "You have the launcher and the suit. Come back when it's burning and take: {rewards}. Forty-millimetre, three flavours, and the tube that throws them — the last thing I'll ever fabricate.",
                    CompleteText =
                        "I heard it from here. That's a recall completed — every batch accounted for.\n\n" +
                        "Here's the launcher. She's loud, she's ugly, and she solves problems. Use her like you paid for her, because you did.\n\n" +
                        "Sit down a minute. There's one more thing, and it isn't work.",
                },
                // Her story + the payday. {code}/{grid} resolve to her own stash.
                new QuestDef
                {
                    Id = "olivia_finale", Giver = "olivia", Name = "Cold Storage", RequiresQuest = "olivia_scientists",
                    Objectives = new List<ObjectiveDef>
                    {
                        new ObjectiveDef { Type = "code", Target = "olivia_finale", Count = 1, Text = "Find the cache near {grid} and open it (code {code})" },
                    },
                    Rewards = new List<RewardDef> { R("scrap", 300) },
                    OfferText =
                        "You want to know why an arms dealer lives on an ice shelf where nobody can find her. Fine.\n\n" +
                        "I wasn't a dealer. I was a fabricator — one of the ones in the blue hazmats, before the blue hazmats meant what they mean now. My job was munitions QA. Sign off on the crate, the crate goes on the boat, the boat goes wherever they point it.\n\n" +
                        "One day I stopped signing. Took a snowcat, a furnace, and everything I could carry, and put the whole ice field between me and them. That's why I have you kill their patrols and don't ask for a reason.\n\n" +
                        "But I brought my last shipment out with me. Never opened it, never sold it — my severance, in a box, buried at **{grid}**. Code's **{code}**.\n\n" +
                        "I'm too old to spend it. You aren't. Go on.",
                    CompleteText =
                        "So it was still there. Good — I'd half convinced myself I dreamt it.\n\n" +
                        "That's the last of the old company in my hands, and I'm glad it's out of them. Take the scrap as well; the foundry's paid for.\n\n" +
                        "Stay warm out there. If the men in blue come asking about the noise... you were never here.",
                },

                // ===== Alexa — the desert garage: parts in, vehicles out =====
                new QuestDef
                {
                    Id = "alexa_bench", Giver = "alexa", Name = "Somewhere to Work",
                    AcceptCostScrap = 50,
                    GrantOnAccept = new List<RewardDef> { R("box.repair.bench") },
                    OfferText =
                        "You're holding that like you don't know which end goes in the socket.\n\n" +
                        "Fifty scrap and I'll give you a repair bench — a real one, not the rusted thing you were about to build. Everything I teach you after this assumes you can fix your own gear.\n\n" +
                        "Then get your hands dirty. I don't care where.",
                    CompleteText = "Bolt it down somewhere with a roof. Now go strip me some engines.",
                },
                new QuestDef
                {
                    // Id kept from the old gears quest — content replaced v1.7.0.
                    Id = "alexa_gears", Giver = "alexa", Name = "Strip It Down", RequiresQuest = "alexa_bench",
                    Objectives = new List<ObjectiveDef>
                    {
                        Turnin("carburetor1", 10, "Bring 10 low quality carburetors"),
                        Turnin("crankshaft1", 10, "Bring 10 low quality crankshafts"),
                        Turnin("piston1", 10, "Bring 10 low quality pistons"),
                        Turnin("valve1", 10, "Bring 10 low quality valves"),
                        Turnin("sparkplug1", 10, "Bring 10 low quality spark plugs"),
                    },
                    Rewards = new List<RewardDef> { R("workbench1"), R("iotable"), R("scrap", 50) },
                    OfferText =
                        "Every engine on this island died of neglect. Go pull them apart.\n\n" +
                        "Ten of every low-quality part — carbs, cranks, pistons, valves, plugs. I don't want the parts, I want you to have stripped fifty engines' worth of other people's mistakes.\n\n" +
                        "Do it and you get {rewards}. The bench is where junk stops being junk.",
                    CompleteText = "Now you know what the inside of an engine looks like. Keep the habit — strip everything, always.",
                },
                // Variant of Strip It Down (slot "garage"): same Id, same
                // link, same bench-and-table payout — a season picking the
                // roadside clean instead of pulling engines apart.
                new QuestDef
                {
                    Id = "alexa_gears", Giver = "alexa", Name = "Parts Is Parts", RequiresQuest = "alexa_bench",
                    Arc = "four_keys.garage.scavenge",
                    Objectives = new List<ObjectiveDef>
                    {
                        Turnin("gears", 40, "Bring 40 gears"),
                        Turnin("metalpipe", 20, "Bring 20 metal pipes"),
                        Turnin("propanetank", 10, "Bring 10 empty propane tanks"),
                    },
                    Rewards = new List<RewardDef> { R("workbench1"), R("iotable"), R("scrap", 50) },
                    OfferText =
                        "Every engine on this island died of neglect, and the roads are lined with what fell off. Go pick the carcasses.\n\n" +
                        "Forty gears, twenty pipes, ten propane tanks. I don't need the junk — I need you to know where junk hides, because that's half of being a mechanic and the cheaper half at that.\n\n" +
                        "Do it and you get {rewards}. The bench is where junk stops being junk.",
                    CompleteText = "Now you know every scrapheap worth knowing, and they know you. Keep the habit — strip everything, always.",
                },
                new QuestDef
                {
                    // Id kept from the old tech-parts quest — content replaced v1.7.0.
                    Id = "alexa_techparts", Giver = "alexa", Name = "Build the Bay", RequiresQuest = "alexa_gears",
                    Objectives = new List<ObjectiveDef>
                    {
                        Turnin("diesel_barrel", 5, "Bring 5 diesel fuel"),
                        Turnin("crude.oil", 300, "Bring 300 crude oil"),
                    },
                    Rewards = new List<RewardDef> { R("modularcarlift"), R("wall.frame.garagedoor", 4), R("scrap", 100) },
                    OfferText =
                        "A garage is four doors and a lift. Everything else is decoration.\n\n" +
                        "Five diesel and three hundred crude — the lift runs on grease and so do I. For it: {rewards}.\n\n" +
                        "Roll something in, drop the doors, and nobody has to know what you're building.",
                    CompleteText = "Lift wants power and a flat floor. Doors want a frame first. You're a garage now — act like it.",
                },
                new QuestDef
                {
                    // Id kept from the old HQM quest — content replaced v1.7.0.
                    Id = "alexa_refined", Giver = "alexa", Name = "Cook It Yourself", RequiresQuest = "alexa_techparts",
                    Objectives = new List<ObjectiveDef> { Turnin("techparts", 15, "Bring 15 tech trash") },
                    Rewards = new List<RewardDef> { R("workbench2"), R("small.oil.refinery"), R("scrap", 150) },
                    OfferText =
                        "Fifteen tech trash. Same crates as everything else worth having.\n\n" +
                        "Buys you {rewards}. The refinery matters more than the bench: crude goes in, low grade comes out, and you never beg a quarry for fuel again.\n\n" +
                        "Cook it yourself. It's cheaper and nobody shoots you.",
                    CompleteText = "Refinery goes by the furnaces, not the bed — it smokes. And tier two means you research instead of guessing now.",
                },
                new QuestDef
                {
                    // Id kept from the old car-lift quest — content replaced v1.7.0.
                    Id = "alexa_lift", Giver = "alexa", Name = "Top Shelf", RequiresQuest = "alexa_refined",
                    Objectives = new List<ObjectiveDef>
                    {
                        Turnin("cctv.camera", 5, "Bring 5 CCTV cameras"),
                        Turnin("targeting.computer", 5, "Bring 5 laptops"),
                        Turnin("keycard_green", 5, "Bring 5 green keycards"),
                    },
                    Rewards = new List<RewardDef>
                    {
                        R("lowgradefuel", 2000), R("carburetor3", 2), R("crankshaft3", 2),
                        R("piston3", 3), R("valve3", 3), R("sparkplug3", 3), R("scrap", 200),
                    },
                    OfferText =
                        "The good parts don't come out of barrels.\n\n" +
                        "Five cameras, five laptops, five green cards — the things the men in blue keep behind locked doors. I have my reasons and you have your health, so don't ask.\n\n" +
                        "Trade you the top shelf for them: {rewards}. Tier threes, and the fuel to burn behind them.",
                    CompleteText = "That's showroom-grade, all of it. Don't mix tiers in one block — an engine is only as good as its cheapest part.",
                },
                new QuestDef
                {
                    // Id kept from the old Bradley quest — content replaced v1.7.0
                    // (the APC kill left the chain; Target "Scientist" is the same
                    // species key Olivia's Field Testing already uses live).
                    // Reward list gains FIELD NOTES №4 at claim (H.5).
                    Id = "alexa_bradley", Giver = "alexa", Name = "Right of Way", RequiresQuest = "alexa_lift",
                    Objectives = new List<ObjectiveDef> { new ObjectiveDef { Type = "kill", Target = "Scientist", Count = 5, Text = "Kill scientists" } },
                    Rewards = new List<RewardDef>
                    {
                        R("vehicle.1mod.cockpit.with.engine"), R("vehicle.2mod.camper"),
                        R("vehicle.1mod.storage", 2), R("vehicle.1mod.engine"), R("scrap", 250),
                        new RewardDef { Item = "note", Amount = 1, NoteTag = "archive_sheet_4" },
                    },
                    OfferText =
                        "Last job, and it's the one I'd do myself if I still went out there.\n\n" +
                        "The men in blue walk my roads like they hold the deed. Five of them, off the asphalt — I don't care which five or where they drop.\n\n" +
                        "For that: {rewards}. A whole rig, ready to bolt together. Drive it, sleep in it, haul in it. The road's yours after this.",
                    CompleteText =
                        "Five fewer clipboards on my roads. Should've charged you less — I'd have paid for that one myself.\n\n" +
                        "Stay a minute. There's something I've been sitting on.",
                },
                new QuestDef
                {
                    Id = "alexa_finale", Giver = "alexa", Name = "The Last Contract", RequiresQuest = "alexa_bradley",
                    Objectives = new List<ObjectiveDef>
                    {
                        new ObjectiveDef { Type = "code", Target = "alexa_finale", Count = 1, Text = "Find the cache near {grid} and open it (code {code})" },
                    },
                    Rewards = new List<RewardDef> { R("scrap", 300) },
                    OfferText =
                        "I serviced their armour. That's the part I don't say out loud.\n\n" +
                        "Motor pool at the launch site, six years, and every APC that rolled out of my bay went somewhere and came back with the paint scratched off and the hull washed out. I stopped asking what the washing was for.\n\n" +
                        "When I left I took my toolbox and one crate that wasn't mine. The motor pool's master set — the bench they certified me on and every mod they ever bolted to it, packed for a transfer that never happened. Buried it out at **{grid}** and never opened it, because opening it makes it mine.\n\n" +
                        "Code's **{code}**. You cleared my roads and built a bay from nothing. You've got more claim to it than I do.",
                    CompleteText =
                        "Hah. Of course it still smells like the motor pool.\n\n" +
                        "That bench built everything they ever sent somewhere ugly, and every trick it knows is bolted right to it. Now it builds for you — that's the cleanest thing that kit will ever have done.\n\n" +
                        "Keep the scrap too. I've got a lift, a bay, and nobody signing my paperwork — that's the only severance I wanted.\n\n" +
                        "Come by when something breaks. Something always breaks.",
                },

                // ===== Rebecca — the homestead: soil and circuits =====
                new QuestDef
                {
                    Id = "rebecca_planter", Giver = "rebecca", Name = "Good Soil",
                    AcceptCostScrap = 50,
                    // Water rides in the jugs: 4 x 5000 ml, because one jug caps
                    // at 5000 (the item's own description says so).
                    GrantOnAccept = new List<RewardDef>
                    {
                        R("planter.large", 2), R("fertilizer", 100), R("seed.hemp", 20), RWater("waterjug", 4, 5000),
                    },
                    OfferText =
                        "You've got the look of someone living on radiated tuna.\n\n" +
                        "Fifty scrap and you walk out of here a farmer: {rewards}. Two boxes, enough fertiliser to make the soil mean it, hemp to start, and the water to see it through the first week.\n\n" +
                        "Plant them somewhere you can defend. Everything else I teach you grows out of those boxes — food, cloth, medicine.",
                    CompleteText = "Fertiliser in first, then seed, then water — in that order or you're just making mud. Now: I need cloth, and a lot of it.",
                },
                new QuestDef
                {
                    Id = "rebecca_cloth", Giver = "rebecca", Name = "First Harvest", RequiresQuest = "rebecca_planter",
                    Objectives = new List<ObjectiveDef> { Turnin("cloth", 1000, "Bring 1000 cloth") },
                    Rewards = new List<RewardDef> { R("composter"), R("seed.corn", 10), R("seed.pumpkin", 10), R("seed.potato", 10), R("scrap", 100) },
                    OfferText =
                        "A thousand cloth. Grow it or pick it wild, but grow it — you'll learn more.\n\n" +
                        "For that: {rewards}. The composter turns everything you'd otherwise throw away back into fertiliser, and the seed gets you off berries and onto real food.",
                    CompleteText = "Compost the trimmings, never the seeds. Corn in full sun, pumpkins where they can sprawl, potatoes anywhere at all.",
                },
                new QuestDef
                {
                    Id = "rebecca_food", Giver = "rebecca", Name = "Catch and Keep", RequiresQuest = "rebecca_cloth",
                    Objectives = new List<ObjectiveDef>
                    {
                        Turnin("corn", 100, "Bring 100 corn"),
                        Turnin("pumpkin", 100, "Bring 100 pumpkins"),
                    },
                    Rewards = new List<RewardDef> { R("water.catcher.large"), R("water.barrel"), R("cookingworkbench"), R("chickencoop"), R("beehive"), R("scrap", 150) },
                    OfferText =
                        "A hundred corn and a hundred pumpkins. That's two full planters of patience, and patience is the whole lesson.\n\n" +
                        "Then the homestead starts feeding itself: {rewards}. The catcher takes water out of the sky, the barrel holds it for the dry spell, the bench turns produce into meals — and the birds and the bees handle eggs and honey while you're off doing something foolish.",
                    CompleteText = "Catcher up high and clear of roof, barrel below it — water only ever flows down. Hens want warmth and the hive wants flowers nearby; neither wants a fuss made. Cook the pumpkin, don't eat it raw like an animal.",
                },
                // Variant of Catch and Keep (slot "harvest"): same Id, same
                // link in her chain, same homestead payout — a potato year
                // instead of a corn-and-pumpkin year. Active only when the
                // bible picks the arc; the pick shadows the classic.
                new QuestDef
                {
                    Id = "rebecca_food", Giver = "rebecca", Name = "What Keeps", RequiresQuest = "rebecca_cloth",
                    Arc = "four_keys.harvest.roots",
                    Objectives = new List<ObjectiveDef> { Turnin("potato", 200, "Bring 200 potatoes") },
                    Rewards = new List<RewardDef> { R("water.catcher.large"), R("water.barrel"), R("cookingworkbench"), R("chickencoop"), R("beehive"), R("scrap", 150) },
                    OfferText =
                        "Two hundred potatoes. Corn sulks and pumpkins are all show — a potato is the only crop on this island that never once lied to me.\n\n" +
                        "Grow them in rows, not heaps, and bring me a harvest that keeps. Then the homestead starts feeding itself: {rewards}. The catcher takes water out of the sky, the barrel holds it for the dry spell, the bench turns produce into meals — and the birds and the bees handle eggs and honey while you're off doing something foolish.",
                    CompleteText =
                        "Firm ones, too — you've been listening.\n\n" +
                        "Catcher up high and clear of roof, barrel below it — water only ever flows down. Hens want warmth and the hive wants flowers nearby; neither wants a fuss made.\n\n" +
                        "And keep six back for seed. Always keep six back — that's how you know winter never gets the last word.",
                },
                new QuestDef
                {
                    Id = "rebecca_power", Giver = "rebecca", Name = "Down One Wire", RequiresQuest = "rebecca_food",
                    Objectives = new List<ObjectiveDef> { Turnin("lowgradefuel", 1000, "Bring 1000 low grade fuel") },
                    Rewards = new List<RewardDef>
                    {
                        R("electric.solarpanel.large", 10), R("electric.battery.rechargable.large"), R("electrical.combiner", 9),
                        R("electrical.branch", 2), R("fridge"), R("electric.splitter"), R("scrap", 200),
                    },
                    OfferText =
                        "A thousand low grade. Render it from fat if you must — I won't judge, I've done worse for less.\n\n" +
                        "Then we electrify properly: {rewards}. Ten panels is a real array, not a science project, and the combiners are how you get all of it down one wire without setting fire to anything.\n\n" +
                        "The fridge is not a luxury. Ask anyone who's watched a week of harvest go off in a box.",
                    CompleteText =
                        "Panels flat to the sky, combiners in pairs up the chain, battery indoors where the cold can't get at it.\n\n" +
                        "Branch what you can afford to lose, never the battery. Store before you spend — that's the whole of electricity in four words.",
                },
                new QuestDef
                {
                    Id = "rebecca_grid", Giver = "rebecca", Name = "While You Sleep", RequiresQuest = "rebecca_power",
                    Objectives = new List<ObjectiveDef>
                    {
                        Turnin("techparts", 20, "Bring 20 tech trash"),
                        Turnin("metal.refined", 100, "Bring 100 high quality metal"),
                    },
                    Rewards = new List<RewardDef>
                    {
                        R("electric.furnace", 3), R("storageadaptor", 4), R("industrial.conveyor"), R("scrap", 250),
                    },
                    OfferText =
                        "Twenty tech trash and a hundred high quality. The last of the shopping, I promise.\n\n" +
                        "It buys you the end of carrying things: {rewards}. Electric furnaces you never feed wood, adaptors on the boxes, and a conveyor to move it all while you're asleep.\n\n" +
                        "After this there's only the story, and I don't charge for that.",
                    CompleteText =
                        "That's a homestead that runs itself. Adaptor on every box you want the belt to see, and mind which way the arrow points — everyone gets that wrong once.\n\n" +
                        "Sit with me a moment. There's something I've never told anyone on this island.",
                },
                new QuestDef
                {
                    Id = "rebecca_finale", Giver = "rebecca", Name = "What the Lights Were For", RequiresQuest = "rebecca_grid",
                    Objectives = new List<ObjectiveDef>
                    {
                        new ObjectiveDef { Type = "code", Target = "rebecca_finale", Count = 1, Text = "Find the cellar near {grid} and open it (code {code})" },
                    },
                    // Scrap is hers to hand over; the gear is in the cellar, so it
                    // arrives from the crate on opening it (stashes.json).
                    Rewards = new List<RewardDef> { R("scrap", 300) },
                    OfferText =
                        "There were greenhouses here. Before.\n\n" +
                        "Rows of them, and I ran the light cycles — sixteen hours on, eight off, and a hundred people fed off my timers. Then the power went for good, and one by one the rows died in the dark while I sat there rationing a generator that couldn't hold them all.\n\n" +
                        "I chose which rows lived. That's the part that stays with you.\n\n" +
                        "The gear that would have saved them turned up two months late. A wheel, a turbine, and the little tester I used to check a circuit before I trusted it with anything living. I put the lot down a cellar at **{grid}**, code **{code}**, and I've never once had the heart to go back for it.\n\nYou've built something that lasts. You should have it.",
                    CompleteText =
                        "You opened it. Thank you. Genuinely.\n\n" +
                        "The wheel and the turbine would have carried what the generator couldn't, if they'd come in time. Put them somewhere they'll actually turn — that's all I ever wanted for them.\n\n" +
                        "The tea is mine. I brewed it for the nights I couldn't sleep for thinking about which rows I let go dark. You'll sleep better than I did.\n\n" +
                        "Just keep the lights on. That's the whole of it.",
                },

                // ===== The vault — all four chains, one door =====
                new QuestDef
                {
                    Id = "grand_finale", Giver = DefaultTraderKey, Name = "Four Keys",
                    RequiresQuests = new List<string> { "sonia_finale", "olivia_finale", "alexa_finale", "rebecca_finale" },
                    Objectives = new List<ObjectiveDef>
                    {
                        new ObjectiveDef { Type = "code", Target = "grand_finale", Count = 1, Text = "Find the armoury near {grid} and open it (code {code})" },
                    },
                    Rewards = new List<RewardDef> { R("scrap", 500) },
                    OfferText =
                        "Sit down. This one isn't mine alone.\n\n" +
                        "Four of us came off that island's payroll: me tracking, Olivia making the ammunition, Alexa keeping their armour rolling, Rebecca running the lights. We never worked the same site, but we all walked away from the same company — and every one of us buried something on the way out.\n\n" +
                        "There's one more cache. The one none of us would open alone, because it isn't severance, it's a war chest. We split the code four ways and agreed it stays buried until somebody earned all four of our stories.\n\n" +
                        "You did that. So here it is, whole: **{grid}**, code **{code}**.\n\n" +
                        "Whatever you do with what's inside — do it deliberately.",
                    CompleteText =
                        "So that's what we were sitting on. Good grief.\n\n" +
                        "Wear it, carry it, and remember four women trusted you with the whole of it. The others will hear about this before you reach the road — we do talk.\n\n" +
                        "You're not a customer any more. Come and go as you please.",
                },

                // --- THE UNPAID: the first faceless side chain (v2 Phase B) --
                // Voice rules: docs/content/resistance.md — typewriter, dry
                // anger, THE UNPAID sign-off, never a trader's name, knows
                // LESS than the welcome note. Entry: any stamped pamphlet in
                // hand, or stumbling on the field sheet itself. Everything
                // auto-claims — there is no counter to return to.
                new QuestDef
                {
                    Id = "unpaid_contact", Giver = UnpaidGiverKey, Arc = "four_keys.unpaid",
                    Name = "A READER",
                    Objectives = new List<ObjectiveDef>
                    {
                        new ObjectiveDef { Type = "readnote", Target = "unpaid_fieldsheet", Text = "Find the field sheet on the ground near {monument:unpaid_drop} (around {monumentgrid:unpaid_drop})" },
                    },
                    OfferText =
                        "YOU PICKED UP THE SHEET. THAT MAKES YOU A READER.\n\n" +
                        "READERS WHO WANT TO BE PAID SHOULD CHECK THE GROUND NEAR {monument:unpaid_drop} — THE {monumentgrid:unpaid_drop} SQUARE, MORE OR LESS. WE LEFT A BOX AND INSTRUCTIONS. " +
                        "THE INSTRUCTIONS ARE NOT SIGNED WITH A NAME BECAUSE NAMES ARE FOR PAYROLL, AND YOU KNOW WHAT HAPPENED TO PAYROLL.\n\n" +
                        "— THE UNPAID",
                    CompleteText =
                        "THE SHEET WAS WHERE WE LEFT IT. TYPED ON A RIBBON THAT DIED WITH THE COMPANY.\n\n" +
                        "WORK FOLLOWS. IT ALWAYS DOES.\n\n— THE UNPAID",
                },
                new QuestDef
                {
                    Id = "unpaid_drop_1", Giver = UnpaidGiverKey, Arc = "four_keys.unpaid",
                    Name = "BACK PAY, ONE OF THREE", RequiresQuest = "unpaid_contact",
                    Objectives = new List<ObjectiveDef>
                    {
                        new ObjectiveDef { Type = "drop", Target = "unpaid_drop", Item = "bandage", Count = 5, Text = "Leave 5 bandages in the drop box near {monument:unpaid_drop}" },
                    },
                    Rewards = new List<RewardDef> { R("scrap", 75) },
                    OfferText =
                        "FIRST JOB. MEDICINE.\n\n" +
                        "COBALT LOGGED EVERY GAUZE PAD AND BILLED US FOR THE ONES WE USED ON EACH OTHER. " +
                        "PUT 5 BANDAGES IN THE BOX. NOBODY WILL WATCH YOU DO IT. THAT IS THE POINT.\n\n" +
                        "PAYMENT IS ALREADY UNDER THE BOX. WE PAY FORWARD. IT IS THE ONLY DIRECTION LEFT.\n\n" +
                        "— THE UNPAID",
                    CompleteText =
                        "THE BOX TOOK THE MEDICINE. SOMEBODY'S NIGHT SHIFT JUST GOT SURVIVABLE.\n\n— THE UNPAID",
                },
                new QuestDef
                {
                    Id = "unpaid_drop_2", Giver = UnpaidGiverKey, Arc = "four_keys.unpaid",
                    Name = "BACK PAY, TWO OF THREE", RequiresQuest = "unpaid_drop_1",
                    Objectives = new List<ObjectiveDef>
                    {
                        new ObjectiveDef { Type = "drop", Target = "unpaid_drop", Item = "lowgradefuel", Count = 100, Text = "Leave 100 low grade fuel in the same drop box" },
                    },
                    Rewards = new List<RewardDef> { R("scrap", 100), R("syringe.medical", 2) },
                    OfferText =
                        "SECOND JOB. FUEL.\n\n" +
                        "THE GENERATORS WERE OURS TO KEEP ALIVE AND THE FUEL WAS THEIRS TO RATION. " +
                        "100 LOW GRADE. SAME BOX. BOXES THAT MOVE GET FOUND.\n\n" +
                        "— THE UNPAID",
                    CompleteText =
                        "RECEIVED. THERE ARE LIGHTS ON TONIGHT THAT COBALT NEVER PAID FOR.\n\n— THE UNPAID",
                },
                new QuestDef
                {
                    Id = "unpaid_drop_3", Giver = UnpaidGiverKey, Arc = "four_keys.unpaid",
                    Name = "BACK PAY, THREE OF THREE", RequiresQuest = "unpaid_drop_2",
                    Objectives = new List<ObjectiveDef>
                    {
                        new ObjectiveDef { Type = "drop", Target = "unpaid_drop", Item = "metal.fragments", Count = 500, Text = "Leave 500 metal fragments in the same drop box" },
                    },
                    // The settled sheet pays out the faction's founding
                    // document too (H.5) — lore as loot.
                    Rewards = new List<RewardDef> { R("scrap", 150), new RewardDef { Item = "note", Amount = 1, NoteTag = "archive_sheet_1" } },
                    OfferText =
                        "LAST JOB ON THIS SHEET. METAL.\n\n" +
                        "EVERYTHING ON THIS ISLAND RUSTS EXCEPT THE INVOICES. 500 METAL FRAGMENTS IN THE BOX. " +
                        "WHAT WE ARE BUILDING IS NOT YOUR PROBLEM YET.\n\n" +
                        "— THE UNPAID",
                    CompleteText =
                        "THE SHEET IS SETTLED AND SO ARE YOU. THAT PHRASE USED TO MEAN SOMETHING HERE.\n\n" +
                        "KEEP THE PAMPHLETS MOVING. THERE WILL BE ANOTHER SHEET WHEN THERE IS ANOTHER SHEET.\n\n" +
                        "— THE UNPAID",
                },
            };

            // v2: every v1 quest rides the four_keys template's core arc; the
            // cross-trader vault gets its own arc so a future template can
            // swap the finale without touching the chains. Stamped here (not
            // per-initializer) so the whole set can never drift out of step.
            foreach (var q in list)
                if (q.Arc == null)
                    q.Arc = q.Id == "grand_finale" ? "four_keys.vault" : "four_keys.core";
            return list;
        }

        private bool QuestInTree(QuestDef q) => q.Tree == null || q.Tree == _state.QuestTree;

        // An arc-tagged quest exists only on wipes whose bible rolled its arc.
        private bool ArcActive(string arc) =>
            arc == null || (_state != null && _state.Bible != null && _state.Bible.ActiveArcs.Contains(arc));

        // THE per-wipe visibility predicate — tree AND arc AND not shadowed
        // by a picked variant. Everything that asks "does this quest exist
        // this wipe" goes through here.
        private bool QuestActiveThisWipe(QuestDef q) => QuestInTree(q) && ArcActive(q.Arc) && !q.ShadowedThisWipe;

        // Index of the quests active this wipe. FindQuest is called from
        // OnEntityDeath and the hook recompute, where a List.Find lambda
        // allocated a closure + delegate on every single call.
        private readonly Dictionary<string, QuestDef> _questIndex = new Dictionary<string, QuestDef>();

        private void BuildQuestIndex()
        {
            _questIndex.Clear();
            // Variant-shadows-classic (v2 Phase E): a slot pick that activated
            // a variant arc retires the same-Id quest the base arcs still
            // carry. The classic defs are never re-tagged or written — a
            // bible with no picks simply plays the classics.
            for (var i = 0; i < _quests.Count; i++) _quests[i].ShadowedThisWipe = false;
            var picks = _state != null && _state.Bible != null ? _state.Bible.VariantPicks : null;
            if (picks != null && picks.Count > 0)
                foreach (var kv in picks)
                {
                    if (kv.Value == ClassicVariant) continue;
                    for (var i = 0; i < _quests.Count; i++)
                    {
                        var v = _quests[i];
                        if (v.Arc != kv.Value || !QuestInTree(v) || !ArcActive(v.Arc)) continue;
                        for (var j = 0; j < _quests.Count; j++)
                        {
                            var other = _quests[j];
                            if (other == v || other.Id != v.Id) continue;
                            if (other.Arc == v.Arc) continue; // tree siblings inside the variant arc stand
                            other.ShadowedThisWipe = true;
                        }
                    }
                }
            for (var i = 0; i < _quests.Count; i++)
            {
                var q = _quests[i];
                if (string.IsNullOrEmpty(q.Id) || !QuestActiveThisWipe(q)) continue;
                _questIndex[q.Id] = q;
                for (var j = 0; j < q.Objectives.Count; j++)
                {
                    var o = q.Objectives[j];
                    if (o.Type != "kill" || string.IsNullOrEmpty(o.Target)) continue;
                    var parts = o.Target.Split('|');
                    for (var k = 0; k < parts.Length; k++) parts[k] = parts[k].Trim();
                    o.TargetsCached = parts;
                }
            }
        }

        private QuestDef FindQuest(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            QuestDef q;
            return _questIndex.TryGetValue(id, out q) ? q : null;
        }

        #endregion

        #region Data — world content (note & stash lore)

        private class NoteContent
        {
            public string Title = "";
            public string Text = "";
            // v2: arc-gated content — placed/granted only on wipes that rolled
            // the arc.
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string Arc;
            // Paper grants work (Phase B): picking this note up auto-accepts
            // the quest — the sheet IS the offer. Prereqs still apply, so
            // re-reading never double-grants.
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string GrantsQuest;
            // Auto-placement (Phase B): a bible monument-binding key. When the
            // arc is live, the note self-places in a ring outside that
            // monument on wipe boot — the LorePlacer v1 deferred, finally.
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string Monument;
        }
        private class StashContent
        {
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<RewardDef> Loot = new List<RewardDef>();
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string Arc;
        }

        private Dictionary<string, NoteContent> _noteContent = new Dictionary<string, NoteContent>();
        private Dictionary<string, StashContent> _stashContent = new Dictionary<string, StashContent>();

        private void LoadWorldContent()
        {
            // Both files merge by key: new authored entries appear, existing
            // ones are never overwritten (live tuning wins — same rule as quests).
            try { _noteContent = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, NoteContent>>($"{DataRoot}/notes"); }
            catch { _noteContent = null; }
            if (_noteContent == null) _noteContent = new Dictionary<string, NoteContent>();
            var notesAdded = 0;
            foreach (var kv in DefaultNotes())
                if (!_noteContent.ContainsKey(kv.Key)) { _noteContent[kv.Key] = kv.Value; notesAdded++; }
            if (notesAdded > 0)
            {
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/notes", _noteContent);
                DLog($"Added {notesAdded} note(s) to notes.json.");
            }

            // Reward NoteTags can only be checked once notes are merged —
            // quests load before world content (H.5).
            foreach (var q in _quests)
            {
                foreach (var r in q.Rewards)
                    if (!string.IsNullOrEmpty(r.NoteTag))
                    {
                        if (r.Item != "note") PrintWarning($"Quest '{q.Id}': NoteTag '{r.NoteTag}' on non-note item '{r.Item}' — it only stocks notes.");
                        if (!_noteContent.ContainsKey(r.NoteTag)) PrintWarning($"Quest '{q.Id}': reward NoteTag '{r.NoteTag}' has no notes.json entry.");
                    }
                foreach (var g in q.GrantOnAccept)
                    if (!string.IsNullOrEmpty(g.NoteTag) && !_noteContent.ContainsKey(g.NoteTag))
                        PrintWarning($"Quest '{q.Id}': grant NoteTag '{g.NoteTag}' has no notes.json entry.");
            }
            // Same late check for the delayed pieces' paper (I.5) — templates
            // load before world content too.
            foreach (var t in _templates)
            {
                if (t == null || t.Choice == null || t.Choice.Options == null) continue;
                foreach (var o in t.Choice.Options)
                {
                    if (o == null) continue;
                    foreach (var dp in o.Delayed)
                        if (dp != null && !string.IsNullOrEmpty(dp.NoteTag) && !_noteContent.ContainsKey(dp.NoteTag))
                            PrintWarning($"Template '{t.Id}': delayed piece '{dp.Id}' NoteTag '{dp.NoteTag}' has no notes.json entry.");
                }
            }

            try { _stashContent = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, StashContent>>($"{DataRoot}/stashes"); }
            catch { _stashContent = null; }
            if (_stashContent == null) _stashContent = new Dictionary<string, StashContent>();
            var stashesAdded = 0;
            foreach (var kv in DefaultStashes())
                if (!_stashContent.ContainsKey(kv.Key)) { _stashContent[kv.Key] = kv.Value; stashesAdded++; }
            if (stashesAdded > 0)
            {
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/stashes", _stashContent);
                DLog($"Added {stashesAdded} stash loot table(s) to stashes.json.");
            }
        }

        private Dictionary<string, NoteContent> DefaultNotes() => new Dictionary<string, NoteContent>
        {
            // Handed to every player once per wipe (WelcomeNote config). Names
            // three of the four only by where they went to ground — the whole
            // point is that {trader} is the one door that's already open.
            [WelcomeNoteTag] = new NoteContent
            {
                Title = "Read This First",
                Text = "Whoever finds this — read it before the rain does.\n\nFour of them came off the Cobalt payroll and never left this island. One went up into the ice. One is out past the dust with the machines. One puts things back in the ground where it's still green. I'm not writing their names down. They didn't all leave clean, and two of them would come asking how I knew.\n\nThe fourth is {trader}. She keeps a railcar lit out in the green and she doesn't mind being found — she'll take your scrap and pay it back in gear if you can work. Start with her. She's the only one who'll explain a thing twice.\n\nWalk up to her counter and press E to talk.\n\nType /quest to open your journal — what you've taken on, how far along you are, and where to go next.\n\nTheir lights are on. The map can see them.\n\n    — C.",
            },
            // THE UNPAID's field sheet (v2 Phase B): self-places in a ring
            // outside the bible's bound monument, grants the contact quest on
            // pickup (which its own reading then completes — the chain flows
            // from finding either this or a pamphlet).
            ["unpaid_fieldsheet"] = new NoteContent
            {
                Title = "FIELD SHEET — FOUND WORK",
                Arc = "four_keys.unpaid",
                Monument = "unpaid_drop",
                GrantsQuest = "unpaid_contact",
                // Short lines on purpose: the vanilla note-reading panel
                // clips long paragraphs (found live 2026-08-10).
                Text =
                    "YOU FOLLOWED PAPER TO DIRT. GOOD.\n" +
                    "COBALT NEVER TESTED FOR THAT.\n\n" +
                    "THE BOX IS CLOSE. GREY WOOD. NO LOCK.\n" +
                    "WE DO NOT LOCK WHAT WE EXPECT BACK.\n\n" +
                    "WORK ORDER: MEDICINE. FUEL. METAL.\n" +
                    "THE JOURNAL KEEPS THE LIST — TYPE /quest.\n\n" +
                    "PAYMENT IS ALREADY UNDER THE BOX.\n" +
                    "THAT IS CALLED TRUST. WE READ ABOUT IT.\n\n" +
                    "DO NOT ASK ABOUT US.\n" +
                    "THERE IS NO US. THERE IS A BOX.\n\n" +
                    "WE COUNT THE WIPES. THIS IS NOT THE FIRST.\n\n" +
                    "— THE UNPAID",
            },
            // Beat-published (v2 Phase F, arc four_keys.unpaid.reprint): lands
            // near the same box days after the first READER, numbered fresh
            // per wipe off the cross-wipe ledger — №3 and №5 are the canon
            // prints, and the numbers this run skips are out working.
            ["unpaid_fieldnotes"] = new NoteContent
            {
                Title = "FIELD NOTES №{fieldnote}",
                Arc = "four_keys.unpaid.reprint",
                Monument = "unpaid_drop",
                Text =
                    "FIELD NOTES №{fieldnote}\n" +
                    "WIPE {wipes}. WE ARE STILL COUNTING.\n\n" +
                    "THE TANK STILL RUNS ITS LOOP.\n" +
                    "THE PATROLS STILL WALK THEIRS.\n" +
                    "NOTHING LEARNED. NOTHING PAID.\n\n" +
                    "SOMEBODY READ THE SHEET BY THE BOX.\n" +
                    "SOMEBODY ALWAYS DOES.\n" +
                    "THAT IS WHY WE KEEP TYPING.\n\n" +
                    "THE NUMBERS MISSING FROM THIS RUN\n" +
                    "ARE NOT MISSING.\n" +
                    "THEY ARE OUT WORKING.\n\n" +
                    "— THE UNPAID",
            },
            // The island's verdict, typed (Phase G): one of these two places
            // by the drop box after the FIRST press_verdict pick — the arcs
            // press.exposed / press.protected go live additively at pick time.
            ["unpaid_exposed"] = new NoteContent
            {
                Title = "A NOTICE OF RELOCATION",
                Arc = "press.exposed",
                Monument = "unpaid_drop",
                Text =
                    "THE PRESS HAS A NAME NOW.\n" +
                    "SOMEBODY SPENT IT.\n\n" +
                    "WE MOVED HER MACHINE BEFORE MORNING.\n" +
                    "IT PRINTS FROM SOMEWHERE COLDER NOW.\n\n" +
                    "NAMES ARE FOR PAYROLL.\n" +
                    "YOU KNOW WHAT HAPPENED TO PAYROLL.\n\n" +
                    "THE SHEETS DO NOT STOP.\n" +
                    "NEITHER DO WE.\n\n" +
                    "— THE UNPAID",
            },
            ["unpaid_protected"] = new NoteContent
            {
                Title = "A RECEIPT FOR SILENCE",
                Arc = "press.protected",
                Monument = "unpaid_drop",
                Text =
                    "YOU KNEW. YOU SAID NOTHING.\n" +
                    "THAT IS THE WHOLE JOB, READER.\n\n" +
                    "MOST PEOPLE SPEND A NAME\n" +
                    "THE SAME DAY THEY LEARN IT.\n" +
                    "YOU PUT YOURS IN THE GROUND.\n\n" +
                    "THE PRESS STAYS WARM.\n" +
                    "THE SHEETS KEEP COMING.\n\n" +
                    "BACK PAY FINDS PEOPLE LIKE YOU.\n\n" +
                    "— THE UNPAID",
            },
            // ===== "The Manifest" notes (v2 Phase H) =====
            // Manifest wipes only (arc manifest.rumors): a salvaged page from
            // the list itself, water-damaged into the deduction game — it
            // proves seats and strike-throughs EXIST without naming anyone.
            ["manifest_page"] = new NoteContent
            {
                Title = "a water-stained page",
                Arc = "manifest.rumors",
                Monument = "manifest_dock",
                Text =
                    "COBALT MARITIME DIVISION — EMERGENCY WIND-DOWN\n" +
                    "VESSEL ALLOCATION, PAGE __ OF __\n\n" +
                    "ROW 12 — [water damage] — SEAT CONFIRMED\n" +
                    "ROW 13 — [water damage] — SEAT CONFIRMED\n" +
                    "ROW 14 — [illegible] — struck through, one line, by hand\n" +
                    "ROW 15 — [water damage] — SEAT CONFIRMED\n\n" +
                    "the rest is salt and pulp. somebody typed this.\n" +
                    "somebody else held the pen that made the line.\n\n" +
                    "(found wedged under the tide rocks. kept dry since.\n" +
                    "not my writing. not my names. — no signature)",
            },
            // Beat-published (arc manifest.pages, day 2-4, time-only): the
            // wind keeps handing pages out whether or not anyone reads them.
            ["manifest_page_six"] = new NoteContent
            {
                Title = "MANIFEST, PAGE SIX",
                Arc = "manifest.pages",
                Monument = "unpaid_drop",
                Text =
                    "PAGE SIX OF THE MANIFEST.\n" +
                    "WE HAVE OTHERS. WE ARE MISSING MORE.\n\n" +
                    "FORTY SEATS. TWO HUNDRED OF US.\n" +
                    "THE MATH HAS NOT IMPROVED WITH AGE.\n\n" +
                    "SOME ROWS SAY SEAT CONFIRMED.\n" +
                    "ONE ROW ON THIS PAGE IS CROSSED OUT\n" +
                    "IN PEN. TYPE DECIDES. INK OVERRULES.\n" +
                    "REMEMBER THAT ORDER OF OPERATIONS.\n\n" +
                    "IF YOU FIND A PAGE, READ THE ROWS.\n" +
                    "THEN READ THE FACES AT THE COUNTERS.\n\n" +
                    "WIPE {wipes}. STILL COUNTING.\n\n" +
                    "— THE UNPAID",
            },
            // The verdict's aftermath (choice arcs, Phase G machinery): one of
            // these three places by the drop box when the island's first pick
            // lands. The POST note may name the seated — post-reveal prose.
            ["manifest_posted_note"] = new NoteContent
            {
                Title = "AN AUDIT, SETTLED",
                Arc = "manifest.posted",
                Monument = "unpaid_drop",
                Text =
                    "THE WHOLE LIST IS ON EVERY COUNTER.\n" +
                    "WE HAVE WAITED YEARS TO READ ROW BY ROW.\n\n" +
                    "FORTY SEATS. WE COUNTED. OF COURSE WE DID.\n" +
                    "ONE SEAT WAS CONFIRMED AND NEVER FILLED.\n" +
                    "{secret:seated_identity} STAYED.\n\n" +
                    "WE DO NOT FORGIVE THE LIST.\n" +
                    "WE NOTE, FOR THE FILE, THAT A SEAT\n" +
                    "IS NOT THE SAME AS A LEAVING.\n\n" +
                    "THE ARITHMETIC IS PUBLIC NOW.\n" +
                    "LET IT STAY PUBLIC.\n\n" +
                    "— THE UNPAID",
            },
            ["manifest_burned_note"] = new NoteContent
            {
                Title = "A NOTE ON SMOKE",
                Arc = "manifest.burned",
                Monument = "unpaid_drop",
                Text =
                    "SOMEBODY BURNED THE COMPLETE COPY.\n" +
                    "WE KNOW BECAUSE WE SMELLED THE RIBBON INK.\n\n" +
                    "UNDERSTAND US CAREFULLY:\n" +
                    "WE ARE NOT ANGRY.\n" +
                    "WE EXPECTED TO BE, AND WE ARE NOT.\n\n" +
                    "A LIST IS A MACHINE FOR SORTING PEOPLE.\n" +
                    "YOU DO NOT REPAIR THAT MACHINE.\n" +
                    "YOU TAKE IT OFF THE ISLAND'S BOOKS.\n\n" +
                    "THE LOOSE PAGES WILL ROT ON THEIR OWN.\n" +
                    "OUR COUNT DOES NOT NEED THE PAPER.\n\n" +
                    "— THE UNPAID",
            },
            ["manifest_given_note"] = new NoteContent
            {
                Title = "RECEIVED IN FULL",
                Arc = "manifest.given",
                Monument = "unpaid_drop",
                Text =
                    "RECEIVED: ONE MANIFEST, COMPLETE.\n" +
                    "FORTY SEATS. EVERY NAME. EVERY LINE.\n\n" +
                    "THE COMPANY KEPT A LIST OF PEOPLE.\n" +
                    "THE PEOPLE NOW KEEP A LIST OF THE COMPANY.\n" +
                    "IT IS FILED. IT IS DRY. IT IS SAFE —\n" +
                    "WHICH IS MORE THAN IT EVER DID FOR US.\n\n" +
                    "IF A RECKONING COMES TO THIS ISLAND,\n" +
                    "IT WILL COME WITH PAGE NUMBERS.\n\n" +
                    "BACK PAY REMEMBERS ITS FRIENDS, READER.\n\n" +
                    "— THE UNPAID",
            },
            // ===== "Yours, C." notes (v2 Phase H.2) =====
            // C.'s voice is the welcome note's voice: typed, plain, warm,
            // lowercase where The Unpaid go loud. Rumor arc: a letter that
            // came BACK — the island's first proof C. sends more than
            // welcomes, naming nobody.
            ["c_returned_letter"] = new NoteContent
            {
                Title = "a letter, returned",
                Arc = "c.rumors",
                Monument = "c_postbox",
                Text =
                    "Came back to me. Wrong weather for the crossing,\n" +
                    "or the reader before you took the coin and left the words.\n" +
                    "Either way — if you found this, it's yours now.\n\n" +
                    "I write more letters than the one you're thinking of.\n" +
                    "Most don't sign anything. This one doesn't either,\n" +
                    "but you'll know the ribbon by now if you've been paying\n" +
                    "attention, and if you haven't: start. This island\n" +
                    "rewards the ones who read.\n\n" +
                    "Ask the four of them what they keep, what they deny,\n" +
                    "what they collect. Don't ask them what they know.\n" +
                    "Nobody answers that one straight.\n\n" +
                    "    — you know who. or you will.",
            },
            // Beat-published (arc c.letter, day 3-6, after somebody takes the
            // knife job): C. noticed. The letter that proves the press is
            // still warm — without settling whose hands warm it.
            ["c_unsent_letter"] = new NoteContent
            {
                Title = "an unsent letter",
                Arc = "c.letter",
                Monument = "c_postbox",
                Text =
                    "To the one who took the knife job —\n\n" +
                    "I heard. I always hear, eventually. The island is\n" +
                    "small and scrap is loud.\n\n" +
                    "I won't tell you the work gets easier. I'll tell you\n" +
                    "what I told the last one, wipe {wipes} being no\n" +
                    "different: the four of them are worth every errand.\n" +
                    "Mind the one who watches you read this — someone\n" +
                    "always watches, out here.\n\n" +
                    "I started these letters for a reason I've never put\n" +
                    "on paper. Maybe this season somebody finally asks\n" +
                    "the right person the right question.\n\n" +
                    "I never sent this one. If you're holding it,\n" +
                    "I suppose that's a kind of sending.\n\n" +
                    "    — C.",
            },
            // The verdict's aftermath: The Unpaid have watched C.'s paper
            // circulate for years — the only other press that never stopped.
            // ALOUD may name (post-reveal); the other two stay value-safe.
            ["c_read_note"] = new NoteContent
            {
                Title = "A NOTE ON SIGNATURES",
                Arc = "c.read",
                Monument = "unpaid_drop",
                Text =
                    "SO NOW THE ISLAND KNOWS.\n" +
                    "{secret:c_author}. SAY IT PLAIN.\n\n" +
                    "WE HAVE WATCHED THOSE WELCOME NOTES\n" +
                    "CIRCULATE SINCE BEFORE WE HAD A NAME.\n" +
                    "THE ONLY OTHER PRESS THAT NEVER QUIT.\n\n" +
                    "WE DO NOT PRINT KINDNESS OURSELVES.\n" +
                    "DIFFERENT DEPARTMENT.\n" +
                    "BUT WE RESPECT A LONG RUN.\n\n" +
                    "A SIGNATURE IS A KIND OF PAYMENT.\n" +
                    "SOMEBODY FINALLY SETTLED UP.\n\n" +
                    "— THE UNPAID",
            },
            ["c_kept_note"] = new NoteContent
            {
                Title = "A NOTE ON DISCRETION",
                Arc = "c.kept",
                Monument = "unpaid_drop",
                Text =
                    "SOMEBODY KNOWS WHO C. IS NOW.\n" +
                    "AND ISN'T SAYING.\n\n" +
                    "WE COUNT THINGS. IT IS WHAT WE DO.\n" +
                    "COUNT THIS: TWO HUNDRED PEOPLE GOT\n" +
                    "LEFT ON THIS ROCK, AND EXACTLY ONE\n" +
                    "OF THEM JUST PROVED A SECRET CAN\n" +
                    "BE KEPT ON IT.\n\n" +
                    "WHOEVER YOU ARE, READER:\n" +
                    "THE FILE STAYS OPEN.\n" +
                    "OUR RESPECT DOES TOO.\n\n" +
                    "— THE UNPAID",
            },
            ["c_burned_note"] = new NoteContent
            {
                Title = "A NOTE ON ASHES",
                Arc = "c.burned",
                Monument = "unpaid_drop",
                Text =
                    "THE LAST LETTER BURNED SEALED.\n" +
                    "WE SMELLED THE WAX IN THE SMOKE.\n\n" +
                    "UNDERSTAND: WE KEEP RECORDS BECAUSE\n" +
                    "THE COMPANY'S RECORDS LIED.\n" +
                    "BUT A QUESTION NOBODY SELLS,\n" +
                    "NOBODY TRADES, NOBODY SPENDS —\n" +
                    "THAT IS THE ONLY CURRENCY ON THIS\n" +
                    "ISLAND THAT HOLDS ITS VALUE.\n\n" +
                    "C. KEEPS WRITING, OR DOESN'T.\n" +
                    "THE NOTES KEEP COMING, OR WON'T.\n" +
                    "EITHER WAY: FILED UNDER UNRESOLVED,\n" +
                    "WHERE THE BEST THINGS LIVE.\n\n" +
                    "— THE UNPAID",
            },
            // ===== "The Recall" notes (v2 Phase H.3) =====
            // Recall wipes only (arc recall.rumors): evidence, not voice — a
            // sorted pile someone left at the fence line, unsigned. It proves
            // the stockpile is MOVING without pointing at anyone.
            ["recall_casings"] = new NoteContent
            {
                Title = "a sorted handful of casings",
                Arc = "recall.rumors",
                Monument = "magazine",
                Text =
                    "left where the fence used to run. sorted by size,\n" +
                    "which means somebody sat here and sorted them.\n\n" +
                    "every one fired. every one stamped the same way:\n" +
                    "OB- and a batch number, cobalt QA format.\n" +
                    "the newest powder smell is weeks old, not years.\n\n" +
                    "dug ground twenty paces upslope. timber smell.\n" +
                    "cart ruts, loaded going OUT, light coming back.\n\n" +
                    "whoever counts things on this island should\n" +
                    "probably count these.\n\n" +
                    "(not my brass. not my hole. — no signature)",
            },
            // Beat-published (arc recall.map, day 2-4 after Cordwood): the
            // watchers' field sheet on the stamped brass — real gameplay
            // hints in the numbered-run tradition, fresh number per wipe.
            ["recall_fieldnotes"] = new NoteContent
            {
                Title = "FIELD NOTES №{fieldnote}",
                Arc = "recall.map",
                Monument = "unpaid_drop",
                Text =
                    "FIELD NOTES №{fieldnote}: THE RECALL\n" +
                    "WIPE {wipes}. STILL COUNTING.\n\n" +
                    "PICK UP YOUR BRASS. READ THE BOTTOM.\n" +
                    "OB- PREFIX MEANS COMPANY STOCK.\n" +
                    "COMPANY STOCK MEANS SOMEBODY'S SELLING\n" +
                    "WHAT WAS NEVER THEIRS TO SELL.\n" +
                    "IT WAS NEVER THEIRS EITHER. NOTED.\n\n" +
                    "IF YOUR AMMUNITION CAME CHEAP,\n" +
                    "CHECK THE STAMP BEFORE YOU'RE GRATEFUL.\n" +
                    "CHEAP HAS A SOURCE. SOURCES HAVE ROUTES.\n" +
                    "ROUTES HAVE ENDS. WE WALK ROUTES.\n\n" +
                    "THE EXAMINER AT HER FURNACE WANTS THE\n" +
                    "BRASS BACK. GIVE IT TO HER. THAT ONE\n" +
                    "DEBT, AT LEAST, CAN STILL BE PAID.\n\n" +
                    "— THE UNPAID",
            },
            // The verdict's aftermath — all three value-safe (the digger is
            // never named by any script; that mystery belongs to the players).
            ["recall_sealed_note"] = new NoteContent
            {
                Title = "A NOTE ON CLOSED ACCOUNTS",
                Arc = "recall.sealed",
                Monument = "unpaid_drop",
                Text =
                    "THE HOLE IS SHUT. WE FELT THE THUD\n" +
                    "THROUGH THE FLOOR OF WHEREVER WE ARE.\n\n" +
                    "SOME OF US WANTED WHAT WAS DOWN THERE.\n" +
                    "SOME OF US REMEMBER LOADING IT IN.\n" +
                    "BOTH KINDS SLEPT BETTER LAST NIGHT.\n\n" +
                    "AN ARSENAL NOBODY OWNS IS AN ARGUMENT\n" +
                    "NOBODY CAN START. FILED UNDER: SETTLED\n" +
                    "THE WAY THE COMPANY NEVER SETTLED.\n\n" +
                    "THE DIGGER KNOWS WHO THE DIGGER IS.\n" +
                    "SO DOES THE DIRT. THAT IS ENOUGH.\n\n" +
                    "— THE UNPAID",
            },
            ["recall_decommissioned_note"] = new NoteContent
            {
                Title = "A NOTE ON PAPERWORK",
                Arc = "recall.decommissioned",
                Monument = "unpaid_drop",
                Text =
                    "THE COLD ONE HAS THE CHART.\n" +
                    "THERE IS A BENCH AT THE HOLE NOW,\n" +
                    "AND A LEDGER, AND A STAMP.\n\n" +
                    "UNDERSTAND: WE HATE COMPANY PAPERWORK.\n" +
                    "WE ARE COMPANY PAPERWORK, UNFILED.\n" +
                    "BUT WATCHING HER SIGN EVERY CRATE OUT\n" +
                    "PROPERLY — BATCH, COUNT, DISPOSITION —\n" +
                    "IS THE FIRST HONEST AUDIT THIS ISLAND\n" +
                    "HAS EVER SEEN. WE TOOK NOTES.\n\n" +
                    "THE RECALL IS REAL. ONE SIGNATURE\n" +
                    "ON THIS ROCK STILL MEANS SOMETHING.\n\n" +
                    "— THE UNPAID",
            },
            ["recall_armed_note"] = new NoteContent
            {
                Title = "RECEIVED: ONE ARSENAL",
                Arc = "recall.armed",
                Monument = "unpaid_drop",
                Text =
                    "RECEIVED: ONE SITE CHART, COMPANY DRAFTING.\n" +
                    "CONTENTS VERIFIED AGAINST OUR OWN COUNT.\n" +
                    "YES. WE HAD OUR OWN COUNT.\n\n" +
                    "TWO HUNDRED OF US CARRIED CRATES DOWN\n" +
                    "THAT HOLE FOR WAGES THAT NEVER CAME.\n" +
                    "TONIGHT THE CRATES CAME UP FOR FREE.\n\n" +
                    "DO NOT BE AFRAID, READER.\n" +
                    "WE HAVE ALWAYS KNOWN WHERE YOU SLEEP,\n" +
                    "AND YOU HAVE ALWAYS WOKEN UP.\n" +
                    "NOTHING ABOUT THAT CHANGES.\n\n" +
                    "BUT THE NEXT COMPANY BOAT THAT TESTS\n" +
                    "THIS SHORE WILL FIND THE GROUND STAFF\n" +
                    "ARMED, PAID, AND CURRENT ON DOCTRINE.\n\n" +
                    "BACK PAY, READER. IN FULL.\n\n" +
                    "— THE UNPAID",
            },
            // ===== Recovered FIELD NOTES archives (v2 Phase H.5) =====
            // The missing low numbers, published at last — as RECOVERED
            // originals granted by quest claims (resistance.md's hook), not
            // as new circulation prints: the numbering canon holds (№3/№5
            // shipped prints, №6+ the per-wipe ledger, gaps still imply the
            // run). No Arc, no Monument — these exist only as rewards.
            ["archive_sheet_1"] = new NoteContent
            {
                Title = "FIELD NOTES №1 (RECOVERED)",
                Text =
                    "FIELD NOTES №1. THE FIRST SHEET.\n" +
                    "TYPED THE NIGHT THE BOATS LEFT.\n\n" +
                    "WE ARE STILL HERE. COUNT: TWO HUNDRED,\n" +
                    "MINUS THE FORTY THE LIST TOOK.\n\n" +
                    "RULE ONE, WRITTEN BY LANTERN:\n" +
                    "NOBODY IS COMING. GOOD.\n" +
                    "EXPECTING RESCUE IS A LEAK.\n" +
                    "PLUG IT AND YOU FLOAT.\n\n" +
                    "KEEP WARM. KEEP COUNT. KEEP TYPING.\n\n" +
                    "(RECOVERED ORIGINAL. THE RIBBON WAS\n" +
                    "NEW ONCE. SO WERE WE.)\n\n" +
                    "— THE UNPAID",
            },
            ["archive_sheet_2"] = new NoteContent
            {
                Title = "FIELD NOTES №2 (RECOVERED)",
                Text =
                    "FIELD NOTES №2. THE YARD.\n" +
                    "FIRST OBSERVATIONS, UNCONFIRMED.\n\n" +
                    "THE MACHINE WALKS THE LAUNCH ROAD.\n" +
                    "WE TIMED THE LOOP: FOURTEEN MINUTES.\n" +
                    "THEN TWELVE. IT VARIES. NOTED.\n\n" +
                    "IT DOES NOT SLEEP. IT DOES NOT\n" +
                    "RESUPPLY THAT WE HAVE SEEN.\n" +
                    "SOMEBODY PAYS ITS WAY REGARDLESS.\n\n" +
                    "DO NOT ENGAGE. NOT YET.\n" +
                    "SHEET THREE WHEN WE KNOW MORE.\n\n" +
                    "(RECOVERED ORIGINAL. SOMEBODY FINALLY\n" +
                    "FILED THE RECALL. — ADDED LATER, IN PEN)\n\n" +
                    "— THE UNPAID",
            },
            ["archive_sheet_4"] = new NoteContent
            {
                Title = "FIELD NOTES №4 (RECOVERED)",
                Text =
                    "FIELD NOTES №4. WHAT FALLS FROM THE SKY.\n\n" +
                    "THE PLANES STILL COME. NOBODY'S PLANES.\n" +
                    "CRATES ON SMOKE, PAID FOR BY NO ONE,\n" +
                    "ADDRESSED TO WHOEVER RUNS FASTEST.\n\n" +
                    "RULE: THE CRATE IS BAIT AND PRIZE BOTH.\n" +
                    "COUNT WHO ELSE IS RUNNING BEFORE YOU\n" +
                    "RUN. ARRIVE SECOND. LEAVE FIRST.\n\n" +
                    "THE COMPANY TAUGHT US LOGISTICS.\n" +
                    "WE ARE USING THEM.\n\n" +
                    "(RECOVERED ORIGINAL.)\n\n" +
                    "— THE UNPAID",
            },
            // ===== "The Count" notes (v2 Phase H.4) =====
            // Count wipes only (arc count.rumors): the wall itself, described
            // by whoever found it — evidence of many hands, long spans, and
            // at least one scrubber (the eraser is real before she's met).
            ["count_wall"] = new NoteContent
            {
                Title = "marks on the tank wall",
                Arc = "count.rumors",
                Monument = "tally_wall",
                Text =
                    "somebody counts here. more than one somebody.\n\n" +
                    "rows of marks, different heights, different tools —\n" +
                    "chalk, knife, paint, one row punched clean like a die.\n" +
                    "the low rows are oldest. rain has eaten the first ones\n" +
                    "to shadows. you can't total what the rain took.\n\n" +
                    "and someone SCRUBS. whole rows gone to bright metal,\n" +
                    "wire-brushed, recent. counting and uncounting,\n" +
                    "on the same wall, for years.\n\n" +
                    "counted the newest row myself. then counted again\n" +
                    "and got a different number. maybe that's the wall.\n" +
                    "maybe that's this place.\n\n" +
                    "(don't take my word. bring your own chalk.\n" +
                    "— no signature)",
            },
            // Beat-published (arc count.sheet, day 2-5 after Good Soil): THE
            // sheet — the one that reads the island's memory aloud. Every
            // memory token, quoted in the watchers' own voice.
            ["count_sheet_note"] = new NoteContent
            {
                Title = "THE COUNT, PUBLISHED",
                Arc = "count.sheet",
                Monument = "unpaid_drop",
                Text =
                    "THE COUNT, PUBLISHED IN FULL.\n" +
                    "THIS IS WIPE {wipes}. WRITE IT SOMEWHERE.\n" +
                    "YOU WILL NOT REMEMBER THAT YOU KNEW.\n\n" +
                    "THE SEASON BEFORE THIS ONE IS REMEMBERED\n" +
                    "AS '{lastwipe.template}'.\n" +
                    "IT ENDED WITH {lastwipe.choice}.\n" +
                    "THAT IS WHAT SURVIVES A RESET:\n" +
                    "ONE NAME. ONE OUTCOME. OUR SHEET.\n\n" +
                    "WE DO NOT COUNT TO MOURN, READER.\n" +
                    "WE COUNT BECAUSE THE COMPANY SAID\n" +
                    "NOBODY WOULD BE KEEPING RECORDS.\n\n" +
                    "TALLY WALLS ARE FOR AMATEURS.\n" +
                    "WE KEEP FILES. THE FILES AGREE\n" +
                    "WITH THIS SHEET. MOSTLY.\n\n" +
                    "WIPE {wipes}. STILL HERE. STILL COUNTING.\n\n" +
                    "— THE UNPAID",
            },
            // The verdict's aftermath — reconcile/give state the truth
            // (post-reveal); stopped keeps it forever.
            ["count_reconciled_note"] = new NoteContent
            {
                Title = "AN AUDIT OF AUDITS",
                Arc = "count.reconciled",
                Monument = "unpaid_drop",
                Text =
                    "TWO COUNTS, POSTED SIDE BY SIDE.\n" +
                    "OURS, AND THE FOUNDERS' BOARD.\n" +
                    "WE CHECKED EACH AGAINST THE OTHER\n" +
                    "THE WAY THE COMPANY NEVER LET US\n" +
                    "CHECK A PAYSLIP.\n\n" +
                    "FINDING: THE TRUTH IS\n" +
                    "{secret:true_count}.\n\n" +
                    "WE AMEND OUR FILES WITHOUT SHAME.\n" +
                    "AN HONEST LEDGER IS ONE THAT CAN\n" +
                    "SURVIVE BEING READ.\n" +
                    "THE COMPANY'S COULDN'T. OURS CAN.\n\n" +
                    "WIPE {wipes}, RECONCILED.\n\n" +
                    "— THE UNPAID",
            },
            ["count_given_note"] = new NoteContent
            {
                Title = "RECEIVED: THE EARLY MARKS",
                Arc = "count.given",
                Monument = "unpaid_drop",
                Text =
                    "RECEIVED: ONE BOARD. FOUR HANDS.\n" +
                    "THE MARKS FROM BEFORE OUR FIRST SHEET.\n\n" +
                    "WE HAVE COUNTED SINCE THE BOATS LEFT.\n" +
                    "WE DID NOT KNOW ANYONE COUNTED BEFORE US.\n" +
                    "THE LINE RUNS BACK TO THE FIRST MARK NOW,\n" +
                    "AND THE WHOLE OF IT SAYS:\n" +
                    "{secret:true_count}.\n\n" +
                    "TO THE FOUR WHO CUT THESE:\n" +
                    "YOU WERE KEEPING OUR RECORDS\n" +
                    "BEFORE WE EXISTED TO KEEP THEM.\n" +
                    "THAT IS THE ONLY BACK PAY\n" +
                    "ANYONE EVER PAID US IN ADVANCE.\n\n" +
                    "— THE UNPAID",
            },
            ["count_stopped_note"] = new NoteContent
            {
                Title = "A NOTE ON THE ONE NUMBER",
                Arc = "count.stopped",
                Monument = "unpaid_drop",
                Text =
                    "THE FOUNDERS' BOARD IS ASH.\n" +
                    "WE FELT THAT ONE, READER.\n" +
                    "NOT THE FIRE. THE ARITHMETIC.\n\n" +
                    "THERE IS ONE COUNT ON THIS ISLAND NOW.\n" +
                    "OURS. UNCHECKABLE. WHICH MEANS:\n" +
                    "TRUSTED, OR NOTHING.\n\n" +
                    "UNDERSTAND WHAT YOU BOUGHT WITH THAT FIRE.\n" +
                    "NOT FORGETTING. NOBODY FORGETS HERE,\n" +
                    "THEY ONLY LOSE THE PROOF.\n" +
                    "YOU BOUGHT US THE LAST WORD.\n\n" +
                    "WE WILL TRY TO DESERVE IT.\n" +
                    "WIPE {wipes}. STILL COUNTING.\n" +
                    "SOMEBODY HAS TO, NOW.\n\n" +
                    "— THE UNPAID",
            },
            // The Press wipes only (arc press.rumors): an outside voice who
            // NOTICED — handwritten, not typed, deliberately unsigned. Its
            // job is to teach the deduction game: warm someone up and listen.
            ["press_watchers"] = new NoteContent
            {
                Title = "an unsigned scrawl",
                Arc = "press.rumors",
                Monument = "press_rumor",
                Text =
                    "not typed. written. there's a difference and the difference matters.\n\n" +
                    "somebody on this island PRINTS. the sheets in the crates are too clean, too regular, too fed. " +
                    "a press needs ink, paper, a roller, a dry room and steady hands. that is a short list of people.\n\n" +
                    "i watch the ones who sell. watch their hands. watch who never asks what the sheets say — everyone asks, unless they already know.\n\n" +
                    "they talk more when they trust you. do the work, earn the warmth, and listen to what falls out.\n\n" +
                    "(no signature. i'm not stupid.)",
            },
            // ===== Delayed pieces (v2 Phase I.5) =====
            // Granted directly to the picker days after the verdict, never
            // placed — like the archive sheets, no Arc needed. House rule of
            // the set: aggression is UNSIGNED and deniable; kindness signs.
            //
            // Press expose, day 1 — the evening before the collections crew
            // (I.6) calls. The one sheet that never says THE UNPAID.
            ["unpaid_warning"] = new NoteContent
            {
                Title = "NO SIGNATURE",
                Text =
                    "NO SIGNATURE ON THIS SHEET.\n" +
                    "YOU WILL WORK OUT WHY.\n\n" +
                    "YOU SAID A NAME AT A COUNTER.\n" +
                    "BY MORNING IT WAS EVERYWHERE.\n" +
                    "NAMES HAVE WEIGHT.\n" +
                    "WEIGHT ROLLS DOWNHILL.\n\n" +
                    "WHAT COMES NEXT IS NOT US.\n" +
                    "THERE IS NO US.\n" +
                    "BUT A SHEET CAN BE A COURTESY.\n" +
                    "THIS IS ONE.\n\n" +
                    "LOCK YOUR DOOR. COUNT YOUR FRIENDS.\n\n" +
                    "SLEEP LIGHTLY, READER.",
            },
            // Press expose, days after the crew called (I.6, AfterVisit) —
            // the sheets deny the night ever happened. SIGNED, because a
            // denial is official correspondence.
            ["unpaid_denial"] = new NoteContent
            {
                Title = "CLARIFICATION, FOR THE RECORD",
                Text =
                    "CLARIFICATION, FOR THE RECORD.\n\n" +
                    "A RUMOR SAYS THREE ARMED PERSONS\n" +
                    "IN COMPANY HAZMAT SUITS\n" +
                    "CALLED ON A READER BY NIGHT.\n\n" +
                    "WE HAVE REVIEWED OUR FILES.\n" +
                    "NO SUCH ORDER EXISTS.\n" +
                    "NO SUCH CREW EXISTS.\n" +
                    "WE DO NOT HAVE MEMBERS.\n" +
                    "WE CERTAINLY DO NOT HAVE CONTRACTORS.\n\n" +
                    "THE COMPANY LEFT ITS SUITS BEHIND.\n" +
                    "ANYONE CAN WEAR A SUIT.\n\n" +
                    "WE ARE GLAD YOU ARE WELL, READER.\n" +
                    "WE HAVE NO OPINION ON WHY.\n\n" +
                    "— THE UNPAID",
            },
            // Press protect — BACK PAY FINDS PEOPLE LIKE YOU, made literal.
            ["unpaid_care_press"] = new NoteContent
            {
                Title = "BACK PAY — SUPPLEMENTAL",
                Text =
                    "YOU KNEW A NAME. YOU KEPT IT.\n" +
                    "THE COMPANY SOLD EVERY NAME IT EVER HELD.\n" +
                    "YOU ARE NOT THE COMPANY.\n" +
                    "NOTED. FILED. REMEMBERED.\n\n" +
                    "THE SHEETS SAID IT FIRST:\n" +
                    "BACK PAY FINDS PEOPLE LIKE YOU.\n" +
                    "THIS IS WHAT THAT MEANS.\n\n" +
                    "MEDICINE KEEPS. FUEL BURNS.\n" +
                    "BOTH ARE YOURS.\n\n" +
                    "DO NOT THANK US. THERE IS NO US.\n\n" +
                    "— THE UNPAID",
            },
            // Manifest give — the archive pays its sources.
            ["unpaid_care_manifest"] = new NoteContent
            {
                Title = "RECEIPT OF FILE",
                Text =
                    "THE FOLDER ARRIVED. PAGES COMPLETE.\n" +
                    "FORTY SEATS. TWO HUNDRED NAMES.\n" +
                    "YOU GAVE THE LIST\n" +
                    "TO THE PEOPLE WHO WERE LEFT OFF IT.\n\n" +
                    "THE COMPANY KEPT A MANIFEST OF PEOPLE.\n" +
                    "NOW THE PEOPLE KEEP ONE OF THE COMPANY.\n" +
                    "YOUR NAME APPEARS IN OURS.\n" +
                    "UNDER ASSETS.\n\n" +
                    "BACK PAY FINDS PEOPLE LIKE YOU.\n\n" +
                    "— THE UNPAID",
            },
            // Count give — the line runs back to the first winter now.
            ["unpaid_care_count"] = new NoteContent
            {
                Title = "THE LINE IS WHOLE",
                Text =
                    "THE EARLY MARKS ARE FILED.\n" +
                    "THE LINE RUNS BACK TO THE FIRST WINTER.\n" +
                    "NOBODY ALIVE REMEMBERS IT.\n" +
                    "THE FILE DOES. NOW.\n\n" +
                    "YOU MADE THE COUNT WHOLE.\n" +
                    "THE COUNT KEEPS WHAT IT IS GIVEN.\n" +
                    "THAT INCLUDES WHO GAVE.\n\n" +
                    "BACK PAY FINDS PEOPLE LIKE YOU.\n\n" +
                    "— THE UNPAID",
            },
            // Count burn — the debt payment. They didn't ask for the last
            // word; they pay for it anyway, itemized (217 matches the piece's
            // scrap to the unit — a number nobody would round to).
            ["unpaid_debt"] = new NoteContent
            {
                Title = "FINAL PAY PERIOD",
                Text =
                    "ITEMIZED. FINAL PAY PERIOD. IN FULL.\n\n" +
                    "YOU BURNED THE OTHER NUMBER.\n" +
                    "NOW THIS ISLAND KEEPS ONE COUNT.\n" +
                    "OURS.\n\n" +
                    "WE DID NOT ASK FOR THAT.\n" +
                    "ASKING LEAVES RECORDS.\n" +
                    "NEVERTHELESS: WORK OCCURRED.\n" +
                    "A BALANCE EXISTED. IT IS SETTLED.\n\n" +
                    "217 SCRAP. COUNT IT IF YOU LIKE.\n" +
                    "WE DID. TWICE.\n\n" +
                    "WE PAY OUR DEBTS. DO NOT REFUSE.\n\n" +
                    "— THE UNPAID",
            },
            // Yours, C., read-alone — the second envelope. Typed years ago
            // for whichever reader kept the first one quiet, so it stays TRUE
            // under all three c_author rolls (the H.2 rule): it never claims
            // C. still writes, and the fresh pencil on the envelope is the
            // keeper's hand, who exists on every roll.
            ["c_private_letter"] = new NoteContent
            {
                Title = "for the one who read it alone",
                Text =
                    "this sheet was typed years before you found it,\n" +
                    "for whichever reader opened the last envelope\n" +
                    "and told no one. if it is in your hands,\n" +
                    "that is who you turned out to be.\n\n" +
                    "i could not know your name. i knew your kind.\n" +
                    "someone always carries the quiet version\n" +
                    "of a thing, so the loud version\n" +
                    "never has to happen.\n\n" +
                    "what you learned, you carried gently.\n" +
                    "that is all any letter can ask.\n\n" +
                    "no more letters after this one.\n" +
                    "the rest is yours to write.\n\n" +
                    "keep the ribbon inked.\n\n" +
                    "    yours, c.",
            },
            ["shop_rumor"] = new NoteContent
            {
                Title = "A Hunter's Rumor",
                Text = "If you're reading this, I'm probably dead — the old croc got me, or the cats did.\n\nThere's a railcar out in the green with its lights still on. Woman runs it. She'll take your scrap at the machines and hand it back if you can hunt.\n\nFind her before the island finds you.\n\n    — R.",
            },
        };

        // The old crew's share — worth the whole chain (docs/content/sonia-arc.md).
        private Dictionary<string, StashContent> DefaultStashes() => new Dictionary<string, StashContent>
        {
            [FinaleStashTag] = new StashContent
            {
                Loot = new List<RewardDef>
                {
                    new RewardDef { Item = "scrap", Amount = 500 },
                    new RewardDef { Item = "rifle.ak", Amount = 1, Attachments = new List<string> { "weapon.mod.flashlight", "weapon.mod.holosight", "weapon.mod.extendedmags" } },
                    new RewardDef { Item = "ammo.rifle", Amount = 250 },
                    new RewardDef { Item = "metal.facemask", Amount = 1 },
                    new RewardDef { Item = "metal.plate.torso", Amount = 1 },
                    new RewardDef { Item = "largemedkit", Amount = 4 },
                    new RewardDef { Item = "supply.signal", Amount = 2 },
                },
            },
            // Olivia's severance: the munitions shipment she walked out with
            // (owner's amounts, v1.8.0 — a raid kit worthy of a whole chain).
            ["olivia_finale"] = new StashContent
            {
                Loot = new List<RewardDef>
                {
                    new RewardDef { Item = "scrap", Amount = 500 },
                    new RewardDef { Item = "explosive.timed", Amount = 20 },
                    new RewardDef { Item = "ammo.rocket.basic", Amount = 20 },
                    new RewardDef { Item = "explosives", Amount = 100 },
                    new RewardDef { Item = "gunpowder", Amount = 2000 },
                    new RewardDef { Item = "techparts", Amount = 50 },
                },
            },
            // Alexa's: the crate that wasn't hers, from the launch-site motor pool.
            // The motor pool's master set (owner, v1.7.0): the T3 bench and every
            // workbench upgrade the game ships, plus the 500 scrap every cellar
            // carries (v1.7.1). Her own 300 comes from her hand on the claim.
            ["alexa_finale"] = new StashContent
            {
                Loot = new List<RewardDef>
                {
                    new RewardDef { Item = "scrap", Amount = 500 },
                    new RewardDef { Item = "workbench3", Amount = 1 },
                    new RewardDef { Item = "workbench.upgrade.accelerated", Amount = 1 },
                    new RewardDef { Item = "workbench.upgrade.comfort", Amount = 1 },
                    new RewardDef { Item = "workbench.upgrade.defensive", Amount = 1 },
                    new RewardDef { Item = "workbench.upgrade.efficiency", Amount = 1 },
                    new RewardDef { Item = "workbench.upgrade.prototype", Amount = 1 },
                    new RewardDef { Item = "workbench.upgrade.range", Amount = 1 },
                    new RewardDef { Item = "workbench.upgrade.recyclebin", Amount = 1 },
                    new RewardDef { Item = "workbench.upgrade.reinforced", Amount = 1 },
                    new RewardDef { Item = "workbench.upgrade.salvage", Amount = 1 },
                    new RewardDef { Item = "workbench.upgrade.surplus", Amount = 1 },
                },
            },
            // Rebecca's: the greenhouse seed stock and the gear that ran it.
            ["rebecca_finale"] = new StashContent
            {
                // The whole cellar (owner, v1.6.3): the greenhouse power kit, her
                // insomnia tea, and the 500 scrap every cellar carries (v1.7.1).
                // Her own 300 comes from her hand on the claim — and the old seed
                // stock, clones, planters, fertiliser, sprinklers and fuel
                // generator are gone, so her chain pays farming and her cellar
                // pays power. Her finale dialog says so; keep the two in step.
                Loot = new List<RewardDef>
                {
                    new RewardDef { Item = "scrap", Amount = 500 },
                    new RewardDef { Item = "generator.water", Amount = 1 },
                    new RewardDef { Item = "generator.wind.scrap", Amount = 1 },
                    new RewardDef { Item = "electric.generator.small", Amount = 1 },
                    new RewardDef { Item = "healingtea.advanced", Amount = 5 },
                    new RewardDef { Item = "maxhealthtea.advanced", Amount = 5 },
                },
            },
            // The vault: four chains earned this (owner's spec, 2026-07-26).
            // NOTE: no "L9" pistol exists on this build — rifle.l96 (L96 Rifle)
            // stands in; swap the shortname here if a different gun was meant.
            ["grand_finale"] = new StashContent
            {
                Loot = new List<RewardDef>
                {
                    new RewardDef { Item = "bdu.shirt", Amount = 1 },
                    new RewardDef { Item = "bdu.pants", Amount = 1 },
                    new RewardDef { Item = "shoes.boots", Amount = 1 },
                    new RewardDef { Item = "ballistic.helmet", Amount = 1 },
                    new RewardDef { Item = "ballistic.vest", Amount = 1 },
                    new RewardDef { Item = "ballistic.legarmor", Amount = 1 },
                    new RewardDef { Item = "rifle.l96", Amount = 1, Attachments = new List<string> { "weapon.mod.lasersight", "weapon.mod.extendedmags", "weapon.mod.8x.scope" } },
                    new RewardDef { Item = "lmg.m249", Amount = 1, Attachments = new List<string> { "weapon.mod.lasersight", "weapon.mod.holosight" } },
                    new RewardDef { Item = "shotgun.m4", Amount = 1, Attachments = new List<string> { "weapon.mod.lasersight", "weapon.mod.extendedmags", "weapon.mod.holosight" } },
                    new RewardDef { Item = "ammo.rifle", Amount = 200 },
                    new RewardDef { Item = "ammo.shotgun", Amount = 100 },
                    new RewardDef { Item = "scrap", Amount = 1000 },
                },
            },
        };

        #endregion

        #region Data — player progress

        private class PlayerProgress
        {
            public string WipeId;
            public Dictionary<string, List<int>> Active = new Dictionary<string, List<int>>();
            public List<string> Completed = new List<string>();
            // Traders who've been vouched for, earned one chain at a time
            // (v1.5). Wipes with the rest of progress — every wipe you walk up
            // to three strangers again. Never null: a pre-v1.5 progress file has
            // no such key, and IsTraderUnlocked re-derives from Completed anyway.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> UnlockedTraders = new List<string>();
            // What each trader remembers about this player from SLM chat
            // sessions (v1.9) — one distilled sentence per session, newest
            // last, capped. Lives here so it wipes with everything else.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, List<string>> TraderMemories = new Dictionary<string, List<string>>();
            // plant objectives (v2 Phase B): which monuments this player has
            // already planted a pamphlet at, per "questId:objIdx" — the stored
            // counter is just this list's length, distinctness lives here.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, List<string>> PlantLedger = new Dictionary<string, List<string>>();
            // The late-wipe choice (v2 Phase G): choiceId -> optionId. Each
            // player picks once at the prompt quest's claim; wipes with the
            // rest of progress.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> Choices = new Dictionary<string, string>();
            // When each pick landed, ISO-8601 UTC (I.5) — the anchor the
            // delayed pieces count their real days from. A pick recorded
            // before this field shipped gets stamped at first delivery
            // check, so nothing ever arrives instantly.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> ChoicePickedUtc = new Dictionary<string, string>();
            // Delivered delayed pieces, "<choiceId>:<pieceId>" (I.5) — the
            // once-each ledger; due days are derived, never stored.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> DeliveredPieces = new List<string>();
            // The collections crew's ledger (I.6): choiceId -> UTC of the
            // night they called. One visit per wipe, and the anchor the
            // AfterVisit pieces (the denial sheet) count from.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> VisitsDone = new Dictionary<string, string>();
        }

        private readonly Dictionary<ulong, PlayerProgress> _progress = new Dictionary<ulong, PlayerProgress>();
        private readonly HashSet<ulong> _progressDirty = new HashSet<ulong>();

        private PlayerProgress GetProgress(BasePlayer player, bool create = true)
        {
            var id = (ulong)player.userID;
            if (_progress.TryGetValue(id, out var p)) return p;

            // ExistsDatafile first: ReadObject CREATES the file when missing, so
            // reading unconditionally wrote a progress file for every connecting
            // player whether or not they ever spoke to the trader.
            try
            {
                p = Interface.Oxide.DataFileSystem.ExistsDatafile($"{DataRoot}/players/{id}")
                    ? Interface.Oxide.DataFileSystem.ReadObject<PlayerProgress>($"{DataRoot}/players/{id}")
                    : null;
            }
            catch (Exception e)
            {
                PrintError($"players/{id}.json failed to parse ({e.Message}) — {player.displayName} starts this wipe's chain fresh.");
                p = null;
            }
            if (p == null || p.WipeId != _state.WipeId)
            {
                if (!create && p == null) return null;
                p = new PlayerProgress { WipeId = _state.WipeId }; // fresh wipe = fresh chain
            }
            if (p.UnlockedTraders == null) p.UnlockedTraders = new List<string>(); // explicit null in an old/edited file
            if (p.TraderMemories == null) p.TraderMemories = new Dictionary<string, List<string>>();
            if (p.PlantLedger == null) p.PlantLedger = new Dictionary<string, List<string>>();
            if (p.Choices == null) p.Choices = new Dictionary<string, string>();
            if (p.ChoicePickedUtc == null) p.ChoicePickedUtc = new Dictionary<string, string>();
            if (p.DeliveredPieces == null) p.DeliveredPieces = new List<string>();
            if (p.VisitsDone == null) p.VisitsDone = new Dictionary<string, string>();
            _progress[id] = p;
            // Fold into the wipe-wide completion ledger (Phase F) — the
            // catch-up path for claims made before the ledger shipped.
            for (var i = 0; i < p.Completed.Count; i++)
                MarkWipeCompleted(p.Completed[i]);
            return p;
        }

        // A beat gate asks "has ANYONE done this yet" — answered here without
        // touching a single progress file at tick time.
        private void MarkWipeCompleted(string questId)
        {
            if (string.IsNullOrEmpty(questId) || _state.WipeCompleted.Contains(questId)) return;
            _state.WipeCompleted.Add(questId);
            _stateDirty = true;
        }

        private void MarkProgressDirty(BasePlayer player) => _progressDirty.Add((ulong)player.userID);

        private void SaveDirtyProgress()
        {
            foreach (var id in _progressDirty)
                if (_progress.TryGetValue(id, out var p))
                    Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/players/{id}", p);
            _progressDirty.Clear();
            if (_stateDirty) SaveState();
        }

        #endregion

        #region Lifecycle

        private bool _wipeDetected;
        private readonly HashSet<ulong> _dialogOpen = new HashSet<ulong>();
        private readonly Dictionary<ulong, string> _dialogTrader = new Dictionary<ulong, string>(); // who they're talking to
        private Timer _saveTimer;
        private Timer _rangeTimer;
        private Timer _lightTimer;
        private Timer _visitTimer;

        private void Init()
        {
            permission.RegisterPermission(_config.AdminPermission, this);
            // Hot/conditional hooks stay unsubscribed until something needs them.
            Unsubscribe(nameof(OnEntityTakeDamage));
            Unsubscribe(nameof(OnNpcConversationStart));
            Unsubscribe(nameof(OnEntityDeath));
            Unsubscribe(nameof(OnCodeEntered));
            Unsubscribe(nameof(OnLootEntity));
            Unsubscribe(nameof(OnItemAddedToContainer));
            Unsubscribe(nameof(OnPlayerInput));
            Unsubscribe(nameof(CanBuild));
            Unsubscribe(nameof(OnPlayerChat)); // armed only while a game-chat SLM session runs
            Unsubscribe(nameof(OnLootSpawn));  // armed in OnServerInitialized once the pamphlet cache proves non-empty
            Unsubscribe(nameof(CanLootEntity));  // armed only while a barred player is online (Phase I)
            Unsubscribe(nameof(CanMountEntity)); // ditto
            Unsubscribe(nameof(OnTechTreeNodeUnlock)); // armed in OnServerInitialized when any TechTreeLock* option is on
            Unsubscribe(nameof(CanCraft)); // armed in OnServerInitialized when any WorkbenchGate* option is set
            // OnPlayerSleepEnded stays subscribed even with WelcomeNote off —
            // it also retries stranded unpaid auto-claims (v2.3.5); the
            // welcome gating lives inside TryGiveWelcomeNote.
            Puts($"RustQuests v{Version} loaded - by LowPopLabs - ko-fi.com/lowpoplabs");
        }

        // Fires on the first boot after a map wipe, before OnServerInitialized.
        private void OnNewSave(string filename) => _wipeDetected = true;

        private void OnServerInitialized()
        {
            LoadState();

            // A wipe is either OnNewSave firing or the map changing under us.
            // MapKey is the map-change detector (pre-2.20 state has none: the
            // WipeId prefix was the map until decision 0006 made WipeId name
            // the SEASON, which may outlive the map). The counter carries
            // across a reset so the new id can never collide with the old one
            // (a same-seed wipe would otherwise leave every player's progress
            // looking current).
            if (_state.MapKey == null && _state.WipeId != null)
            {
                var dash = _state.WipeId.LastIndexOf('-');
                _state.MapKey = dash > 0 ? _state.WipeId.Substring(0, dash) : MapId();
            }
            var mapChanged = _state.MapKey != null && _state.MapKey != MapId();
            var freshMap = false;    // the world (shops/stashes/notes/drops) must be rebuilt
            if (_wipeDetected || mapChanged)
            {
                freshMap = true;
                var nextCounter = _state.WipeCounter + 1;
                string why;
                if (SeasonCarries(out why))
                {
                    CarrySeason(nextCounter);
                    DLog($"Wipe detected (was '{_state.WipeId}') — season CARRIED ({_state.CarriedWipes}/{_config.SeasonCarryOverMaxWipes}): {why}. Story and progress stand; the world re-places on this map.");
                }
                else
                {
                    DLog($"Wipe detected (was '{_state.WipeId}') — season ends ({why}): resetting world state and player progress.");
                    _state = FreshWipeState(nextCounter); // carries DataVersion + cross-wipe memory, distils the old bible
                }
            }
            if (_state.WipeId == null) _state.WipeId = WipeIdFor(_state.WipeCounter); // first boot of a season — never recomputed after (the map may change under a carried season)
            if (_state.MapKey == null) _state.MapKey = MapId();
            var freshSeason = _state.QuestTree == null; // null = state was just reset (or first-ever boot)
            if (freshSeason)
            {
                freshMap = true;
                _state.QuestTree = DetectQuestTree();
                DLog($"Quest tree for this wipe: {_state.QuestTree}.");
            }
            SaveState();

            LoadTraders();
            MigrateDataFiles(); // v2 Arc stamp — MUST precede the quest merge or every quest doubles
            LoadTemplates();
            LoadOverlays();
            LoadQuestDefs();
            LoadWorldContent();
            var noteDef = ItemManager.FindItemDefinition("note");
            _noteItemId = noteDef != null ? noteDef.itemid : -1;
            var photoDef = ItemManager.FindItemDefinition("photo");
            _photoItemId = photoDef != null ? photoDef.itemid : -1;

            LoadPamphletCache();
            LoadPamphletManifest();
            if (_config.PamphletLootChance > 0f && _pamphletCache.Count > 0)
                Subscribe(nameof(OnLootSpawn));

            if (_config.TechTreeLockWb1 || _config.TechTreeLockWb2 || _config.TechTreeLockWb3 || _config.TechTreeLockEng)
                Subscribe(nameof(OnTechTreeNodeUnlock));

            if (AnyWorkbenchGate())
            {
                Subscribe(nameof(CanCraft));
                Subscribe(nameof(CanDeployItem)); // RecomputeProtectionHook keeps it armed too
                // A typo'd trader key would silently gate a bench forever —
                // say so at boot instead.
                foreach (var gate in new[] { _config.WorkbenchGateWb1, _config.WorkbenchGateWb2, _config.WorkbenchGateWb3, _config.WorkbenchGateEng })
                    if (!string.IsNullOrWhiteSpace(gate) && Profile(gate) == null)
                        PrintWarning($"WorkbenchGate names unknown trader '{gate}' — that bench can never unlock. Valid keys: the trader keys in traders.json.");
            }

            // Roll (or replay) this wipe's story before any world placement —
            // the arc filter decides which quests exist, and the stash scanner
            // reads the filtered set. A generator throw must not kill the
            // engine: fall back to four_keys, no remix (= exactly v1).
            try
            {
                if (_state.Bible == null)
                {
                    GenerateWipeBible(freshSeason);
                    SaveState();
                }
            }
            catch (Exception e)
            {
                PrintError($"Wipe bible generation failed ({e.Message}) — running four_keys with no remix.\n{e}");
                _state.Bible = FallbackBible();
                SaveState();
            }
            if (_carriedThisBoot)
            {
                // Phase S: the story stands, the map doesn't — rebind the
                // monuments now that templates are loaded. Its own try: a
                // survey throw must cost the bindings, never the carried story.
                try { RebindMonumentsForCarry(); }
                catch (Exception e) { PrintWarning($"Monument rebind after the carried wipe failed ({e.Message}) — bindings empty, content falls back to random spots; rq.wipe.carry re-runs it."); }
                SaveState();
            }
            _carriedThisBoot = false;
            ApplyBible();

            // World setup can throw on hand-edited JSON or odd terrain. If it
            // does, the engine hooks below MUST still be wired — otherwise the
            // whole quest system is silently dead until the next restart.
            try
            {
                // Stash first: notes bake {grid}/{code} into their text at spawn,
                // so it has to exist before any note is written. Drops next
                // (same reason won't apply, but boxes should exist before the
                // notes that point at them); monument notes last.
                EnsureAllFinaleStashes();
                EnsureAllDrops();
                EnsurePlacedNotes();
                SpawnWorldObjects();
                RebindShops();
                foreach (var kv in _state.Shops)
                    if (kv.Value.TraderPlaced && kv.Value.Trader == null)
                        SpawnTrader(kv.Key, kv.Value.TraderPos, kv.Value.TraderYaw);

                // Fresh map -> place every trader's shop somewhere new. Prefab
                // mode is always ready; paste mode also needs CopyPaste + file.
                if (freshMap && _config.AutoPlaceOnWipe)
                {
                    timer.Once(60f, AutoPlaceMissingShops);
                }
            }
            catch (Exception e)
            {
                PrintError($"World setup failed ({e.Message}) — quests still run; fix and use rq.shop.autoplace / rq.stash.reroll.\n{e}");
            }

            // Oxide does not replay OnPlayerConnected for players already online,
            // so prime their progress before deciding which hooks to subscribe —
            // otherwise a hot reload silently stops crediting their kills.
            foreach (var p in BasePlayer.activePlayerList)
                if (p != null && !p.IsNpc) GetProgress(p);

            RecomputeEngineHooks();
            RecomputeWorldHooks();
            RecomputeProtectionHook();
            _saveTimer = timer.Every(300f, SaveDirtyProgress);

            // Beats (v2 Phase F): catch up anything past-due now that the
            // world exists, then keep the 5-minute clock only while a beat
            // is still pending.
            BeatTick();
            RecomputeBeatTimer();
            RecomputeGrudgeHooks(); // arm the gambling lockout if a barred player is already on
            RecomputePieceTimer();  // and the mail clock if someone online is still owed a piece (I.5)
            RecomputeCrewTimer();   // and the night watch if someone online is still owed a visit (I.6)

            // Keep the shop lights lit: a 60s parity check per shop. The
            // caboose's lighting is a two-way switch circuit (see
            // SwitchOnShopLights) — lit is a property of the switch PAIR, so
            // the heal toggles one switch only when parity is even (dark) and
            // otherwise does nothing. A player flipping switches is fine: any
            // state they leave lit is odd parity and stays untouched; any
            // dark state heals within a minute.
            if (_config.ShopLightsOn)
                _lightTimer = timer.Every(60f, () =>
                {
                    foreach (var key in new List<string>(_state.Shops.Keys))
                        if (Shop(key).ShopEnt != null) SwitchOnShopLights(key, false);
                });
        }

        private void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(p, UiPanel);
                CuiHelper.DestroyUi(p, JournalPanel);
                CuiHelper.DestroyUi(p, ChatPanel);
            }
            _dialogOpen.Clear();
            _chatSessions.Clear(); // no distill on unload — an in-flight callback must not touch a dead instance
            DespawnAllTraders();
            DespawnWorldObjects(); // pasted builds persist (engine-saved); everything else respawns on load
            foreach (var kv in _state.Shops)
            {
                if (kv.Value.ShopEnt != null && !kv.Value.ShopEnt.IsDestroyed) kv.Value.ShopEnt.Kill(); // prefab shells
                if (kv.Value.Marker != null && !kv.Value.Marker.IsDestroyed) kv.Value.Marker.Kill();
            }
            if (_devEnt != null && !_devEnt.IsDestroyed) _devEnt.Kill();      // dev probe spawn
            _devEnt = null;
            _pasteBuf = null;  // an in-flight CopyPaste callback must not touch a dead instance
            _saveTimer?.Destroy();
            _visitTimer?.Destroy();
            _rangeTimer?.Destroy();
            _lightTimer?.Destroy();
            _pieceTimer?.Destroy();
            _crewTimer?.Destroy();
            _crewEndTimer?.Destroy();
            DespawnCrew(); // the visit never survives its plugin
            SaveDirtyProgress();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            GetProgress(player, create: false);
            RecomputeEngineHooks();
            RecomputeGrudgeHooks();
            RecomputePieceTimer();
            RecomputeCrewTimer();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            _dialogOpen.Remove((ulong)player.userID);
            var id = (ulong)player.userID;
            EndChatSession(id); // before the progress eviction below — the distill callback re-writes the file itself
            if (_progressDirty.Contains(id) && _progress.TryGetValue(id, out var p))
            {
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/players/{id}", p);
                _progressDirty.Remove(id);
            }
            _progress.Remove(id);
            RecomputeEngineHooks();
            RecomputeGrudgeHooks();
            RecomputePieceTimer();
            // The appointment left mid-visit: the crew has no one to menace.
            // The ledger was claimed at spawn — they came; that's the visit.
            if (_crewActive.Count > 0 && id == _crewTargetId) EndVisit(false);
            RecomputeCrewTimer();
        }

        #endregion

        #region Trader NPC

        private void SpawnTrader(string key, Vector3 pos, float yaw)
        {
            var profile = Profile(key);
            if (profile == null) { PrintError($"No trader profile '{key}' in traders.json."); return; }
            var shop = Shop(key);
            DespawnTrader(key);

            var ent = GameManager.server.CreateEntity(profile.Prefab, pos, Quaternion.Euler(0f, yaw, 0f));
            if (ent == null)
            {
                PrintError($"CreateEntity returned null for '{profile.Prefab}' — check the profile's Prefab in traders.json.");
                return;
            }

            var npc = ent as NPCTalking;
            if (npc == null)
            {
                // Probe finding worth logging loudly: the prefab exists but isn't NPCTalking.
                PrintError($"'{profile.Prefab}' is {ent.GetType().Name}, not NPCTalking — conversation intercept won't fire. Pick another prefab.");
                UnityEngine.Object.Destroy(ent.gameObject);
                return;
            }

            // Clients derive face/gender deterministically from userID — set it
            // BEFORE Spawn() so the appearance seed networks with the create.
            // Steam-range values only: userID < 10,000,000 would flip IsBot.
            if (profile.FaceSeed != 0) npc.userID = profile.FaceSeed;

            // Respawn-on-load model: we own the lifecycle, the engine save file doesn't.
            ent.EnableSaving(false);
            ent.Spawn();

            npc.displayName = profile.Name;
            DressTrader(npc, profile);
            shop.Trader = npc;

            Subscribe(nameof(OnNpcConversationStart));
            Subscribe(nameof(OnPlayerInput));
            RecomputeProtectionHook();
            // Always runs: it also self-heals a destroyed trader, so it can't be
            // gated on the (documented as disable-able) auto-close range.
            if (_rangeTimer == null) _rangeTimer = timer.Every(2f, DialogRangeSweep);

            DLog($"Trader '{profile.Name}' spawned at {pos} ({npc.ShortPrefabName}, face {profile.FaceSeed}).");
        }

        private void DespawnTrader(string key)
        {
            var shop = Shop(key);
            CloseDialogsFor(key); // otherwise they're stuck with a cursor panel
            if (shop.Trader != null && !shop.Trader.IsDestroyed) shop.Trader.Kill();
            shop.Trader = null;
            RecomputeTraderHooks();
        }

        private void DespawnAllTraders()
        {
            foreach (var kv in _state.Shops)
            {
                CloseDialogsFor(kv.Key);
                if (kv.Value.Trader != null && !kv.Value.Trader.IsDestroyed) kv.Value.Trader.Kill();
                kv.Value.Trader = null;
            }
            RecomputeTraderHooks();
        }

        private bool AnyTraderAlive()
        {
            foreach (var kv in _state.Shops)
                if (kv.Value.Trader != null && !kv.Value.Trader.IsDestroyed) return true;
            return false;
        }

        private void RecomputeTraderHooks()
        {
            if (AnyTraderAlive())
            {
                Subscribe(nameof(OnNpcConversationStart));
                Subscribe(nameof(OnPlayerInput));
                if (_rangeTimer == null) _rangeTimer = timer.Every(2f, DialogRangeSweep);
            }
            else
            {
                Unsubscribe(nameof(OnNpcConversationStart));
                Unsubscribe(nameof(OnPlayerInput));
                _rangeTimer?.Destroy();
                _rangeTimer = null;
            }
            RecomputeProtectionHook();
        }

        private void CloseAllDialogs()
        {
            foreach (var id in new List<ulong>(_chatSessions.Keys)) EndChatSession(id);
            if (_dialogOpen.Count == 0) return;
            foreach (var p in BasePlayer.activePlayerList)
                if (_dialogOpen.Contains((ulong)p.userID))
                {
                    CuiHelper.DestroyUi(p, UiPanel);
                    CuiHelper.DestroyUi(p, ChatPanel);
                }
            _dialogOpen.Clear();
            _dialogTrader.Clear();
        }

        private void CloseDialogsFor(string traderKey)
        {
            // Her chat sessions end with her — both delivery modes.
            List<ulong> ended = null;
            foreach (var kv in _chatSessions)
                if (kv.Value.TraderKey == traderKey) (ended ?? (ended = new List<ulong>())).Add(kv.Key);
            if (ended != null) foreach (var id2 in ended) EndChatSession(id2);
            if (_dialogOpen.Count == 0) return;
            foreach (var p in BasePlayer.activePlayerList)
            {
                var id = (ulong)p.userID;
                string k;
                if (!_dialogTrader.TryGetValue(id, out k) || k != traderKey) continue;
                CuiHelper.DestroyUi(p, UiPanel);
                CuiHelper.DestroyUi(p, ChatPanel);
                _dialogOpen.Remove(id);
                _dialogTrader.Remove(id);
            }
        }

        // Rust destroys a player's CUI on death; clear our flag to match, or the
        // stale entry blocks every future E press.
        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null) return;
            _dialogOpen.Remove((ulong)player.userID);
            EndChatSession((ulong)player.userID); // death nuked the CUI, chat panel included
            TryGiveWelcomeNote(player); // a fresh spawn may never fire OnPlayerSleepEnded
        }

        private void DressTrader(NPCTalking npc, TraderProfile profile)
        {
            var wear = npc.inventory?.containerWear;
            if (wear == null)
            {
                PrintWarning("Trader has no wear container — outfit skipped (probe finding).");
                return;
            }

            for (var i = wear.itemList.Count - 1; i >= 0; i--)
            {
                var worn = wear.itemList[i];
                worn.RemoveFromContainer();
                worn.Remove();
            }

            foreach (var entry in profile.Kit)
            {
                var shortname = entry;
                ulong skin = 0;
                var at = entry.IndexOf('@');
                if (at > 0)
                {
                    shortname = entry.Substring(0, at);
                    ulong.TryParse(entry.Substring(at + 1), out skin);
                }
                var item = ItemManager.CreateByName(shortname, 1, skin);
                if (item == null) { PrintWarning($"TraderKit item '{shortname}' unknown — skipped."); continue; }
                if (!item.MoveToContainer(wear)) item.Remove();
            }
            npc.SendNetworkUpdate();
        }

        // Protection — subscribed only while something protectable exists.
        // Covers the trader (god mode) and every OwnerStamp-ed entity: the
        // pasted shop building, note boxes, and stashes (ProtectWorldObjects).
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            // Hottest hook in the game — order the tests cheapest-first and keep
            // Unity's == operator overload out of the fast path.
            if ((object)entity == null) return null;
            if (_config.ProtectWorldObjects && IsOurStamp(entity.OwnerID)) return true;
            if (_config.TraderGodMode && IsTraderEntity(entity)) return true;
            // The collections crew shoots to frighten (I.6): their hits on
            // players are scaled down — survivable is doctrine, not luck.
            if (_crewActive.Count > 0 && _crewDamageScale < 1f && info != null &&
                entity is BasePlayer && info.Initiator is CrewNpc)
                info.damageTypes.ScaleAll(_crewDamageScale);
            return null;
        }

        private void RecomputeProtectionHook()
        {
            var anyShop = false;
            foreach (var kv in _state.Shops) if (kv.Value.ShopPlaced) { anyShop = true; break; }
            var needed = (AnyTraderAlive() && _config.TraderGodMode)
                         || (_config.ProtectWorldObjects && (anyShop || _state.Notes.Count > 0 || _state.Stashes.Count > 0 || _state.Drops.Count > 0))
                         || _crewActive.Count > 0; // the visit's damage scale rides the same hook
            if (needed) Subscribe(nameof(OnEntityTakeDamage));
            else Unsubscribe(nameof(OnEntityTakeDamage));

            // Same trigger, different protection: the no-build zone only means
            // anything once a shop is actually standing. Both placement paths
            // need gating — see CanDeployItem for why CanBuild alone isn't enough.
            var shopRing = anyShop && _config.ShopNoBuildRadius > 0f;
            if (shopRing) Subscribe(nameof(CanBuild));
            else Unsubscribe(nameof(CanBuild));
            // CanDeployItem doubles as the workbench quest gate (v2.18), so it
            // must stay armed while any gate is configured even with no shop up.
            if (shopRing || AnyWorkbenchGate()) Subscribe(nameof(CanDeployItem));
            else Unsubscribe(nameof(CanDeployItem));
        }

        // --- shop no-build zone ---------------------------------------------
        // Without this a player can wall a shop in and deny the whole quest
        // line to everyone — and the map marker advertises exactly where to do
        // it. A cylinder, not a sphere: the airspace above the shop is denied
        // too, so nobody roofs it over from a tower.

        private bool NearShop(Vector3 pos, float extra, out string traderKey)
        {
            traderKey = null;
            var radius = _config.ShopNoBuildRadius + extra;
            if (radius <= 0f) return false;
            var sqr = radius * radius;
            foreach (var kv in _state.Shops)
            {
                if (!kv.Value.ShopPlaced) continue;
                var dx = kv.Value.ShopX - pos.x;
                var dz = kv.Value.ShopZ - pos.z;
                if (dx * dx + dz * dz > sqr) continue;
                traderKey = kv.Key;
                return true;
            }
            return false;
        }

        // Cancelling is silent in the client, so say why ourselves. Admins are
        // exempt — decorating a shop by hand is a normal thing to want.
        private object CanBuild(Planner planner, Construction prefab, Construction.Target target)
        {
            if (planner == null || _config.ShopNoBuildRadius <= 0f) return null;
            var player = planner.GetOwnerPlayer();
            if (player == null) return null;
            if (permission.UserHasPermission(player.UserIDString, _config.AdminPermission)) return null;

            var pos = target.entity != null && target.socket != null ? target.GetWorldPosition() : target.position;
            string key;
            if (!NearShop(pos, 0f, out key)) return null;
            PrintToChat(player, L("Build.Blocked", player, TraderName(key), _config.ShopNoBuildRadius.ToString("0")));
            return false;
        }

        // CanBuild only covers Planner — building blocks. Deployables go through
        // Deployer, which never calls it (live assembly 2026-07-26: Deployer.DoDeploy
        // gates on CanDeployItem alone). That left the zone stopping foundations
        // while missing the attack somebody would actually use: a ring of high
        // external walls, which are deployables. Same for a cupboard planted at
        // the door to claim the ground.
        //
        // The hook hands us the player, not the target point (the placement ray
        // lives in the RPC message we never see), so we test where he is standing
        // plus Deployer's own 8 m placement reach. Conservative by design: it can
        // deny a placement just outside the ring, never allow one inside it.
        //
        // The engine passes a third argument (the slot-target NetworkableId) which
        // we deliberately don't declare — Oxide binds the leading parameters a hook
        // method asks for, and naming that type would drag in a Network reference
        // we otherwise don't need.
        private const float DeployReachMetres = 8f;

        private object CanDeployItem(BasePlayer player, Deployer deployer)
        {
            if (player == null) return null;

            // Workbench quest gate first — a gameplay rule like the tech tree
            // lock, so deliberately NO admin exemption (the exemption below is
            // about decorating shops, not progression).
            var def = deployer != null ? deployer.GetOwnerItemDefinition() : null;
            string gateTrader;
            if (def != null && WorkbenchGateBlocks(player, def.shortname, out gateTrader))
            {
                PrintToChat(player, L("Workbench.Gated", player, TraderName(gateTrader)));
                return false;
            }

            if (_config.ShopNoBuildRadius <= 0f) return null;
            if (permission.UserHasPermission(player.UserIDString, _config.AdminPermission)) return null;
            string key;
            if (!NearShop(player.transform.position, DeployReachMetres, out key)) return null;
            PrintToChat(player, L("Deploy.Blocked", player, TraderName(key)));
            return false;
        }

        // --- workbench quest gates -------------------------------------------
        // A bench must be EARNED: craft and placement of the four bench items
        // are refused until the player finishes the configured trader's chain
        // (WarmthTier >= 2 — the same "her list is clear" bar the traders use).
        // Per-player and per-wipe by construction, since progress wipes with
        // everything else. What this cannot gate: the static benches at
        // monuments, and standing at a bench somebody unlocked placed — that
        // is Rust's own proximity mechanic.

        private string WorkbenchGateTrader(string shortname)
        {
            switch (shortname)
            {
                case "workbench1": return string.IsNullOrWhiteSpace(_config.WorkbenchGateWb1) ? null : _config.WorkbenchGateWb1;
                case "workbench2": return string.IsNullOrWhiteSpace(_config.WorkbenchGateWb2) ? null : _config.WorkbenchGateWb2;
                case "workbench3": return string.IsNullOrWhiteSpace(_config.WorkbenchGateWb3) ? null : _config.WorkbenchGateWb3;
                case "iotable":    return string.IsNullOrWhiteSpace(_config.WorkbenchGateEng) ? null : _config.WorkbenchGateEng;
                default: return null;
            }
        }

        private bool AnyWorkbenchGate()
        {
            return !string.IsNullOrWhiteSpace(_config.WorkbenchGateWb1)
                || !string.IsNullOrWhiteSpace(_config.WorkbenchGateWb2)
                || !string.IsNullOrWhiteSpace(_config.WorkbenchGateWb3)
                || !string.IsNullOrWhiteSpace(_config.WorkbenchGateEng);
        }

        private bool WorkbenchGateBlocks(BasePlayer player, string shortname, out string traderKey)
        {
            traderKey = WorkbenchGateTrader(shortname);
            if (traderKey == null) return false;
            // create:false — a gate check must never mint a progress file;
            // WarmthTier(null, ...) is 0, which reads as "chain not done".
            var prog = GetProgress(player, create: false);
            return WarmthTier(prog, traderKey) < 2;
        }

        // CanCraft fires once per craft click, before ingredients are checked
        // or taken (live assembly 2026-08-16) — false refuses the craft whole.
        private object CanCraft(ItemCrafter crafter, ItemBlueprint bp, int amount, bool free)
        {
            var player = crafter != null ? crafter.owner : null;
            if (player == null || bp == null || bp.targetItem == null) return null;
            string gateTrader;
            if (!WorkbenchGateBlocks(player, bp.targetItem.shortname, out gateTrader)) return null;
            PrintToChat(player, L("Workbench.Gated", player, TraderName(gateTrader)));
            return false;
        }

        // --- tech tree lock --------------------------------------------------
        // Optional hard mode: a locked bench's tech tree refuses the scrap
        // unlock path entirely, so its blueprints only come from a research
        // table or experiments. The hook fires inside RPC_TechTreeUnlock
        // BEFORE any scrap is taken, and a non-null return blocks both the
        // normal path and the upgrade bypass/prototype path (live assembly
        // 2026-08-16). isIOBench marks the engineering workbench, which also
        // reports a Workbenchlevel — test it first.
        //
        // Per-player since 2.19.0: when the bench also carries a WorkbenchGate
        // trader, that player's lock lifts once HER chain is done — the same
        // bar that let them build the bench. A locked bench with NO gate
        // trader stays locked for everyone (the 2.17.0 absolute behavior).
        private object OnTechTreeNodeUnlock(Workbench bench, TechTreeData.NodeInstance node, BasePlayer player)
        {
            if (bench == null || player == null) return null;
            bool locked; string shortname;
            if (bench.isIOBench) { locked = _config.TechTreeLockEng; shortname = "iotable"; }
            else if (bench.Workbenchlevel == 1) { locked = _config.TechTreeLockWb1; shortname = "workbench1"; }
            else if (bench.Workbenchlevel == 2) { locked = _config.TechTreeLockWb2; shortname = "workbench2"; }
            else if (bench.Workbenchlevel == 3) { locked = _config.TechTreeLockWb3; shortname = "workbench3"; }
            else return null;
            if (!locked) return null;
            string gateTrader;
            if (!WorkbenchGateBlocks(player, shortname, out gateTrader))
            {
                if (gateTrader != null) return null; // her chain is done — the tree is theirs
                PrintToChat(player, L("TechTree.Locked", player));
                return true;
            }
            PrintToChat(player, L("TechTree.LockedUntil", player, TraderName(gateTrader)));
            return true;
        }

        // --- public API ------------------------------------------------------
        // For other plugins that place things in the world (FakeFriends asks
        // before it anchors a bot's home): true when pos falls inside a shop's
        // no-build cylinder, grown by `extra` metres for the caller's own
        // footprint. Safe to call before any shop is placed — it just says no.

        [HookMethod("IsNearQuestShop")]
        public bool IsNearQuestShop(Vector3 pos, float extra)
        {
            string key;
            return NearShop(pos, extra, out key);
        }

        [HookMethod("GetQuestShopNoBuildRadius")]
        public float GetQuestShopNoBuildRadius() => _config.ShopNoBuildRadius;

        [HookMethod("GetQuestShopPositions")]
        public List<Vector3> GetQuestShopPositions()
        {
            var list = new List<Vector3>();
            foreach (var kv in _state.Shops)
                if (kv.Value.ShopPlaced) list.Add(kv.Value.ShopPos);
            return list;
        }

        private bool IsTraderEntity(BaseEntity e)
        {
            foreach (var kv in _state.Shops) if (ReferenceEquals(kv.Value.Trader, e)) return true;
            return false;
        }

        #endregion

        #region Shop (prefab spawn / CopyPaste paste)

        private List<BaseEntity> _pasteBuf;
        private string _pasteKey; // trader whose shop is mid-paste
        private const string ShopMarkerPrefab = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";

        // Mode dispatch — all placement paths (manual, autoplace, wipe) go
        // through here.
        private object PlaceShop(string key, Vector3 origin, float yawDeg)
        {
            var p = Profile(key);
            if (p == null) return $"no trader profile '{key}'";
            var shop = Shop(key);

            // Clear the old one first. Paste-mode builds are engine-persisted, so
            // re-placing without this left the previous shop standing forever with
            // no state pointing at it (nothing could ever remove it).
            if (shop.ShopPlaced)
            {
                RemoveShopEntities(key);
                if (shop.ShopEnt != null && !shop.ShopEnt.IsDestroyed) shop.ShopEnt.Kill();
                shop.ShopEnt = null;
                KillShopMarker(key);
                shop.ShopPlaced = false;
            }
            return p.ShopMode == "paste" ? PasteShop(key, origin, yawDeg) : SpawnShopPrefab(key, origin, yawDeg);
        }

        private object SpawnShopPrefab(string key, Vector3 origin, float yawDeg)
        {
            var p = Profile(key);
            if (p == null) return $"no trader profile '{key}'";
            var shop = Shop(key);
            ClearFootprintTrees(origin, yawDeg);
            origin.y += _config.PasteHeightOffset; // rigid trim (prefab pivot vs ground)
            var ent = GameManager.server.CreateEntity(p.ShopPrefab, origin, Quaternion.Euler(0f, yawDeg, 0f));
            if (ent == null) return $"'{p.ShopPrefab}' is not spawnable (no entity component)";

            if (shop.ShopEnt != null && !shop.ShopEnt.IsDestroyed) shop.ShopEnt.Kill();
            ent.EnableSaving(false); // respawn-on-load model, same as the trader
            ent.OwnerID = OwnerStamp;
            ent.Spawn();
            shop.ShopEnt = ent;

            shop.ShopPlaced = true;
            shop.ShopX = origin.x; shop.ShopY = origin.y; shop.ShopZ = origin.z;
            shop.ShopYawDeg = yawDeg;
            shop.ShopHeightApplied = 0f; // offsets are entity-local in prefab mode

            // Child entities spawn slightly after the shell, and the electrical
            // sim can recompute power afterwards — so re-apply a few times.
            if (_config.ShopLightsOn)
            {
                timer.Once(1f, () => SwitchOnShopLights(key, false));
                timer.Once(5f, () => SwitchOnShopLights(key, false));
                timer.Once(20f, () => SwitchOnShopLights(key, false));
            }
            UpdateShopMarker(key);

            var tpos = origin + Quaternion.Euler(0f, yawDeg, 0f) * new Vector3(p.TraderOffsetX, 0f, p.TraderOffsetZ);
            tpos.y = origin.y + p.TraderOffsetY;
            SpawnTrader(key, tpos, yawDeg + p.TraderYawOffset);
            shop.TraderPlaced = true;
            shop.TraderX = tpos.x; shop.TraderY = tpos.y; shop.TraderZ = tpos.z;
            shop.TraderYaw = yawDeg + p.TraderYawOffset;

            SaveState();
            RecomputeProtectionHook();
            DLog($"Shop prefab '{ent.ShortPrefabName}' for '{p.Name}' placed at {origin} (yaw {yawDeg:0}).");
            return null;
        }

        // Returns null on success (async paste in flight), else an error string.
        private object PasteShop(string key, Vector3 origin, float yawDeg)
        {
            if (CopyPaste == null) return "CopyPaste plugin not loaded";
            if (_pasteBuf != null) return "a paste is already in progress";
            var file = Profile(key).ShopPasteFile;
            _pasteKey = key;
            if (!Interface.Oxide.DataFileSystem.ExistsDatafile($"copypaste/{file}"))
                return $"paste file '{file}' not found — capture the shop build in-game with /copy {file}";

            ClearFootprintTrees(origin, yawDeg);
            _pasteBuf = new List<BaseEntity>();
            // CopyPaste's autoheight is AIM-POINT-relative (capture-aim dependent),
            // which buries foundations — ported fix from FakeFriends decision 0003:
            // compute the vertical fit from the blueprint's foundation footprint and
            // paste rigidly with autoheight OFF. Autoheight remains the fallback for
            // blueprints without foundations.
            string[] args;
            var fit = ComputeFoundationFitHeight(file, origin, yawDeg);
            var applied = 0f; // vertical lift the paste applies (blueprint-space anchor for trader offsets)
            if (float.IsNaN(fit))
            {
                DLog($"[shop] {file}: no foundations in blueprint — CopyPaste autoheight fallback (trader offsets will be less stable).");
                args = new[]
                {
                    "deployables", "true", "inventories", "true",
                    "autoheight", "true", "height", _config.PasteHeightOffset.ToString("0.###"),
                    "stability", "true", "auth", "false", "entityowner", "false"
                };
            }
            else
            {
                applied = fit + _config.PasteHeightOffset;
                DLog($"[shop] {file}: footprint-fit height {applied:0.00} (fit {fit:0.00} + trim {_config.PasteHeightOffset:0.00}).");
                args = new[]
                {
                    "deployables", "true", "inventories", "true",
                    "autoheight", "false", "height", applied.ToString("0.###"),
                    "stability", "true", "auth", "false", "entityowner", "false"
                };
            }
            var result = CopyPaste.Call("TryPasteFromVector3", origin, yawDeg * Mathf.Deg2Rad, file, args,
                new Action(() => FinishShopPaste(key, origin, yawDeg, applied)),
                new Action<BaseEntity>(ent =>
                {
                    if (ent == null || _pasteBuf == null) return;
                    ent.OwnerID = OwnerStamp;
                    _pasteBuf.Add(ent);
                }));
            if (result is string)
            {
                _pasteBuf = null;
                return $"paste failed: {result}";
            }
            return null;
        }

        private void FinishShopPaste(string key, Vector3 origin, float yawDeg, float appliedHeight)
        {
            var ents = _pasteBuf;
            _pasteBuf = null;
            _pasteKey = null;
            if (ents == null) return; // plugin unloaded mid-paste
            var p = Profile(key);
            if (p == null) return;
            var shop = Shop(key);

            shop.ShopPlaced = true;
            shop.ShopX = origin.x; shop.ShopY = origin.y; shop.ShopZ = origin.z;
            shop.ShopYawDeg = yawDeg;
            shop.ShopHeightApplied = appliedHeight;

            // Trader stands at the profile offset — XZ rotates with the paste,
            // Y is blueprint-space (origin + applied lift + offset).
            var tpos = origin + Quaternion.Euler(0f, yawDeg, 0f) * new Vector3(p.TraderOffsetX, 0f, p.TraderOffsetZ);
            tpos.y = origin.y + appliedHeight + p.TraderOffsetY;
            SpawnTrader(key, tpos, yawDeg + p.TraderYawOffset);
            shop.TraderPlaced = true;
            shop.TraderX = tpos.x; shop.TraderY = tpos.y; shop.TraderZ = tpos.z;
            shop.TraderYaw = yawDeg + p.TraderYawOffset;

            UpdateShopMarker(key);
            SaveState();
            RecomputeProtectionHook();
            DLog($"Shop for '{p.Name}' pasted at {origin} (yaw {yawDeg:0}, {ents.Count} entities).");
        }

        // Boot-time shop recovery for every trader. Prefab mode: the shell was
        // EnableSaving(false), so respawn shell + trader from state. Paste mode:
        // the build persists in the save file, only the marker needs restoring.
        private void RebindShops()
        {
            foreach (var key in new List<string>(_state.Shops.Keys))
            {
                var shop = Shop(key);
                if (!shop.ShopPlaced) continue;
                var p = Profile(key);
                if (p == null) { PrintWarning($"State has a shop for unknown trader '{key}' — ignoring."); continue; }
                if (p.ShopMode == "paste") { UpdateShopMarker(key); continue; }
                var origin = shop.ShopPos;
                origin.y -= _config.PasteHeightOffset; // SpawnShopPrefab re-adds the trim
                var err = SpawnShopPrefab(key, origin, shop.ShopYawDeg);
                if (err != null) PrintWarning($"Shop respawn failed for '{key}': {err}");
            }
        }

        // --- lights ---------------------------------------------------------

        // The caboose ships its lamps, generator and switches as child entities,
        // so "lights on" means walking the tree and flagging them On. Names vary
        // by prefab, hence the keyword match plus a one-time log of everything
        // found (tune the keywords from that log if a lamp stays dark).
        private static readonly string[] LightKeywords = { "light", "lamp", "generator", "switch", "bulb", "sign", "neon" };

        // The caboose has a real light switch inside the door (observed live), and
        // a player flip goes through ElectricSwitch.SetSwitch — which both sets
        // the flag AND MarkDirty()s the IO graph, so the caboose's own wiring
        // re-propagates power to every lamp downstream.
        //
        // AND the lighting is a genuine TWO-WAY circuit (entity dump, all four
        // cabooses, 2026-08-11): two caboose_lightswitch entities feed one
        // caboose_xorswitch whose output drives the lamps — lit means ODD
        // parity, exactly one switch on, like any stairwell. The old rule here
        // ("every switch found off gets flipped ON") made both XOR inputs hot
        // and the output cold: the healer itself was switching the lights OFF,
        // within a minute of any player flipping them on. Lit is a property of
        // the switch PAIR, so the heal toggles ONE switch only when parity is
        // even, and otherwise keeps its hands off the pair entirely.
        private int SwitchOnShopLights(string key, bool verbose)
        {
            var shop = Shop(key);
            var shopEnt = shop.ShopEnt;
            if (shopEnt == null || shopEnt.IsDestroyed) return 0;
            var seen = verbose ? new StringBuilder() : null;

            var ents = new List<BaseEntity>();
            CollectChildren(shopEnt, ents, 0);
            // The lamps (and possibly the door switch) may not be parented to
            // the shell — sweep nearby too.
            var nearby = new List<BaseEntity>();
            Vis.Entities(shop.ShopPos, 14f, nearby, ShopSweepLayers);
            for (var i = 0; i < nearby.Count; i++)
                if (nearby[i] != null && !nearby[i].IsDestroyed && !ents.Contains(nearby[i])) ents.Add(nearby[i]);

            var flipped = 0;
            var fed = 0;
            // The two-way pair: with an XOR in the circuit, switches are
            // handled by parity below, never flipped individually.
            ElectricSwitch pairA = null, pairB = null;
            var hasXor = false;
            for (var i = 0; i < ents.Count; i++)
            {
                if (ents[i] is XORSwitch) { hasXor = true; continue; }
                var ps = ents[i] as ElectricSwitch;
                if (ps == null || (ents[i].ShortPrefabName ?? "").IndexOf("lightswitch", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (pairA == null) pairA = ps;
                else if (pairB == null) pairB = ps;
            }
            var xorPair = hasXor && pairA != null;
            if (xorPair)
            {
                var onCount = (pairA.HasFlag(BaseEntity.Flags.On) ? 1 : 0) +
                              (pairB != null && pairB.HasFlag(BaseEntity.Flags.On) ? 1 : 0);
                if (onCount % 2 == 0)
                {
                    try { pairA.SetSwitch(!pairA.HasFlag(BaseEntity.Flags.On)); flipped++; }
                    catch (Exception e) { PrintWarning($"Toggling '{pairA.ShortPrefabName}' failed: {e.Message}"); }
                }
            }
            for (var i = 0; i < ents.Count; i++)
            {
                var ent = ents[i];
                var name = ent.ShortPrefabName ?? "";
                var io = ent as IOEntity;
                seen?.AppendLine(io != null
                    ? $"  {name} ({io.GetType().Name}) IO in:{(io.inputs != null ? io.inputs.Length : 0)} powered:{io.IsPowered()} on:{ent.HasFlag(BaseEntity.Flags.On)}"
                    : $"  {name} ({ent.GetType().Name}) on:{ent.HasFlag(BaseEntity.Flags.On)}");
                try
                {
                    var sw = ent as ElectricSwitch;
                    if (sw != null)
                    {
                        // Legacy path for non-XOR shops only (a pasted build
                        // with plain switches): there, on = lit.
                        if (!xorPair && !sw.HasFlag(BaseEntity.Flags.On)) { sw.SetSwitch(true); flipped++; }
                        continue;
                    }
                    if (io == null)
                    {
                        // Not electrical — plain flag lights (signs, braziers).
                        for (var k = 0; k < LightKeywords.Length; k++)
                        {
                            if (name.IndexOf(LightKeywords[k], StringComparison.OrdinalIgnoreCase) < 0) continue;
                            if (!ent.HasFlag(BaseEntity.Flags.On))
                            {
                                SetFlagNet(ent, BaseEntity.Flags.On, true);
                                ent.SendNetworkUpdate();
                                fed++;
                            }
                            break;
                        }
                    }
                }
                catch (Exception e) { PrintWarning($"Powering '{name}' failed: {e.Message}"); }
            }

            // Direct-feed fallback, only on a pass where no switch flipped (so
            // the wiring has had a chance to do its job first).
            if (flipped == 0)
            {
                for (var i = 0; i < ents.Count; i++)
                {
                    var io = ents[i] as IOEntity;
                    if (io == null || io is ElectricSwitch) continue;
                    try
                    {
                        var inputs = io.inputs != null ? io.inputs.Length : 0;
                        // NEVER touch sources (inputs == 0). The caboose feeds its
                        // switches from a built-in static supply THROUGH AN XOR
                        // whose other input is caboose_generator — flagging the
                        // generator On made both XOR inputs hot, which cut the
                        // whole lighting circuit and the sim then cleared our
                        // switch flips within minutes (live entity dump,
                        // 2026-07-31: the 5-min blink cycle was self-inflicted).
                        if (inputs == 0) continue;
                        if (io.IsPowered()) continue; // the wiring got there — leave the sim alone
                        io.UpdateHasPower(25, 0);
                        io.UpdateFromInput(25, 0);
                        io.SendNetworkUpdate();
                        fed++;
                    }
                    catch (Exception e) { PrintWarning($"Powering '{io.ShortPrefabName}' failed: {e.Message}"); }
                }
            }

            if (verbose && seen != null) DLog($"[{key}] shop entities:\n{seen}");
            var switched = flipped + fed;
            // Routine re-flips are EXPECTED (the sim clears the switches every
            // few minutes) — logging each one buried the log at 1k+ lines/day.
            // rq.shop.lights still reports everything on demand.
            if (verbose && switched > 0) DLog($"[{key}] shop lights: flipped {flipped} switch(es), fed {fed} device(s).");
            return switched;
        }

        private void CollectChildren(BaseEntity ent, List<BaseEntity> outList, int depth)
        {
            if (ent == null || ent.IsDestroyed || depth > 4) return;
            var children = ent.children;
            if (children == null) return;
            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (child == null || child.IsDestroyed) continue;
                outList.Add(child);
                CollectChildren(child, outList, depth + 1);
            }
        }

        [ConsoleCommand("rq.shop.lights")]
        private void CmdShopLights(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var key = ResolveTraderKey(arg, 0);
            if (key == null) { arg.ReplyWith(UnknownTraderReply(arg)); return; }
            var n = SwitchOnShopLights(key, true);
            arg.ReplyWith($"[{key}] switched on {n} entit(ies). Full child list written to the plugin log (oxide/logs/RustQuests) — send it over if a lamp stays dark.");
        }

        // --- map marker -----------------------------------------------------

        // A named shop icon on the map: the vanilla vending marker with no
        // machine attached (it only self-repositions when it has one, so ours
        // stays put at the shop).
        private void UpdateShopMarker(string key)
        {
            KillShopMarker(key);
            var shop = Shop(key);
            var p = Profile(key);
            if (!_config.ShopMapMarker || !shop.ShopPlaced || p == null) return;
            var marker = GameManager.server.CreateEntity(ShopMarkerPrefab, shop.ShopPos) as VendingMachineMapMarker;
            if (marker == null) { PrintWarning("Shop map marker prefab failed to spawn."); return; }
            marker.EnableSaving(false);
            marker.OwnerID = OwnerStamp;
            marker.markerShopName = p.ShopName;
            marker.Spawn();
            marker.SendNetworkUpdate();
            shop.Marker = marker;
            DLog($"Shop map marker placed: \"{marker.markerShopName}\" at {MapHelper.PositionToString(shop.ShopPos)}.");
        }

        private void KillShopMarker(string key)
        {
            var shop = Shop(key);
            if (shop.Marker != null && !shop.Marker.IsDestroyed) shop.Marker.Kill();
            shop.Marker = null;
        }

        // Vertical fit from the blueprint's foundations (FakeFriends decision
        // 0003, verified in-game there). With autoheight OFF a pasted entity's
        // world Y = localOffset.y + origin.y + height, so seating the most-buried
        // foundation on the highest ground under its corners means:
        //   height = max_over_foundations( groundMax_corner - localOffset.y ) - origin.y
        // Unlike FakeFriends we paste rotated, so foundation XZ offsets rotate by
        // yaw before ground sampling. NaN = no foundations / unparseable.
        private float ComputeFoundationFitHeight(string file, Vector3 origin, float yawDeg)
        {
            try
            {
                var data = Interface.Oxide.DataFileSystem.GetDatafile($"copypaste/{file}");
                var entities = data["entities"] as List<object>;
                if (entities == null) return float.NaN;
                var rot = Quaternion.Euler(0f, yawDeg, 0f);
                var need = float.NegativeInfinity;
                var found = false;
                foreach (var raw in entities)
                {
                    var ent = raw as Dictionary<string, object>;
                    if (ent == null) continue;
                    var prefab = ent.ContainsKey("prefabname") ? ent["prefabname"] as string : null;
                    if (prefab == null || prefab.IndexOf("/foundation", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var pos = ent["pos"] as Dictionary<string, object>;
                    if (pos == null) continue;
                    var local = new Vector3(Convert.ToSingle(pos["x"]), Convert.ToSingle(pos["y"]), Convert.ToSingle(pos["z"]));
                    var world = origin + rot * local;
                    var groundMax = float.NegativeInfinity;
                    for (var dx = -1; dx <= 1; dx++)
                        for (var dz = -1; dz <= 1; dz++)
                        {
                            var h = TerrainMeta.HeightMap.GetHeight(new Vector3(world.x + dx * 1.5f, 0f, world.z + dz * 1.5f));
                            if (h > groundMax) groundMax = h;
                        }
                    var hi = groundMax - local.y;
                    if (hi > need) need = hi;
                    found = true;
                }
                if (!found) return float.NaN;
                return need - origin.y;
            }
            catch (Exception e)
            {
                PrintWarning($"ComputeFoundationFitHeight({file}) failed: {e.Message} — using autoheight fallback.");
                return float.NaN;
            }
        }

        // --- random placement (task 3.2) -----------------------------------

        // CLIFF|OCEAN|MONUMENT|ROAD|RIVER|LAKE|BUILDING|CLIFFSIDE — literals from
        // Rust.World's TerrainTopology (verified 2026-07-25, DIAGNOSTICS §7).
        private const int AvoidTopology = 2 | 128 | 1024 | 2048 | 16384 | 65536 | 2097152 | 4194304;

        // Caboose footprint (train car ≈ 16 m × 4 m). Checks run as an oriented
        // box at the candidate yaw, so the same spot can pass one rotation and
        // fail another — the search tries several per position.
        private const float ShopHalfLength = 9f;   // along the car
        private const float ShopHalfWidth = 2.6f;  // across
        private const float ShopClearHeight = 5f;  // headroom above ground

        // A wooden box needs nothing like the caboose's clearance, so it gets its
        // own cheap search instead of running the 16m oriented-box test.
        private bool TryFindStashSpot(out Vector3 spot)
        {
            spot = default(Vector3);
            var half = TerrainMeta.Size.x * 0.5f - 250f;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var pos = new Vector3(UnityEngine.Random.Range(-half, half), 0f, UnityEngine.Random.Range(-half, half));
                pos.y = TerrainMeta.HeightMap.GetHeight(pos);
                if (pos.y < 2f) continue;
                if (TerrainMeta.TopologyMap.GetTopology(pos, AvoidTopology)) continue;
                if (Physics.CheckSphere(pos + Vector3.up * 0.5f, 1.5f, StashBlockLayers, QueryTriggerInteraction.Ignore)) continue;
                spot = pos;
                return true;
            }
            return false;
        }

        private static readonly int StashBlockLayers = LayerMask.GetMask("World", "Tree", "Construction", "Deployed");

        // TerrainBiome constants (Rust.World, verified 2026-07-25).
        private static int BiomeMask(string name)
        {
            switch ((name ?? "any").ToLowerInvariant())
            {
                case "arid": return 1;
                case "temperate": return 2;
                case "tundra": return 4;
                case "arctic": return 8;
                case "jungle": return JungleBiomeMask; // 16
                default: return 0; // any
            }
        }

        private bool TryFindShopSpot(string traderKey, out Vector3 spot, out float yaw)
        {
            spot = default(Vector3);
            yaw = 0f;
            var half = TerrainMeta.Size.x * 0.5f - 250f;
            var p = Profile(traderKey);
            var wantMask = BiomeMask(p?.Biome);
            // "World" carries rocks, cliffs and monument meshes; "Tree" the
            // trunks. Without these a caboose happily spawns inside a rock.
            // Phased search. The biome test is cheap and runs before any physics,
            // so we can afford thousands of in-biome samples before considering
            // giving up on the biome at all:
            //   1. in-biome, nothing at all in the footprint
            //   2. in-biome, only trees in the way (we fell them at placement —
            //      this is what jungle almost always needs)
            //   3. any biome (only when the profile allows it)
            // Trees are why a jungle-preferred shop used to end up in a field.
            var budget = Mathf.Max(600, _config.ShopSearchAttempts);
            var phase1 = (int)(budget * 0.5f);
            var phase2 = (int)(budget * 0.9f);

            for (var attempt = 0; attempt < budget; attempt++)
            {
                var allowTrees = attempt >= phase1;
                var requireBiome = wantMask != 0 && (attempt < phase2 || _config.ShopBiomeStrict);

                var pos = new Vector3(UnityEngine.Random.Range(-half, half), 0f, UnityEngine.Random.Range(-half, half));
                pos.y = TerrainMeta.HeightMap.GetHeight(pos);
                if (pos.y < 2f) continue; // underwater / beach line
                if (requireBiome && TerrainMeta.BiomeMap.GetBiome(pos, wantMask) < 0.4f) continue;
                if (TerrainMeta.TopologyMap.GetTopology(pos, AvoidTopology)) continue;

                var nearMonument = false;
                foreach (var m in TerrainMeta.Path.Monuments)
                    if (m != null && m.IsInBounds(pos)) { nearMonument = true; break; }
                if (nearMonument) continue;

                // Try a few orientations before giving up on the position.
                for (var t = 0; t < 6; t++)
                {
                    var tryYaw = UnityEngine.Random.Range(0f, 360f);
                    bool treesOnly;
                    if (!FootprintClear(pos, tryYaw, out treesOnly)) continue;
                    if (treesOnly && !allowTrees) continue;
                    spot = pos;
                    yaw = tryYaw;
                    var biomeNote = wantMask != 0
                        ? (TerrainMeta.BiomeMap.GetBiome(pos, wantMask) >= 0.4f ? $"in {p?.Biome ?? "target"} biome" : "OUTSIDE preferred biome")
                        : "any biome";
                    DLog($"[{traderKey}] spot found after {attempt + 1} attempt(s), {biomeNote}{(treesOnly ? ", clearing trees" : "")}.");
                    return true;
                }
            }
            // Preferred biome exhausted. Try her fallback biome before giving up
            // (Sonia: jungle → temperate on a map with no jungle).
            if (wantMask != 0 && !string.IsNullOrEmpty(p?.BiomeFallback) && BiomeMask(p.BiomeFallback) != 0)
            {
                DLog($"[{traderKey}] nothing in '{p.Biome}' after {budget} attempts — falling back to '{p.BiomeFallback}'.");
                return TryFindSpotInBiome(traderKey, BiomeMask(p.BiomeFallback), budget, out spot, out yaw);
            }
            DLog($"[{traderKey}] no spot found in {budget} attempts (biome '{p?.Biome}', strict {_config.ShopBiomeStrict}).");
            return false;
        }

        // The fallback pass: same rules, different biome mask.
        private bool TryFindSpotInBiome(string traderKey, int mask, int budget, out Vector3 spot, out float yaw)
        {
            spot = default(Vector3); yaw = 0f;
            var half = TerrainMeta.Size.x * 0.5f - 250f;
            for (var attempt = 0; attempt < budget; attempt++)
            {
                var pos = new Vector3(UnityEngine.Random.Range(-half, half), 0f, UnityEngine.Random.Range(-half, half));
                pos.y = TerrainMeta.HeightMap.GetHeight(pos);
                if (pos.y < 2f) continue;
                if (mask != 0 && TerrainMeta.BiomeMap.GetBiome(pos, mask) < 0.4f) continue;
                if (TerrainMeta.TopologyMap.GetTopology(pos, AvoidTopology)) continue;
                var nearMonument = false;
                foreach (var m in TerrainMeta.Path.Monuments)
                    if (m != null && m.IsInBounds(pos)) { nearMonument = true; break; }
                if (nearMonument) continue;
                for (var t = 0; t < 6; t++)
                {
                    var tryYaw = UnityEngine.Random.Range(0f, 360f);
                    bool treesOnly;
                    if (!FootprintClear(pos, tryYaw, out treesOnly)) continue;
                    spot = pos; yaw = tryYaw;
                    DLog($"[{traderKey}] fallback spot found after {attempt + 1} attempt(s){(treesOnly ? ", clearing trees" : "")}.");
                    return true;
                }
            }
            return false;
        }

        // Fells the trees standing where the shop is about to go. Jungle is dense
        // enough that insisting on tree-free ground pushes the shop out of the
        // biome entirely; trees regrow on the next wipe.
        private int ClearFootprintTrees(Vector3 pos, float yawDeg)
        {
            if (!_config.ShopClearTrees) return 0;
            var hits = Physics.OverlapBox(pos + Vector3.up * (ShopClearHeight * 0.5f),
                new Vector3(ShopHalfWidth + 1f, ShopClearHeight * 0.5f, ShopHalfLength + 1f),
                Quaternion.Euler(0f, yawDeg, 0f), TreeLayers, QueryTriggerInteraction.Ignore);
            var felled = 0;
            for (var i = 0; i < hits.Length; i++)
            {
                var tree = hits[i].GetComponentInParent<TreeEntity>();
                if (tree == null || tree.IsDestroyed) continue;
                tree.Kill();
                felled++;
            }
            if (felled > 0) DLog($"Felled {felled} tree(s) to seat the shop.");
            return felled;
        }

        private static readonly int TreeLayers = LayerMask.GetMask("Tree");

        // Hoisted: GetMask does string lookups, and this runs up to 1,600 times
        // per search.
        private static readonly int BuildBlockLayers = LayerMask.GetMask("Construction", "Deployed", "Prevent Building");
        // Rocks, cliffs, monument meshes and player builds — never negotiable.
        private static readonly int HardBlockLayers = LayerMask.GetMask("World", "Construction", "Deployed", "Prevent Building");

        // treesOnly = the only things in the way are trees, which the caller may
        // choose to fell rather than reject the spot.
        // Rejection reasons, for the diagnose command.
        private enum SpotReject { None, Flatness, Topology, HardBlock, DoorClearance, Neighbours }

        private bool FootprintClear(Vector3 pos, float yawDeg, out bool treesOnly)
        {
            SpotReject why;
            return FootprintClear(pos, yawDeg, out treesOnly, out why);
        }

        private bool FootprintClear(Vector3 pos, float yawDeg, out bool treesOnly, out SpotReject why)
        {
            treesOnly = false;
            why = SpotReject.None;
            var rot = Quaternion.Euler(0f, yawDeg, 0f);
            var clearHeight = Mathf.Max(2f, _config.ShopClearHeightCfg);

            // Flat + clean topology across the car's own footprint, sampled on a
            // 3×5 grid rather than a circle of corners.
            for (var ix = -1; ix <= 1; ix++)
                for (var iz = -2; iz <= 2; iz++)
                {
                    var sample = pos + rot * new Vector3(ix * ShopHalfWidth, 0f, iz * ShopHalfLength * 0.5f);
                    if (Mathf.Abs(TerrainMeta.HeightMap.GetHeight(sample) - pos.y) > _config.ShopFlatness)
                    { why = SpotReject.Flatness; return false; }
                    if (TerrainMeta.TopologyMap.GetTopology(sample, AvoidTopology))
                    { why = SpotReject.Topology; return false; }
                }

            // Nothing solid may occupy the volume the car will fill — rocks,
            // cliffs, player builds. Trees are reported separately so the caller
            // can decide to fell them.
            var centre = pos + Vector3.up * (clearHeight * 0.5f);
            var extents = new Vector3(ShopHalfWidth + 0.5f, clearHeight * 0.5f, ShopHalfLength + 0.5f);
            if (Physics.CheckBox(centre, extents, rot, HardBlockLayers, QueryTriggerInteraction.Ignore))
            { why = SpotReject.HardBlock; return false; }
            treesOnly = Physics.CheckBox(centre, extents, rot, TreeLayers, QueryTriggerInteraction.Ignore);

            // And keep a little breathing room around the doors.
            if (_config.ShopDoorClearance > 0f &&
                Physics.CheckSphere(pos + Vector3.up * 1f, ShopHalfWidth + _config.ShopDoorClearance, BuildBlockLayers, QueryTriggerInteraction.Ignore))
            { why = SpotReject.DoorClearance; return false; }

            // Never land inside somebody's back yard: the no-build zone we bring
            // with us would freeze whatever is already standing there, and at
            // wipe the bot bases go down before the shops do.
            if (_config.ShopNoBuildRadius > 0f &&
                Physics.CheckSphere(pos + Vector3.up * 1f, _config.ShopNoBuildRadius, BuildBlockLayers, QueryTriggerInteraction.Ignore))
            { why = SpotReject.Neighbours; return false; }
            return true;
        }

        // Places a shop for every trader that lacks one. With strict biomes a
        // pass can legitimately find nothing (a map with almost no jungle), so
        // it reschedules itself instead of settling for the wrong biome.
        private void AutoPlaceMissingShops()
        {
            var stillMissing = false;
            foreach (var key in new List<string>(_traders.Keys))
            {
                var p = Profile(key);
                if (Shop(key).ShopPlaced) continue;
                if (p.ShopMode == "paste" &&
                    (CopyPaste == null || !Interface.Oxide.DataFileSystem.ExistsDatafile($"copypaste/{p.ShopPasteFile}")))
                { DLog($"Auto-place skipped for '{key}': paste file missing."); continue; }

                Vector3 spot; float spotYaw;
                if (!TryFindShopSpot(key, out spot, out spotYaw))
                {
                    DLog($"Auto-place: no spot for '{key}' in its biome — retrying in {_config.ShopPlaceRetryMinutes:0} min.");
                    stillMissing = true;
                    continue;
                }
                var err = PlaceShop(key, spot, spotYaw);
                if (err != null) { DLog($"Auto-place failed for '{key}': {err}"); stillMissing = true; continue; }
                DLog($"Auto-placed {p.Name}'s shop at {MapHelper.PositionToString(spot)}.");
            }
            if (stillMissing && _config.ShopPlaceRetryMinutes > 0f)
                timer.Once(_config.ShopPlaceRetryMinutes * 60f, AutoPlaceMissingShops);
        }

        // Which trader a command applies to: an explicit argument, else the
        // trader you're standing at, else the only one configured.
        private string ResolveTraderKey(ConsoleSystem.Arg arg, int argIndex)
        {
            if (arg.HasArgs(argIndex + 1))
            {
                var named = arg.GetString(argIndex);
                if (_traders.ContainsKey(named)) return named;
                return null;
            }
            var player = arg.Player();
            if (player != null)
            {
                var near = TraderKeyNear(player.transform.position, 30f);
                if (near != null) return near;
            }
            return _traders.Count == 1 ? new List<string>(_traders.Keys)[0] : null;
        }

        private string UnknownTraderReply(ConsoleSystem.Arg arg) =>
            $"Name the trader: {string.Join(", ", new List<string>(_traders.Keys).ToArray())} (or stand at one).";

        [ConsoleCommand("rq.shop.paste")]
        private void CmdShopPaste(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var player = arg.Player();
            if (player == null) { arg.ReplyWith("In-game only (shop lands at your feet, facing your way)."); return; }
            var key = arg.HasArgs() ? arg.GetString(0) : DefaultTraderKey;
            if (!_traders.ContainsKey(key)) { arg.ReplyWith(UnknownTraderReply(arg)); return; }
            var err = PlaceShop(key, player.transform.position, player.eyes.rotation.eulerAngles.y);
            arg.ReplyWith(err as string ?? $"Placing {TraderName(key)}'s shop...");
        }

        [ConsoleCommand("rq.shop.autoplace")]
        private void CmdShopAutoplace(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            // No argument = place every configured trader's shop.
            var keys = arg.HasArgs() ? new List<string> { arg.GetString(0) } : new List<string>(_traders.Keys);
            var sb = new StringBuilder();
            foreach (var key in keys)
            {
                if (!_traders.ContainsKey(key)) { sb.AppendLine($"{key}: unknown trader"); continue; }
                Vector3 spot; float spotYaw;
                if (!TryFindShopSpot(key, out spot, out spotYaw))
                { sb.AppendLine($"{key}: no clear spot found (400 attempts) — try again or place manually"); continue; }
                var err = PlaceShop(key, spot, spotYaw);
                sb.AppendLine(err as string ?? $"{TraderName(key)}: placed at {MapHelper.PositionToString(spot)}");
            }
            arg.ReplyWith(sb.ToString());
        }

        // Sweeps our stamped entities around the shop. Note boxes and stashes
        // carry the same stamp, so they're explicitly spared — killing them here
        // used to leave dead references in _state and break the finale.
        private int RemoveShopEntities(string key)
        {
            var list = new List<BaseEntity>();
            Vis.Entities(Shop(key).ShopPos, 40f, list, ShopSweepLayers);
            var killed = 0;
            for (var i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == null || e.IsDestroyed || !IsOurStamp(e.OwnerID)) continue;
                if (IsWorldObjectEntity(e)) continue;
                e.Kill();
                killed++;
            }
            return killed;
        }

        private static readonly int ShopSweepLayers = LayerMask.GetMask("Construction", "Deployed", "Default", "World");

        private bool IsWorldObjectEntity(BaseEntity e)
        {
            for (var i = 0; i < _state.Notes.Count; i++)
                if (ReferenceEquals(_state.Notes[i].Box, e)) return true;
            for (var i = 0; i < _state.Stashes.Count; i++)
                if (ReferenceEquals(_state.Stashes[i].Box, e) || ReferenceEquals(_state.Stashes[i].Lock, e)) return true;
            return false;
        }

        // Why did/didn't she land in her biome? Samples the map and reports
        // coverage plus how many sampled spots would actually take a caboose.
        [ConsoleCommand("rq.shop.biomescan")]
        private void CmdBiomeScan(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var half = TerrainMeta.Size.x * 0.5f - 250f;
            var names = new[] { "arid", "temperate", "tundra", "arctic", "jungle" };
            var counts = new int[names.Length];
            var land = 0;
            const int grid = 60;
            for (var ix = 0; ix < grid; ix++)
                for (var iz = 0; iz < grid; iz++)
                {
                    var pos = new Vector3(-half + 2f * half * ix / (grid - 1), 0f, -half + 2f * half * iz / (grid - 1));
                    pos.y = TerrainMeta.HeightMap.GetHeight(pos);
                    if (pos.y < 2f) continue;
                    land++;
                    for (var b = 0; b < names.Length; b++)
                        if (TerrainMeta.BiomeMap.GetBiome(pos, BiomeMask(names[b])) >= 0.4f) counts[b]++;
                }

            var sb = new StringBuilder($"Biome coverage ({land} land samples of {grid * grid}):\n");
            for (var b = 0; b < names.Length; b++)
                sb.Append($"  {names[b],-10} {(land > 0 ? counts[b] * 100f / land : 0f),5:0.0}%  ({counts[b]} samples)\n");

            // Sample IN-BIOME positions (200 each) rather than 200 map-wide —
            // a rare biome like jungle otherwise gets ~20 samples and the result
            // reads as "impossible" when it's merely uncommon.
            sb.Append("Placement test per trader (200 in-biome positions, 6 orientations each):\n");
            foreach (var kv in _traders)
            {
                var mask = BiomeMask(kv.Value.Biome);
                int sampled = 0, ok = 0, trees = 0;
                for (var i = 0; i < 40000 && sampled < 200; i++)
                {
                    var pos = new Vector3(UnityEngine.Random.Range(-half, half), 0f, UnityEngine.Random.Range(-half, half));
                    pos.y = TerrainMeta.HeightMap.GetHeight(pos);
                    if (pos.y < 2f) continue;
                    if (mask != 0 && TerrainMeta.BiomeMap.GetBiome(pos, mask) < 0.4f) continue;
                    sampled++;
                    if (TerrainMeta.TopologyMap.GetTopology(pos, AvoidTopology)) continue;
                    for (var t = 0; t < 6; t++)
                    {
                        bool treesOnly;
                        if (!FootprintClear(pos, UnityEngine.Random.Range(0f, 360f), out treesOnly)) continue;
                        ok++;
                        if (treesOnly) trees++;
                        break;
                    }
                }
                var coverage = land > 0 ? counts[Array.IndexOf(names, kv.Value.Biome)] * 1f / land : 0f;
                var expected = (int)(_config.ShopSearchAttempts * coverage * (sampled > 0 ? ok * 1f / sampled : 0f));
                var fb = string.IsNullOrEmpty(kv.Value.BiomeFallback) ? "none" : kv.Value.BiomeFallback;
                sb.Append($"  {kv.Key,-9} '{kv.Value.Biome}' (fallback {fb}): {ok}/{sampled} usable, {trees} need trees felled");
                sb.Append(expected <= 0 ? "  << would use fallback\n" : $"  ≈{expected} candidates per placement\n");
            }
            arg.ReplyWith(sb.ToString());
        }

        // Why is a biome rejecting the shop? Samples in-biome positions only and
        // tallies which constraint killed each one, so tuning is aimed rather
        // than guessed. usage: rq.shop.diagnose <biome> [samples]
        [ConsoleCommand("rq.shop.diagnose")]
        private void CmdShopDiagnose(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var biome = arg.HasArgs() ? arg.GetString(0) : "jungle";
            var mask = BiomeMask(biome);
            var want = arg.HasArgs(2) ? arg.GetInt(1) : 400;
            var half = TerrainMeta.Size.x * 0.5f - 250f;

            int inBiome = 0, flat = 0, topo = 0, hard = 0, door = 0, ok = 0, trees = 0, monument = 0, neighbours = 0;
            var spreadTotal = 0f;
            for (var i = 0; i < 60000 && inBiome < want; i++)
            {
                var pos = new Vector3(UnityEngine.Random.Range(-half, half), 0f, UnityEngine.Random.Range(-half, half));
                pos.y = TerrainMeta.HeightMap.GetHeight(pos);
                if (pos.y < 2f) continue;
                if (mask != 0 && TerrainMeta.BiomeMap.GetBiome(pos, mask) < 0.4f) continue;
                inBiome++;

                var near = false;
                foreach (var m in TerrainMeta.Path.Monuments)
                    if (m != null && m.IsInBounds(pos)) { near = true; break; }
                if (near) { monument++; continue; }

                // Ground spread across the footprint, independent of the flatness gate.
                var lo = float.MaxValue; var hi = float.MinValue;
                for (var iz = -2; iz <= 2; iz++)
                {
                    var h = TerrainMeta.HeightMap.GetHeight(pos + new Vector3(0f, 0f, iz * ShopHalfLength * 0.5f));
                    if (h < lo) lo = h;
                    if (h > hi) hi = h;
                }
                spreadTotal += hi - lo;

                var placed = false;
                for (var t = 0; t < 6 && !placed; t++)
                {
                    bool treesOnly; SpotReject why;
                    if (FootprintClear(pos, UnityEngine.Random.Range(0f, 360f), out treesOnly, out why))
                    { ok++; if (treesOnly) trees++; placed = true; break; }
                    if (t < 5) continue; // only tally the last orientation's reason
                    switch (why)
                    {
                        case SpotReject.Flatness: flat++; break;
                        case SpotReject.Topology: topo++; break;
                        case SpotReject.HardBlock: hard++; break;
                        case SpotReject.DoorClearance: door++; break;
                        case SpotReject.Neighbours: neighbours++; break;
                    }
                }
            }

            arg.ReplyWith(
                $"Diagnose '{biome}' — {inBiome} in-biome samples\n" +
                $"  usable:            {ok}  ({trees} need trees felled)\n" +
                $"  rejected flatness: {flat}   (tolerance {_config.ShopFlatness:0.0} m)\n" +
                $"  rejected topology: {topo}\n" +
                $"  rejected rock/build:{hard}  (clear height {_config.ShopClearHeightCfg:0.0} m)\n" +
                $"  rejected doorway:  {door}   (clearance {_config.ShopDoorClearance:0.0} m)\n" +
                $"  rejected neighbours:{neighbours}  (existing build within the {_config.ShopNoBuildRadius:0} m no-build radius)\n" +
                $"  inside monuments:  {monument}\n" +
                $"  mean ground spread over the car's length: {(inBiome > 0 ? spreadTotal / inBiome : 0f):0.00} m\n" +
                "Tune ShopFlatnessMetres / ShopClearHeightMetres / ShopDoorClearanceMetres in the config, then oxide.reload RustQuests.");
        }

        [ConsoleCommand("rq.shop.remove")]
        private void CmdShopRemove(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var key = ResolveTraderKey(arg, 0);
            if (key == null) { arg.ReplyWith(UnknownTraderReply(arg)); return; }
            var shop = Shop(key);
            if (!shop.ShopPlaced) { arg.ReplyWith($"{TraderName(key)} has no shop placed."); return; }
            var killed = RemoveShopEntities(key);
            shop.ShopEnt = null; // stamped — killed by the sweep above
            KillShopMarker(key);
            DespawnTrader(key);
            shop.ShopPlaced = false;
            shop.TraderPlaced = false;
            SaveState();
            RecomputeProtectionHook();
            arg.ReplyWith($"{TraderName(key)}'s shop removed ({killed} entities).");
        }

        // One-step calibration: stand exactly where the trader should stand,
        // facing the way she should face, and run this. Records your spot as her
        // offset from the shop (rotation-aware, mode-correct) and respawns her
        // there so you see the result immediately. Every future placement —
        // including per-wipe autoplace — reassembles her identically.
        [ConsoleCommand("rq.shop.offset")]
        private void CmdShopOffset(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var key = ResolveTraderKey(arg, 0);
            if (key == null) { arg.ReplyWith(UnknownTraderReply(arg)); return; }
            var shop = Shop(key);
            if (!shop.ShopPlaced) { arg.ReplyWith($"Place {TraderName(key)}'s shop first (rq.shop.paste / rq.shop.autoplace)."); return; }
            var player = arg.Player();
            // Prefer the admin's own position; fall back to the trader's when run
            // from RCON with her already standing in the right place.
            Vector3? pos = player != null ? player.transform.position
                         : (shop.Trader != null && !shop.Trader.IsDestroyed ? (Vector3?)shop.Trader.transform.position : null);
            var yaw = player != null ? player.eyes.rotation.eulerAngles.y : shop.TraderYaw;
            if (pos == null) { arg.ReplyWith("Run this in-game while standing where the trader belongs."); return; }

            // Fly mode can't hold a precise height, so snap her feet to the
            // surface underneath: cast down from just above the given point and
            // take the first solid hit (the caboose floor).
            var snapped = "";
            RaycastHit floor;
            if (Physics.Raycast(pos.Value + Vector3.up * 1.5f, Vector3.down, out floor, 6f,
                                global::Rust.Layers.Solid, QueryTriggerInteraction.Ignore))
            {
                snapped = $" (snapped {(pos.Value.y - floor.point.y):+0.00;-0.00} to the floor)";
                pos = new Vector3(pos.Value.x, floor.point.y, pos.Value.z);
            }

            var p = Profile(key);
            var local = Quaternion.Euler(0f, -shop.ShopYawDeg, 0f) * (pos.Value - shop.ShopPos);
            p.TraderOffsetX = local.x; p.TraderOffsetZ = local.z;
            // Y is measured against the shop's own vertical anchor: the applied
            // paste lift in paste mode, the entity origin in prefab mode.
            p.TraderOffsetY = pos.Value.y - shop.ShopY - shop.ShopHeightApplied;
            p.TraderYawOffset = yaw - shop.ShopYawDeg;
            SaveTraders();

            SpawnTrader(key, pos.Value, yaw);
            shop.TraderPlaced = true;
            shop.TraderX = pos.Value.x; shop.TraderY = pos.Value.y; shop.TraderZ = pos.Value.z;
            shop.TraderYaw = yaw;
            SaveState();

            arg.ReplyWith($"{p.Name} moved here{snapped} and offset saved ({p.TraderOffsetX:0.00}, {p.TraderOffsetY:0.00}, {p.TraderOffsetZ:0.00}), yaw {p.TraderYawOffset:0}. Future placements put her here. Fine-tune with rq.trader.nudge <±metres>.");
        }

        // Fine vertical tuning without re-standing: rq.trader.nudge 0.05 lifts
        // her 5cm, -0.05 drops her. Saves to the profile and respawns in place.
        [ConsoleCommand("rq.trader.nudge")]
        private void CmdTraderNudge(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var dy = arg.HasArgs() ? arg.GetFloat(0, 0.05f) : 0.05f;
            var key = ResolveTraderKey(arg, 1);
            if (key == null) { arg.ReplyWith(UnknownTraderReply(arg)); return; }
            MoveTrader(arg, key, 0f, 0f, dy, 0f);
        }

        // Relative repositioning in HER OWN frame, so "back" always means behind
        // her regardless of which way the shop was rotated:
        //   rq.trader.move <back(+)/forward(-)> [right(+)/left(-)] [up] [yaw°] [trader]
        // Every value is metres and optional. Beats re-running rq.shop.offset for
        // small corrections, since you'd have to stand exactly right.
        [ConsoleCommand("rq.trader.move")]
        private void CmdTraderMove(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (!arg.HasArgs())
            {
                arg.ReplyWith("usage: rq.trader.move <back> [right] [up] [yaw] [trader]   (metres/degrees; negatives allowed, e.g. 'rq.trader.move 0.3' steps her back 30cm)");
                return;
            }
            var back = arg.GetFloat(0, 0f);
            var right = arg.GetFloat(1, 0f);
            var up = arg.GetFloat(2, 0f);
            var dyaw = arg.GetFloat(3, 0f);
            var key = ResolveTraderKey(arg, 4);
            if (key == null) { arg.ReplyWith(UnknownTraderReply(arg)); return; }
            MoveTrader(arg, key, back, right, up, dyaw);
        }

        private void MoveTrader(ConsoleSystem.Arg arg, string key, float back, float right, float up, float dyaw)
        {
            var shop = Shop(key);
            if (!shop.TraderPlaced) { arg.ReplyWith($"{TraderName(key)} is not placed."); return; }
            var p = Profile(key);

            // Her facing defines the frame: -forward is "back".
            var rot = Quaternion.Euler(0f, shop.TraderYaw, 0f);
            var delta = rot * new Vector3(right, 0f, -back);
            delta.y = up;

            var pos = new Vector3(shop.TraderX + delta.x, shop.TraderY + delta.y, shop.TraderZ + delta.z);
            var yaw = shop.TraderYaw + dyaw;

            // Re-derive the shop-relative offsets so the change survives every
            // future placement, not just this one.
            var local = Quaternion.Euler(0f, -shop.ShopYawDeg, 0f) * (pos - shop.ShopPos);
            p.TraderOffsetX = local.x; p.TraderOffsetZ = local.z;
            p.TraderOffsetY = pos.y - shop.ShopY - shop.ShopHeightApplied;
            p.TraderYawOffset = yaw - shop.ShopYawDeg;
            SaveTraders();

            SpawnTrader(key, pos, yaw);
            shop.TraderX = pos.x; shop.TraderY = pos.y; shop.TraderZ = pos.z; shop.TraderYaw = yaw;
            SaveState();

            arg.ReplyWith($"{p.Name} moved (back {back:0.00}, right {right:0.00}, up {up:0.00}, yaw {dyaw:0}) — offsets now ({p.TraderOffsetX:0.00}, {p.TraderOffsetY:0.00}, {p.TraderOffsetZ:0.00}), yaw {p.TraderYawOffset:0}.");
        }

        #endregion

        #region World objects (lore notes + codelocked stashes)

        private const string BoxPrefab = "assets/prefabs/deployable/woodenbox/woodbox_deployed.prefab";
        private const string CodeLockPrefab = "assets/prefabs/locks/keypad/lock.code.prefab";

        // Keypads only ever produce four digits — "+123"/" 123" would make a
        // stash permanently unopenable.
        private static bool IsValidLockCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length != 4) return false;
            for (var i = 0; i < 4; i++) if (code[i] < '0' || code[i] > '9') return false;
            return true;
        }

        private StorageContainer SpawnQuestBox(float x, float y, float z, float yawDeg)
        {
            var box = GameManager.server.CreateEntity(BoxPrefab, new Vector3(x, y, z), Quaternion.Euler(0f, yawDeg, 0f)) as StorageContainer;
            if (box == null) { PrintError($"CreateEntity failed for {BoxPrefab}"); return null; }
            box.EnableSaving(false); // we respawn from state each boot
            box.OwnerID = OwnerStamp;
            box.Spawn();
            return box;
        }

        private void SpawnNote(NoteSpawn n)
        {
            if (n.Box != null && !n.Box.IsDestroyed) return; // already standing this boot (Ensure* spawned it before SpawnWorldObjects re-walks the list)
            if (!_noteContent.TryGetValue(n.Tag, out var content))
            { PrintWarning($"Note '{n.Tag}' has no entry in notes.json — skipped."); return; }
            n.Box = SpawnQuestBox(n.X, n.Y, n.Z, n.YawDeg);
            if (n.Box == null) return;
            StockNoteItem(n, content);
        }

        private void StockNoteItem(NoteSpawn n, NoteContent content)
        {
            var note = ItemManager.CreateByName("note", 1);
            if (note == null) return;
            note.text = ExpandTokens(content.Text);
            // Titles expand too (Phase F) — "FIELD NOTES №{fieldnote}" is an
            // item name.
            if (!string.IsNullOrEmpty(content.Title)) note.name = ExpandTokens(content.Title);
            _noteTags[note.uid.Value] = n.Tag; // credit resolves by tag, not title
            if (!note.MoveToContainer(n.Box.inventory)) note.Remove();
        }

        // The press keeps leaving copies: a taken sheet is replaced a tick
        // later, so the box is never empty for the next reader. Without this,
        // one player pocketing the letter starved everyone else's "find the
        // field sheet" objective until the next restart (Stage 1 review).
        private void RestockNoteBox(string tag)
        {
            NoteContent content;
            if (!_noteContent.TryGetValue(tag, out content) || content == null) return;
            foreach (var n in _state.Notes)
            {
                if (n.Tag != tag || n.Box == null || n.Box.IsDestroyed || n.Box.inventory == null) continue;
                if (n.Box.inventory.itemList.Count > 0) continue; // still stocked (or a reader put theirs back)
                StockNoteItem(n, content);
            }
        }

        // --- welcome note ---------------------------------------------------
        // The island's onboarding: who's out here, and how the journal works.
        // Once per player per wipe, delivered on the first wake-up so the
        // inventory exists and the join spam has cleared.

        private const string WelcomeNoteTag = "welcome";

        private void TryGiveWelcomeNote(BasePlayer player, bool force = false)
        {
            if (player == null || player.IsNpc) return;
            // FakeFriends bots are BasePlayers that pass IsNpc — same
            // exclusion the kill path uses. And on a cold boot, sleepers
            // (mostly those bots) wake BEFORE OnServerInitialized has loaded
            // state — three NREs per boot in the live log (v1.11-era, found
            // 2026-08-10). Real players get the note on their next wake.
            if (!player.userID.IsSteamId()) return;
            if (_state == null) return;
            if (!force && !_config.WelcomeNote) return;
            var id = (ulong)player.userID;
            if (!force && _state.Welcomed.Contains(id)) return;

            NoteContent content;
            if (!_noteContent.TryGetValue(WelcomeNoteTag, out content))
            { PrintWarning($"Note '{WelcomeNoteTag}' has no entry in notes.json — welcome note skipped."); return; }

            // Claim the slot before the delay: two wake-ups in five seconds
            // (respawn straight after a sleep-end) must not double-deliver.
            if (!_state.Welcomed.Contains(id)) { _state.Welcomed.Add(id); SaveState(); }

            timer.Once(5f, () =>
            {
                if (player == null || !player.IsConnected) return;
                var note = ItemManager.CreateByName("note", 1);
                if (note == null) { PrintWarning("Item 'note' unknown — welcome note skipped."); return; }
                note.text = ExpandTokens(content.Text);
                if (!string.IsNullOrEmpty(content.Title)) note.name = ExpandTokens(content.Title);
                if (!player.inventory.GiveItem(note))
                {
                    note.Drop(player.GetDropPosition(), player.GetDropVelocity());
                    PrintToChat(player, L("Welcome.Dropped", player));
                }
                PrintToChat(player, L("Welcome.Chat", player));
            });
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            TryGiveWelcomeNote(player);
            // An unpaid auto-claim whose tick found the player disconnected
            // (the RPC kick, a crash mid-claim) has no other retry — the
            // quest sits complete-but-unclaimed forever (found live
            // 2026-08-10: BACK PAY 1 stranded after the MoveItem kick).
            if (player != null && !player.IsNpc && player.userID.IsSteamId() && _state != null)
            {
                SweepUnpaidAutoClaims(player);
                // Delayed pieces land on wake (I.5) — a beat after the welcome
                // note's 5s so two papers never talk over each other in chat.
                timer.Once(8f, () =>
                {
                    if (player != null && player.IsConnected) DeliverDuePieces(player);
                });
            }
        }

        // Claim anything of The Unpaid's that finished while nobody was
        // around to pay out. Collect first — the claim mutates Active.
        private void SweepUnpaidAutoClaims(BasePlayer player)
        {
            var prog = GetProgress(player, create: false);
            if (prog == null || prog.Active.Count == 0) return;
            List<QuestDef> pending = null;
            foreach (var kv in prog.Active)
            {
                var q = FindQuest(kv.Key);
                if (q == null || q.Giver != UnpaidGiverKey) continue;
                if (!AllObjectivesMet(player, q, kv.Value)) continue;
                if (pending == null) pending = new List<QuestDef>();
                pending.Add(q);
            }
            if (pending != null)
                for (var i = 0; i < pending.Count; i++) TryAutoClaimUnpaid(player, pending[i]);
        }

        [ConsoleCommand("rq.note.welcome")]
        private void CmdNoteWelcome(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var player = arg.Player();
            if (player == null) { arg.ReplyWith("In-game only."); return; }
            TryGiveWelcomeNote(player, force: true);
            arg.ReplyWith("Welcome note incoming (5s).");
        }

        private void SpawnStash(StashSpawn s)
        {
            if (s.Box != null && !s.Box.IsDestroyed) return; // wipe-boot double-spawn guard: EnsureAllFinaleStashes spawns fresh stashes, then SpawnWorldObjects walks the same list
            s.Box = SpawnQuestBox(s.X, s.Y, s.Z, s.YawDeg);
            if (s.Box == null) return;

            var lockEnt = GameManager.server.CreateEntity(CodeLockPrefab, Vector3.zero) as CodeLock;
            if (lockEnt == null) { PrintError($"CreateEntity failed for {CodeLockPrefab}"); return; }
            lockEnt.gameObject.Identity();
            lockEnt.SetParent(s.Box, s.Box.GetSlotAnchorName(BaseEntity.Slot.Lock));
            lockEnt.EnableSaving(false);
            lockEnt.OwnerID = OwnerStamp;
            lockEnt.Spawn();
            lockEnt.code = s.Code;
            lockEnt.hasCode = true;
            s.Box.SetSlot(BaseEntity.Slot.Lock, lockEnt);
            SetFlagNet(lockEnt, BaseEntity.Flags.Locked, true);
            s.Lock = lockEnt;
        }

        // --- dead drops + auto-placed notes (v2 Phase B) -------------------

        private void SpawnDrop(DropSpawn d)
        {
            if (d.Box != null && !d.Box.IsDestroyed) return; // same double-spawn guard as notes/stashes
            d.Box = SpawnQuestBox(d.X, d.Y, d.Z, d.YawDeg);
        }

        // Ring placement outside a monument: close enough that "near the
        // {monument}" reads true, far enough that the MONUMENT topology mask
        // rejects the pad itself. Range runs long because big monuments
        // (airfield, launch) put their center 100m+ from their edge; the
        // topology filter finds the seam. Falls back to anywhere-valid — the
        // prose never promises precision (deliberate: monument layouts drift
        // with Facepunch patches, decision 0004.9).
        private bool TryFindSpotNearMonument(Vector3 center, out Vector3 spot)
        {
            spot = default(Vector3);
            // Phased like the shop search: close-in first (findable beats
            // hidden — a 70m-out woodbox was a needle in grass, found live
            // 2026-08-10), widening only when the near ring is all pad/water.
            for (var attempt = 0; attempt < 220; attempt++)
            {
                var ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                var dist = attempt < 120 ? UnityEngine.Random.Range(15f, 45f) : UnityEngine.Random.Range(45f, 110f);
                var pos = new Vector3(center.x + Mathf.Cos(ang) * dist, 0f, center.z + Mathf.Sin(ang) * dist);
                pos.y = TerrainMeta.HeightMap.GetHeight(pos);
                if (pos.y < 2f) continue;
                if (TerrainMeta.TopologyMap.GetTopology(pos, AvoidTopology)) continue;
                if (Physics.CheckSphere(pos + Vector3.up * 0.5f, 1.5f, StashBlockLayers, QueryTriggerInteraction.Ignore)) continue;
                spot = pos;
                return true;
            }
            return TryFindStashSpot(out spot);
        }

        // Tight ring for paper that promises "near here" — 10-30m of the
        // anchor, same cheap validation.
        private bool TryFindSpotNearBox(Vector3 center, out Vector3 spot)
        {
            spot = default(Vector3);
            for (var attempt = 0; attempt < 80; attempt++)
            {
                var ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                var dist = UnityEngine.Random.Range(10f, 30f);
                var pos = new Vector3(center.x + Mathf.Cos(ang) * dist, 0f, center.z + Mathf.Sin(ang) * dist);
                pos.y = TerrainMeta.HeightMap.GetHeight(pos);
                if (pos.y < 2f) continue;
                if (TerrainMeta.TopologyMap.GetTopology(pos, AvoidTopology)) continue;
                if (Physics.CheckSphere(pos + Vector3.up * 0.5f, 1.5f, StashBlockLayers, QueryTriggerInteraction.Ignore)) continue;
                spot = pos;
                return true;
            }
            return false; // caller falls back to the monument ring
        }

        // Every active quest's `drop` tag gets one box per wipe. Placement:
        // the bible's monument binding of the SAME KEY as the tag (that's the
        // authoring convention), else anywhere valid. Idempotent — existing
        // placements are kept, same rule as the stash scanner.
        private void EnsureAllDrops()
        {
            foreach (var q in _questIndex.Values)
                for (var i = 0; i < q.Objectives.Count; i++)
                {
                    var o = q.Objectives[i];
                    if (o.Type != "drop" || string.IsNullOrEmpty(o.Target)) continue;
                    EnsureDrop(o.Target);
                }
        }

        private void EnsureDrop(string tag)
        {
            for (var i = 0; i < _state.Drops.Count; i++)
                if (string.Equals(_state.Drops[i].Tag, tag, StringComparison.OrdinalIgnoreCase)) return;
            Vector3 spot;
            MonumentBinding bind;
            var bound = _state.Bible != null && _state.Bible.Monuments.TryGetValue(tag, out bind) && bind != null
                ? TryFindSpotNearMonument(bind.Pos, out spot)
                : TryFindStashSpot(out spot);
            if (!bound)
            {
                PrintWarning($"Dead drop '{tag}': no valid spot found — retrying next boot.");
                return;
            }
            var d = new DropSpawn { Tag = tag, X = spot.x, Y = spot.y, Z = spot.z, YawDeg = UnityEngine.Random.Range(0f, 360f) };
            _state.Drops.Add(d);
            SpawnDrop(d);
            SaveState();
            RecomputeProtectionHook();
            DLog($"Dead drop '{tag}' placed at {spot} ({MapHelper.PositionToString(spot)}).");
        }

        // The LorePlacer v1 deferred: notes with a Monument binding key place
        // themselves in the ring when their arc is live. Idempotent by tag.
        private void EnsurePlacedNotes()
        {
            var placed = 0;
            foreach (var kv in _noteContent)
            {
                var c = kv.Value;
                if (c == null || string.IsNullOrEmpty(c.Monument) || !ArcActive(c.Arc)) continue;
                var exists = false;
                for (var i = 0; i < _state.Notes.Count; i++)
                    if (_state.Notes[i].Tag == kv.Key) { exists = true; break; }
                if (exists) continue;

                // A note whose binding key is also a drop tag anchors to the
                // BOX, tight — "there is a wooden box near here" must be
                // true (found live 2026-08-10: sheet and box landed 130m
                // apart when both ringed the monument independently).
                var spot = default(Vector3);
                var ok = false;
                for (var i = 0; i < _state.Drops.Count && !ok; i++)
                    if (string.Equals(_state.Drops[i].Tag, c.Monument, StringComparison.OrdinalIgnoreCase))
                        ok = TryFindSpotNearBox(new Vector3(_state.Drops[i].X, _state.Drops[i].Y, _state.Drops[i].Z), out spot);
                if (!ok)
                {
                    MonumentBinding bind;
                    ok = _state.Bible != null && _state.Bible.Monuments.TryGetValue(c.Monument, out bind) && bind != null
                        ? TryFindSpotNearMonument(bind.Pos, out spot)
                        : TryFindStashSpot(out spot);
                }
                if (!ok)
                {
                    PrintWarning($"Note '{kv.Key}': no valid spot near '{c.Monument}' — retrying next boot.");
                    continue;
                }
                var n = new NoteSpawn { Tag = kv.Key, X = spot.x, Y = spot.y, Z = spot.z, YawDeg = UnityEngine.Random.Range(0f, 360f) };
                _state.Notes.Add(n);
                SpawnNote(n);
                placed++;
                DLog($"Note '{kv.Key}' placed near '{c.Monument}' at {MapHelper.PositionToString(spot)}.");
            }
            if (placed > 0)
            {
                SaveState();
                RecomputeProtectionHook();
                RecomputeEngineHooks(); // a grant-bearing note just entered the world
            }
        }

        // Loot goes straight to the player who earned it, NOT into the box.
        // A shared world container let a second finisher (or anyone who learned
        // the code) walk off with someone else's share, and anything left inside
        // vanished on the next restart because the box is EnableSaving(false).
        private void GrantStashLoot(StashSpawn s, BasePlayer player)
        {
            if (!_stashContent.TryGetValue(s.Tag, out var content))
            { PrintWarning($"Stash '{s.Tag}' has no entry in stashes.json — nothing to grant."); return; }
            foreach (var r in content.Loot)
            {
                var def = ItemManager.FindItemDefinition(r.Item);
                if (def == null) { PrintWarning($"Stash '{s.Tag}': unknown loot item '{r.Item}'."); continue; }
                GrantRewardItem(player, def, r); // same stack-legal path as quest rewards
            }
        }

        // Has this player already been granted this stash's loot?
        private static bool HasClaimedStash(StashSpawn s, BasePlayer player) => s.ClaimedBy.Contains((ulong)player.userID);

        // The finale stash hides somewhere new each wipe with a fresh code —
        // Sonia's parting gift is a real treasure hunt. Dialog text pulls the
        // grid + code through {grid}/{code} tokens, so the clue always matches
        // the world.
        private const string FinaleStashTag = "sonia_finale";

        private StashSpawn FindStash(string tag)
        {
            foreach (var s in _state.Stashes) if (s.Tag == tag) return s;
            return null;
        }

        // Each trader whose profile names a FinaleStashTag gets one hidden stash
        // per wipe with its own random location and code.
        private void EnsureAllFinaleStashes()
        {
            foreach (var kv in _traders)
                if (!string.IsNullOrEmpty(kv.Value.FinaleStashTag)) EnsureFinaleStash(kv.Value.FinaleStashTag);

            // Any stash a quest actually asks for — covers the cross-trader
            // vault, which belongs to no single trader's profile.
            for (var i = 0; i < _quests.Count; i++)
            {
                var q = _quests[i];
                if (!QuestActiveThisWipe(q)) continue;
                for (var j = 0; j < q.Objectives.Count; j++)
                {
                    var o = q.Objectives[j];
                    if (o.Type == "code" && !string.IsNullOrEmpty(o.Target)) EnsureFinaleStash(o.Target);
                }
            }
        }

        private void EnsureFinaleStash(string tag = FinaleStashTag)
        {
            var FinaleStashTag = tag; // local shadow keeps the body below unchanged
            if (FindStash(FinaleStashTag) != null) return;
            if (!_stashContent.ContainsKey(FinaleStashTag)) return;
            if (!TryFindStashSpot(out var spot))
            {
                PrintWarning("Finale stash: no valid spot found — retrying next boot (or place manually with rq.stash.place).");
                return;
            }
            var s = new StashSpawn
            {
                Tag = tag,
                Code = UnityEngine.Random.Range(1000, 10000).ToString(),
                X = spot.x, Y = spot.y, Z = spot.z,
                YawDeg = UnityEngine.Random.Range(0f, 360f),
            };
            _state.Stashes.Add(s);
            SpawnStash(s);
            SaveState();
            // Must re-run: this stash is usually created AFTER SpawnWorldObjects
            // decided there were none, which left OnCodeEntered unsubscribed and
            // the finale uncompletable for the whole session.
            RecomputeWorldHooks();
            RecomputeProtectionHook();
            DLog($"Finale stash hidden at {spot} ({MapHelper.PositionToString(spot)}), code {s.Code}.");
        }

        // Human-readable reward list: "Hunting Bow, 48x Wooden Arrow, 20x Scrap".
        // Lets dialog use {rewards} instead of hardcoding amounts, so retuning
        // rewards in quests.json never leaves the text lying.
        private string RewardSummary(List<RewardDef> rewards)
        {
            if (rewards == null || rewards.Count == 0) return "nothing";
            var sb = new StringBuilder();
            for (var i = 0; i < rewards.Count; i++)
            {
                var r = rewards[i];
                var def = ItemManager.FindItemDefinition(r.Item);
                var name = def != null ? def.displayName.english : r.Item;
                if (sb.Length > 0) sb.Append(i == rewards.Count - 1 ? " and " : ", ");
                sb.Append(r.Amount > 1 ? $"{r.Amount}x {name}" : name);
                if (r.Water > 0) sb.Append($" (full, {r.Water} ml each)");
                if (r.Attachments != null && r.Attachments.Count > 0)
                {
                    sb.Append(" (fitted: ");
                    for (var a = 0; a < r.Attachments.Count; a++)
                    {
                        var mdef = ItemManager.FindItemDefinition(r.Attachments[a]);
                        if (a > 0) sb.Append(", ");
                        sb.Append(mdef != null ? mdef.displayName.english : r.Attachments[a]);
                    }
                    sb.Append(')');
                }
            }
            return sb.ToString();
        }

        // {trader}/{code}/{grid}/{rewards} in authored dialog + note text.
        // {code}/{grid} resolve against the stash belonging to the speaking
        // trader; {rewards} against the quest being offered.
        // prog is optional (Phase G): only the {choice} token reads it — the
        // player's own pick where known, else the island's first, else a
        // neutral line.
        private string ExpandTokens(string text, string traderKey = DefaultTraderKey, QuestDef quest = null, PlayerProgress prog = null)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) return text;
            if (text.Contains("{choice}"))
            {
                var c = ActiveChoice();
                ChoiceOption opt = null;
                if (c != null)
                {
                    string pick = null;
                    if (prog != null) prog.Choices.TryGetValue(c.Id, out pick);
                    if (pick == null && _state?.Bible?.Choice != null) pick = _state.Bible.Choice.FirstPick;
                    opt = FindChoiceOption(c, pick);
                }
                text = text.Replace("{choice}", opt != null && !string.IsNullOrEmpty(opt.Label) ? opt.Label : "nothing, yet");
            }
            text = text.Replace("{trader}", TraderName(traderKey));
            // {monument:<key>}/{monumentgrid:<key>} — the bible's bound
            // monuments, so authored prose survives whatever the map offers
            // (v2 Phase B). Unknown keys stay literal and obvious.
            if (text.IndexOf("{monument", StringComparison.Ordinal) >= 0 && _state != null && _state.Bible != null)
                foreach (var kv in _state.Bible.Monuments)
                {
                    if (kv.Value == null) continue;
                    text = text.Replace("{monument:" + kv.Key + "}", kv.Value.DisplayName)
                               .Replace("{monumentgrid:" + kv.Key + "}", MapHelper.PositionToString(kv.Value.Pos));
                }
            // {chain:n} — the nth trader in this wipe's rolled vouch order
            // (1-based). Canon and referral prose survive any shuffle.
            if (text.IndexOf("{chain:", StringComparison.Ordinal) >= 0 && _state != null && _state.Bible != null)
            {
                var order = _state.Bible.ChainOrder;
                for (var i = 0; i < order.Count; i++)
                    text = text.Replace("{chain:" + (i + 1) + "}", TraderName(order[i]));
            }
            // {nexthint} — how to describe the next trader without naming a
            // biome-truth the shuffle could falsify. {onward} — the whole
            // referral sentence: hand-on to the next trader mid-chain, or
            // back to the head for the vault when she's the last link. These
            // two are what make one authored closing line true in any order.
            if (text.Contains("{nexthint}"))
            {
                var nk = NextTraderKey(traderKey);
                var hint = nk != null ? Profile(nk)?.LocatorHint : null;
                text = text.Replace("{nexthint}", string.IsNullOrEmpty(hint) ? "somewhere out there" : hint);
            }
            if (text.Contains("{onward}"))
            {
                var nk = NextTraderKey(traderKey);
                string onward;
                if (nk != null)
                {
                    var hint = Profile(nk)?.LocatorHint;
                    var nShop = Shop(nk);
                    onward = $"Go find {TraderName(nk)}" +
                             (string.IsNullOrEmpty(hint) ? "" : $" — {hint}") +
                             (nShop.ShopPlaced ? $", out around {MapHelper.PositionToString(nShop.ShopPos)}" : "") +
                             $". Tell her {TraderName(traderKey)} vouched for you.";
                }
                else
                {
                    var order = _state != null && _state.Bible != null ? _state.Bible.ChainOrder : null;
                    var first = order != null && order.Count > 0 ? TraderName(order[0]) : "the first of us";
                    onward = $"You've settled with all of us now. Take it back to {first} — the last door opens at her counter.";
                }
                text = text.Replace("{onward}", onward);
            }
            // {role} — the speaking trader's rolled role, phrased by the
            // template; {faction}; {press} — the rolled press's NAME.
            // {press} is for POST-REVEAL prose only (finale/choice text) —
            // never put it in offers, notes or slips, it spoils the mystery.
            if (text.Contains("{role}"))
            {
                var phrase = "";
                var tpl = ActiveTemplate();
                string role;
                if (tpl != null && _state?.Bible != null && _state.Bible.Roles.TryGetValue(traderKey, out role) && role != null)
                    tpl.RolePhrases.TryGetValue(role, out phrase);
                text = text.Replace("{role}", string.IsNullOrEmpty(phrase) ? "one of the four" : phrase);
            }
            text = text.Replace("{faction}", "The Unpaid");
            // Wipe memory (v2 Phase F): the island counts its resets. Authored
            // content that references memory must survive a first wipe, hence
            // the neutral fallbacks.
            if (text.Contains("{wipes}"))
                text = text.Replace("{wipes}", _state != null ? _state.WipeCounter.ToString() : "?");
            if (text.Contains("{lastwipe.template}"))
            {
                var lastId = _state != null && _state.Memory != null ? _state.Memory.LastTemplateId : null;
                var lastName = string.IsNullOrEmpty(lastId) ? null : (FindTemplate(lastId)?.Name ?? lastId);
                text = text.Replace("{lastwipe.template}", string.IsNullOrEmpty(lastName) ? "the one before" : lastName);
            }
            if (text.Contains("{lastwipe.choice}"))
            {
                var lastChoice = _state != null && _state.Memory != null ? _state.Memory.LastChoiceOutcome : null;
                text = text.Replace("{lastwipe.choice}", string.IsNullOrEmpty(lastChoice) ? "nothing anyone wrote down" : lastChoice);
            }
            // {fieldnote} — this wipe's claimed FIELD NOTES number. "?" only
            // ever renders on a broken state file; ApplyBible adopts a number
            // for every bible it touches.
            if (text.Contains("{fieldnote}"))
            {
                var fn = _state != null && _state.Bible != null ? _state.Bible.FieldNoteNumber : 0;
                text = text.Replace("{fieldnote}", fn > 0 ? fn.ToString() : "?");
            }
            if (text.Contains("{press}"))
            {
                string pressKey = null;
                if (_state?.Bible != null) _state.Bible.Secrets.TryGetValue("press_identity", out pressKey);
                text = text.Replace("{press}", pressKey != null && Profile(pressKey) != null ? TraderName(pressKey) : "nobody");
            }
            // {secret:<id>} — the generalized {press} (Phase H): a rolled
            // secret's value, rendered as a trader's NAME when it is one.
            // Same rule as {press}: POST-REVEAL prose only (endings, verdict
            // notes) — never offers, rumor notes or slips.
            if (text.IndexOf("{secret:", StringComparison.Ordinal) >= 0 && _state?.Bible != null)
                foreach (var kv in _state.Bible.Secrets)
                {
                    var v = kv.Value != null && Profile(kv.Value) != null ? TraderName(kv.Value) : (kv.Value ?? "nobody");
                    text = text.Replace("{secret:" + kv.Key + "}", v);
                }
            // {next}/{nextgrid} — who this trader hands you on to, and where her
            // shop actually landed this wipe (referral lines in ChainDone/Pending).
            if (text.Contains("{next}") || text.Contains("{nextgrid}"))
            {
                var nextKey = NextTraderKey(traderKey);
                var nextShop = nextKey != null ? Shop(nextKey) : null;
                text = text.Replace("{next}", nextKey != null ? TraderName(nextKey) : "the others")
                           .Replace("{nextgrid}", nextShop != null && nextShop.ShopPlaced
                               ? MapHelper.PositionToString(nextShop.ShopPos)
                               : "somewhere out there");
            }
            // {target}/{count} — a pooled turn-in's rolled item (display
            // name) and amount; the authored values when nothing rolled.
            // Bound to the quest's FIRST pooled objective — one pool per
            // quest is the supported shape (validation warns past that).
            if (quest != null && (text.Contains("{target}") || text.Contains("{count}")))
                for (var i = 0; i < quest.Objectives.Count; i++)
                {
                    var o = quest.Objectives[i];
                    if (string.IsNullOrEmpty(o.Pool)) continue;
                    var shortname = EffectiveTarget(quest, i, o);
                    var idef = ItemManager.FindItemDefinition(shortname);
                    text = text.Replace("{target}", idef != null ? idef.displayName.english : shortname)
                               .Replace("{count}", EffectiveCount(quest, i, o).ToString());
                    break;
                }
            if (quest != null && text.Contains("{rewards}"))
                // Purchase quests (Good Soil, Tools of the Trade) pay through
                // GrantOnAccept and have no Rewards at all — without the
                // fallback their offers read "…you walk out of here a farmer:
                // nothing." (found live). Claim rewards win when both exist.
                text = text.Replace("{rewards}", RewardSummary(quest.Rewards.Count > 0 ? quest.Rewards : quest.GrantOnAccept));
            if (text.Contains("{code}") || text.Contains("{grid}"))
            {
                // Prefer the stash this quest's own `code` objective points at
                // (the vault isn't any trader's own stash); fall back to hers.
                string tag = null;
                if (quest != null)
                    for (var i = 0; i < quest.Objectives.Count; i++)
                        if (quest.Objectives[i].Type == "code") { tag = quest.Objectives[i].Target; break; }
                if (string.IsNullOrEmpty(tag))
                {
                    var prof = Profile(traderKey);
                    tag = prof != null && !string.IsNullOrEmpty(prof.FinaleStashTag) ? prof.FinaleStashTag : FinaleStashTag;
                }
                var s = FindStash(tag);
                text = text.Replace("{code}", s?.Code ?? "????")
                           .Replace("{grid}", s != null ? MapHelper.PositionToString(new Vector3(s.X, s.Y, s.Z)) : "somewhere out there");
            }
            return text;
        }

        private void SpawnWorldObjects()
        {
            foreach (var n in _state.Notes) SpawnNote(n);
            foreach (var s in _state.Stashes)
            {
                if (s.ClaimedBy == null) s.ClaimedBy = new List<ulong>();
                SpawnStash(s);
            }
            foreach (var d in _state.Drops) SpawnDrop(d);
            RecomputeWorldHooks();
            RecomputeProtectionHook();
        }

        private void DespawnWorldObjects()
        {
            foreach (var n in _state.Notes)
                if (n.Box != null && !n.Box.IsDestroyed) n.Box.Kill();
            foreach (var s in _state.Stashes)
                if (s.Box != null && !s.Box.IsDestroyed) s.Box.Kill(); // lock dies with its parent
            foreach (var d in _state.Drops)
                if (d.Box != null && !d.Box.IsDestroyed) d.Box.Kill();
        }

        private void RecomputeWorldHooks()
        {
            if (_state.Stashes.Count > 0)
            {
                Subscribe(nameof(OnCodeEntered));
                Subscribe(nameof(OnLootEntity));
            }
            else
            {
                Unsubscribe(nameof(OnCodeEntered));
                Unsubscribe(nameof(OnLootEntity));
            }
        }

        // Opening the box is the real completion event. Code entry alone isn't
        // enough: Sonia reveals the code in her OFFER text, so a player can
        // unlock the box before accepting the quest — after that the keypad
        // never appears again (they're whitelisted) and OnCodeEntered could
        // never fire for them, softlocking the finale.
        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null || player.IsNpc) return;
            for (var i = 0; i < _state.Stashes.Count; i++)
            {
                var s = _state.Stashes[i];
                if (!ReferenceEquals(s.Box, entity)) continue;
                TryGrantStash(s, player);
                break;
            }
        }

        // One share per player, granted straight to them. Safe to call from
        // either entry point (code entry or opening the box).
        private void TryGrantStash(StashSpawn s, BasePlayer player)
        {
            if (HasClaimedStash(s, player)) return;
            if (!HasActiveTagObjective(player, "code", s.Tag)) return;
            s.ClaimedBy.Add((ulong)player.userID);
            GrantStashLoot(s, player);
            SaveState();
            PrintToChat(player, L("Stash.Stocked", player));
            DLog($"{player.displayName} claimed stash '{s.Tag}' ({s.ClaimedBy.Count} claim(s) total).");
            ProgressTagObjective(player, "code", s.Tag);
        }

        // Correct code on one of our stashes: per-player loot grant + `code`
        // objective credit. Never cancels — vanilla handles the unlock.
        private object OnCodeEntered(CodeLock codeLock, BasePlayer player, string code)
        {
            if (codeLock == null || player == null || player.IsNpc) return null;
            foreach (var s in _state.Stashes)
            {
                if (!ReferenceEquals(s.Lock, codeLock)) continue;
                if (code != s.Code) break;
                TryGrantStash(s, player); // credits + grants; no-op if already claimed or not on the quest
                break;
            }
            return null;
        }

        private bool HasActiveTagObjective(BasePlayer player, string type, string key)
        {
            var prog = GetProgress(player);
            if (prog == null) return false;
            foreach (var kv in prog.Active)
            {
                var q = FindQuest(kv.Key);
                if (q == null) continue;
                for (var i = 0; i < q.Objectives.Count; i++)
                {
                    var o = q.Objectives[i];
                    if (o.Type != type || !string.Equals(o.Target, key, StringComparison.OrdinalIgnoreCase)) continue;
                    var stored = i < kv.Value.Count ? kv.Value[i] : 0;
                    if (stored < o.Count) return true;
                }
            }
            return false;
        }

        // readnote credit — subscribed by RecomputeEngineHooks only while an
        // online player has an unfinished readnote objective.
        // Fires for every item moved into any container server-wide, so gate on
        // an int compare. Credit resolves the note's TAG (not its display title,
        // which is what the objective schema documents and what `code` uses).
        private int _noteItemId = -1;
        private int _photoItemId = -1;
        private readonly Dictionary<ulong, string> _noteTags = new Dictionary<ulong, string>();

        // The paper hook (v2 Phase B grew it from readnote-only): lore-note
        // credit and note-grants, pamphlet pickup-grants and plants, and
        // dead-drop deposits all ride item movement. Subscribed only while
        // something above actually needs it (RecomputeEngineHooks).
        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            if (item == null || container == null) return;
            if (item.info.itemid == _noteItemId)
            {
                string tag;
                if (!_noteTags.TryGetValue(item.uid.Value, out tag)) return;
                var player = container.GetOwnerPlayer();
                if (player == null || player.IsNpc) return;
                // We are INSIDE the MoveItem RPC here — anything that mutates
                // an inventory mid-move (grants, auto-claims, takes) corrupts
                // the move and kicks the client (found live 2026-08-10,
                // bandage deposit). Everything down-path defers one tick.
                //
                // Paper grants work: the sheet is the offer. Grant BEFORE the
                // readnote credit, so a sheet that grants a quest whose
                // objective is reading that very sheet completes in the same
                // pickup. TryAcceptQuest quietly refuses unless genuinely
                // Available, so re-reading never double-grants.
                NextTick(() =>
                {
                    if (player == null || !player.IsConnected) return;
                    NoteContent content;
                    if (_noteContent.TryGetValue(tag, out content) && content != null
                        && !string.IsNullOrEmpty(content.GrantsQuest) && ArcActive(content.Arc))
                    {
                        var q = FindQuest(content.GrantsQuest);
                        if (q != null) TryAcceptQuest(player, q);
                    }
                    ProgressTagObjective(player, "readnote", tag);
                    RestockNoteBox(tag); // deferred tick — safe to stock the box here
                });
                return;
            }
            if (item.info.itemid == _photoItemId) { OnPamphletMoved(container, item); return; }
            if (_state.Drops.Count > 0) OnDropDeposit(container, item);
        }

        // Who has this world container open right now. Deposits into our
        // boxes have no container owner — the looting session is the link.
        private BasePlayer LootingPlayer(BaseEntity box)
        {
            if (box == null) return null;
            foreach (var p in BasePlayer.activePlayerList)
                if (p != null && !p.IsNpc && p.inventory != null && p.inventory.loot != null
                    && ReferenceEquals(p.inventory.loot.entitySource, box)) return p;
            return null;
        }

        private void OnPamphletMoved(ItemContainer container, Item item)
        {
            if (_tuckingPamphlet) return; // our own loot-seeding, not a player
            var photo = ItemModAssociatedEntity<PhotoEntity>.GetAssociatedEntity(item);
            if (photo == null || !IsOurStamp(photo.PhotographerSteamId)) return;

            var player = container.GetOwnerPlayer();
            if (player != null && !player.IsNpc)
            {
                // Into their own inventory — a pickup. Maybe it's a recruiter.
                // Deferred: the grant can hand items over, and we're mid-RPC.
                NextTick(() => { if (player != null && player.IsConnected) TryPamphletRecruit(player); });
                return;
            }
            OnPamphletPlanted(container, item); // progress-only — no inventory mutation, safe in-RPC
        }

        // Any of our prints reaching a player's own hands may recruit. Shared
        // by organic pickup and rq.pamphlet.give — the tuck guard that keeps
        // loot-seeding silent must not keep the TEST command silent (found
        // live 2026-08-10: give never granted A READER).
        private bool TryPamphletRecruit(BasePlayer player)
        {
            foreach (var kv in _pamphletMeta)
            {
                var meta = kv.Value;
                if (meta == null || string.IsNullOrEmpty(meta.GrantsQuest) || !ArcActive(meta.Arc)) continue;
                var q = FindQuest(meta.GrantsQuest);
                if (q != null && TryAcceptQuest(player, q)) return true;
            }
            return false;
        }

        // plant objective: one of our prints deposited into a container that
        // is neither the planter's own box nor one of ours, at a monument not
        // already covered. Distinctness lives in PlantLedger per objective.
        private void OnPamphletPlanted(ItemContainer container, Item item)
        {
            var host = container.entityOwner;
            if (host == null || IsOurStamp(host.OwnerID)) return;
            var planter = LootingPlayer(host);
            if (planter == null) return;
            if (host.OwnerID == (ulong)planter.userID) return; // their own box doesn't spread the word

            // Attribution: the monument whose bounds the container stands in.
            string monumentKey = null;
            var monuments = TerrainMeta.Path != null ? TerrainMeta.Path.Monuments : null;
            if (monuments != null)
                foreach (var m in monuments)
                    if (m != null && m.IsInBounds(host.transform.position))
                    {
                        var pos = m.transform.position;
                        monumentKey = $"{m.name}@{Mathf.RoundToInt(pos.x)},{Mathf.RoundToInt(pos.z)}";
                        break;
                    }
            if (monumentKey == null) return; // not at a monument — nobody will read it out here

            var prog = GetProgress(planter);
            if (prog == null || prog.Active.Count == 0) return;
            foreach (var kv in prog.Active)
            {
                var q = FindQuest(kv.Key);
                if (q == null) continue;
                for (var i = 0; i < q.Objectives.Count; i++)
                {
                    var o = q.Objectives[i];
                    if (o.Type != "plant") continue;
                    while (kv.Value.Count <= i) kv.Value.Add(0);
                    if (kv.Value[i] >= o.Count) continue;

                    var ledgerKey = $"{q.Id}:{i}";
                    List<string> planted;
                    if (!prog.PlantLedger.TryGetValue(ledgerKey, out planted))
                        prog.PlantLedger[ledgerKey] = planted = new List<string>();
                    if (planted.Contains(monumentKey)) continue; // this monument already reads the truth

                    planted.Add(monumentKey);
                    kv.Value[i] = planted.Count;
                    MarkProgressDirty(planter);
                    PrintToChat(planter, L("Plant.Credited", planter, planted.Count, o.Count));
                    if (AllObjectivesMet(planter, q, kv.Value))
                    {
                        if (q.Giver == UnpaidGiverKey) NextTick(() => TryAutoClaimUnpaid(planter, q));
                        else PrintToChat(planter, L("Quest.Ready", planter, TraderName(q.Giver)));
                    }
                    RecomputeEngineHooks();
                    return;
                }
            }
        }

        // drop objective: the required items are all in the box at once —
        // then they're consumed and the objective credits. Partial deposits
        // stay in the box (and can be taken back; nothing is owed yet).
        private void OnDropDeposit(ItemContainer container, Item item)
        {
            DropSpawn drop = null;
            for (var i = 0; i < _state.Drops.Count; i++)
            {
                var d = _state.Drops[i];
                if (d.Box != null && !d.Box.IsDestroyed && ReferenceEquals(d.Box.inventory, container)) { drop = d; break; }
            }
            if (drop == null) return;
            var player = LootingPlayer(drop.Box);
            if (player == null) return;
            // Consuming from the box while the MoveItem RPC is still placing
            // the stack corrupts the move and kicks the depositor (found
            // live 2026-08-10). Settle up one tick later, off the RPC.
            NextTick(() => SettleDropDeposit(drop, player));
        }

        private void SettleDropDeposit(DropSpawn drop, BasePlayer player)
        {
            if (drop == null || drop.Box == null || drop.Box.IsDestroyed || drop.Box.inventory == null) return;
            if (player == null || !player.IsConnected) return;
            var container = drop.Box.inventory;
            // Unstick first: a deposit against an already-satisfied objective
            // means an auto-claim went missing — poking the box retries it.
            SweepUnpaidAutoClaims(player);
            var prog = GetProgress(player);
            if (prog == null || prog.Active.Count == 0) return;

            foreach (var kv in prog.Active)
            {
                var q = FindQuest(kv.Key);
                if (q == null) continue;
                for (var i = 0; i < q.Objectives.Count; i++)
                {
                    var o = q.Objectives[i];
                    if (o.Type != "drop" || !string.Equals(o.Target, drop.Tag, StringComparison.OrdinalIgnoreCase)) continue;
                    while (kv.Value.Count <= i) kv.Value.Add(0);
                    if (kv.Value[i] >= o.Count) continue;
                    var def = string.IsNullOrEmpty(o.Item) ? null : ItemManager.FindItemDefinition(o.Item);
                    if (def == null) continue; // validation warned at load
                    if (container.GetAmount(def.itemid, true) < o.Count) continue; // partial — keep filling

                    container.Take(null, def.itemid, o.Count);
                    kv.Value[i] = o.Count;
                    MarkProgressDirty(player);
                    PrintToChat(player, L("Drop.Taken", player));
                    if (AllObjectivesMet(player, q, kv.Value))
                    {
                        if (q.Giver == UnpaidGiverKey) NextTick(() => TryAutoClaimUnpaid(player, q));
                        else PrintToChat(player, L("Quest.Ready", player, TraderName(q.Giver)));
                    }
                    RecomputeEngineHooks();
                    return;
                }
            }
        }

        // The Unpaid have no counter to return to — their quests pay out the
        // moment the work is done. The claim itself is the standard path
        // (turnin-take, rewards, unlocks), just nobody to talk to. On top:
        // the next sheet in their chain hands itself out — they don't wait
        // to be asked.
        private void TryAutoClaimUnpaid(BasePlayer player, QuestDef q)
        {
            if (q == null || q.Giver != UnpaidGiverKey || player == null || !player.IsConnected) return;
            var prog = GetProgress(player);
            List<int> progress;
            if (prog == null || !prog.Active.TryGetValue(q.Id, out progress)) return;
            if (!AllObjectivesMet(player, q, progress)) return;
            if (!TryClaimQuest(player, q)) return;
            PrintToChat(player, L("Unpaid.Claimed", player));
            foreach (var next in _questIndex.Values)
                if (next.Giver == UnpaidGiverKey && next.RequiresQuest == q.Id)
                { TryAcceptQuest(player, next); break; }
        }

        [ConsoleCommand("rq.note.place")]
        private void CmdNotePlace(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var player = arg.Player();
            if (player == null) { arg.ReplyWith("In-game only."); return; }
            if (!arg.HasArgs()) { arg.ReplyWith($"usage: rq.note.place <tag>  (tags: {string.Join(", ", _noteContent.Keys)})"); return; }
            var tag = arg.GetString(0);
            if (!_noteContent.ContainsKey(tag)) { arg.ReplyWith($"No note content for '{tag}' — add it to oxide/data/RustQuests/notes.json first."); return; }

            var pos = player.transform.position + player.eyes.BodyForward() * 1.5f;
            var n = new NoteSpawn { Tag = tag, X = pos.x, Y = pos.y, Z = pos.z, YawDeg = player.eyes.rotation.eulerAngles.y };
            _state.Notes.Add(n);
            SpawnNote(n);
            SaveState();
            RecomputeProtectionHook();
            arg.ReplyWith($"Note '{tag}' placed at {pos}.");
        }

        // Fifth of the sync family: pulls shipped note TEXT (Title/Text) over
        // notes.json for the tags it touches, then respawns any placed boxes
        // carrying that tag so the baked item text refreshes without a boot.
        // Arc/Monument/GrantsQuest wiring is structural and untouched.
        [ConsoleCommand("rq.note.synctext")]
        private void CmdNoteSyncText(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var only = arg.HasArgs() ? arg.GetString(0) : null;
            var synced = 0;
            foreach (var kv in DefaultNotes())
            {
                if (only != null && kv.Key != only) continue;
                NoteContent live;
                if (!_noteContent.TryGetValue(kv.Key, out live) || live == null) continue;
                live.Title = kv.Value.Title;
                live.Text = kv.Value.Text;
                synced++;
            }
            if (synced > 0)
            {
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/notes", _noteContent);
                var respawned = 0;
                foreach (var n in _state.Notes)
                {
                    if (only != null && n.Tag != only) continue;
                    if (n.Box != null && !n.Box.IsDestroyed) n.Box.Kill();
                    n.Box = null;
                    SpawnNote(n);
                    respawned++;
                }
                arg.ReplyWith($"Re-synced text for {synced} note(s); respawned {respawned} placed box(es) with fresh text. Wiring (arc/monument/grants) untouched.");
                return;
            }
            arg.ReplyWith($"No matching note{(only != null ? $" '{only}'" : "")} found.");
        }

        [ConsoleCommand("rq.note.remove")]
        private void CmdNoteRemove(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (!arg.HasArgs()) { arg.ReplyWith("usage: rq.note.remove <tag>"); return; }
            var tag = arg.GetString(0);
            var removed = 0;
            for (var i = _state.Notes.Count - 1; i >= 0; i--)
            {
                if (_state.Notes[i].Tag != tag) continue;
                if (_state.Notes[i].Box != null && !_state.Notes[i].Box.IsDestroyed) _state.Notes[i].Box.Kill();
                _state.Notes.RemoveAt(i);
                removed++;
            }
            SaveState();
            RecomputeProtectionHook();
            arg.ReplyWith($"Removed {removed} note placement(s) for '{tag}'.");
        }

        [ConsoleCommand("rq.stash.place")]
        private void CmdStashPlace(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var player = arg.Player();
            if (player == null) { arg.ReplyWith("In-game only."); return; }
            if (!arg.HasArgs(2)) { arg.ReplyWith($"usage: rq.stash.place <tag> <4-digit-code>  (tags: {string.Join(", ", _stashContent.Keys)})"); return; }
            var tag = arg.GetString(0);
            var code = arg.GetString(1);
            if (!IsValidLockCode(code)) { arg.ReplyWith("Code must be exactly 4 digits (0-9)."); return; }
            if (!_stashContent.ContainsKey(tag)) { arg.ReplyWith($"No stash content for '{tag}' — add it to oxide/data/RustQuests/stashes.json first."); return; }

            var pos = player.transform.position + player.eyes.BodyForward() * 1.5f;
            var s = new StashSpawn { Tag = tag, Code = code, X = pos.x, Y = pos.y, Z = pos.z, YawDeg = player.eyes.rotation.eulerAngles.y };
            _state.Stashes.Add(s);
            SpawnStash(s);
            SaveState();
            RecomputeWorldHooks();
            RecomputeProtectionHook();
            arg.ReplyWith($"Stash '{tag}' placed at {pos} (code {code}).");
        }

        // Re-hides the finale stash somewhere new with a fresh code (dev/testing;
        // players mid-chain will see the updated clue next time they ask Sonia).
        [ConsoleCommand("rq.stash.reroll")]
        private void CmdStashReroll(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var old = FindStash(FinaleStashTag);
            if (old != null)
            {
                if (old.Box != null && !old.Box.IsDestroyed) old.Box.Kill();
                _state.Stashes.Remove(old);
            }
            EnsureFinaleStash();
            RecomputeWorldHooks();
            RecomputeProtectionHook();
            var s = FindStash(FinaleStashTag);
            arg.ReplyWith(s == null
                ? "Re-roll failed — no valid spot found; try again."
                : $"Finale stash re-hidden at {MapHelper.PositionToString(new Vector3(s.X, s.Y, s.Z))}, code {s.Code}.");
        }

        // Third of the sync family (see rq.quest.syncrewards / rq.trader.synctext):
        // pulls shipped stash LOOT TABLES over stashes.json, which otherwise only
        // ever gains keys it has never seen. Discards live tuning for the tags it
        // touches; takes one tag to keep that blast radius small. Placement, code
        // and who has already claimed it live in state.json and are untouched.
        [ConsoleCommand("rq.stash.syncloot")]
        private void CmdStashSyncLoot(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var only = arg.HasArgs() ? arg.GetString(0) : null; // one tag, or all
            var synced = 0;
            foreach (var kv in DefaultStashes())
            {
                if (only != null && kv.Key != only) continue;
                if (!_stashContent.ContainsKey(kv.Key)) continue;
                _stashContent[kv.Key] = kv.Value;
                synced++;
            }
            if (synced > 0) Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/stashes", _stashContent);
            arg.ReplyWith(synced == 0
                ? $"No matching stash loot table{(only != null ? $" '{only}'" : "")} found."
                : $"Re-synced loot for {synced} stash(es) from the plugin. Placements, codes and claim history untouched.");
        }

        [ConsoleCommand("rq.stash.remove")]
        private void CmdStashRemove(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (!arg.HasArgs()) { arg.ReplyWith("usage: rq.stash.remove <tag>"); return; }
            var tag = arg.GetString(0);
            var removed = 0;
            for (var i = _state.Stashes.Count - 1; i >= 0; i--)
            {
                if (_state.Stashes[i].Tag != tag) continue;
                if (_state.Stashes[i].Box != null && !_state.Stashes[i].Box.IsDestroyed) _state.Stashes[i].Box.Kill();
                _state.Stashes.RemoveAt(i);
                removed++;
            }
            SaveState();
            RecomputeWorldHooks();
            RecomputeProtectionHook();
            arg.ReplyWith($"Removed {removed} stash placement(s) for '{tag}'.");
        }

        #endregion

        #region Resistance pamphlets (v1.10)

        // Anti-Cobalt propaganda seeded into crate loot. Same delivery tech as
        // FakeFriends' roleplayer camera prints: curated JPGs (854x480, <=256KB)
        // from oxide/data/RustQuests/pamphlets, stamped onto photo items via
        // FileStorage. Sources + render pipeline live in assets/pamphlets/; the
        // faction behind them is authored in docs/content/resistance.md and is
        // the v2 storyline hook.
        private class PamphletPrint { public string File; public byte[] Bytes; }
        private List<PamphletPrint> _pamphletCache;

        // pamphlets.json (v2 Phase B): per-print metadata, merged by filename
        // with the usual never-overwrite rule. "*" applies to every print
        // without its own entry. Arc on a per-file entry gates that print out
        // of the loot rotation until the arc is live; GrantsQuest makes
        // picking up one of our prints hand out work (paper is the offer).
        // Limitation, documented: a picked-up print can't be traced back to
        // its source file, so the FIRST arc-active GrantsQuest entry wins.
        private class PamphletMeta
        {
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string GrantsQuest;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string Arc;
        }

        private Dictionary<string, PamphletMeta> _pamphletMeta = new Dictionary<string, PamphletMeta>();

        private Dictionary<string, PamphletMeta> DefaultPamphletMeta() => new Dictionary<string, PamphletMeta>
        {
            // Any Unpaid print in your hands starts the conversation — when
            // this wipe's story includes them at all.
            ["*"] = new PamphletMeta { GrantsQuest = "unpaid_contact", Arc = "four_keys.unpaid" },
            // Beat prints ship their gate (H.5): a file with NO entry is in
            // rotation by default, so without this the print would circulate
            // the moment the JPG lands in the press — before any Count wipe
            // ever publishes the sheet. FireBeat's additive write then no-ops.
            ["the-count-published.jpg"] = new PamphletMeta { Arc = "count.sheet" },
        };

        private void LoadPamphletManifest()
        {
            try { _pamphletMeta = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, PamphletMeta>>($"{DataRoot}/pamphlets_meta"); }
            catch (Exception e)
            {
                PrintError($"pamphlets_meta.json failed to parse ({e.Message}) — running on defaults, FILE LEFT UNTOUCHED for repair.");
                _pamphletMeta = DefaultPamphletMeta();
                return;
            }
            if (_pamphletMeta == null) _pamphletMeta = new Dictionary<string, PamphletMeta>();
            var added = 0;
            foreach (var kv in DefaultPamphletMeta())
                if (!_pamphletMeta.ContainsKey(kv.Key)) { _pamphletMeta[kv.Key] = kv.Value; added++; }
            if (added > 0)
            {
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/pamphlets_meta", _pamphletMeta);
                DLog($"Added {added} pamphlet manifest entr(ies) to pamphlets_meta.json.");
            }
        }

        // A print is in the loot rotation unless its OWN manifest entry names
        // an arc this wipe didn't roll. The "*" entry never gates rotation —
        // the propaganda keeps circulating whatever the story is.
        private bool PamphletInRotation(PamphletPrint p)
        {
            PamphletMeta meta;
            if (!_pamphletMeta.TryGetValue(p.File, out meta) || meta == null) return true;
            return ArcActive(meta.Arc);
        }

        // Admin: which prints circulate THIS wipe, and why the rest don't
        // (H.5 — beat-gated prints made rotation state worth being able to
        // see without spawning crates).
        [ConsoleCommand("rq.pamphlet.status")]
        private void CmdPamphletStatus(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (_pamphletCache == null || _pamphletCache.Count == 0)
            { arg.ReplyWith($"Press empty — no JPGs in oxide/data/{DataRoot}/pamphlets."); return; }
            var sb = new StringBuilder();
            var inRot = 0;
            sb.Append($"Pamphlet press: {_pamphletCache.Count} print(s), loot chance {_config.PamphletLootChance:0.###}\n");
            foreach (var p in _pamphletCache)
            {
                PamphletMeta meta;
                var hasOwn = _pamphletMeta.TryGetValue(p.File, out meta) && meta != null;
                var rot = PamphletInRotation(p);
                if (rot) inRot++;
                sb.Append($"  {p.File}: {(rot ? "IN ROTATION" : $"gated out (arc '{meta?.Arc}' not active)")}");
                if (hasOwn && !string.IsNullOrEmpty(meta.GrantsQuest)) sb.Append($", grants '{meta.GrantsQuest}'");
                sb.Append('\n');
            }
            sb.Append($"{inRot} of {_pamphletCache.Count} in rotation this wipe.");
            arg.ReplyWith(sb.ToString());
        }

        private void LoadPamphletCache()
        {
            _pamphletCache = new List<PamphletPrint>();
            try
            {
                var dir = System.IO.Path.Combine(Interface.Oxide.DataDirectory, DataRoot, "pamphlets");
                if (!System.IO.Directory.Exists(dir)) return;
                foreach (var f in System.IO.Directory.GetFiles(dir, "*.jpg"))
                {
                    var bytes = System.IO.File.ReadAllBytes(f);
                    if (bytes.Length > 0 && bytes.Length <= 524288) // 512KB — 2x renders, same as FakeFriends v0.15.1
                        _pamphletCache.Add(new PamphletPrint { File = System.IO.Path.GetFileName(f), Bytes = bytes });
                    if (_pamphletCache.Count >= 64) break;
                }
                if (_pamphletCache.Count > 0) DLog($"Pamphlet press loaded: {_pamphletCache.Count} print(s).");
            }
            catch (Exception e) { PrintWarning($"Pamphlet cache unavailable: {e.Message}"); }
        }

        // Called before PopulateLoot fills the crate (verified live 2026-08-08),
        // so the pamphlet is added a tick later, on top of vanilla loot. Only
        // subscribed while the cache is non-empty and the chance is > 0.
        private void OnLootSpawn(LootContainer container)
        {
            if (container == null) return;
            if (!container.ShortPrefabName.StartsWith("crate", StringComparison.Ordinal)) return;
            if (UnityEngine.Random.value >= _config.PamphletLootChance) return;
            NextTick(() =>
            {
                if (container == null || container.IsDestroyed || container.inventory == null) return;
                TuckPamphletInto(container.inventory);
            });
        }

        // Set while WE are the ones moving a print — OnPamphletMoved must not
        // read our own loot-seeding as a player pickup or plant.
        private bool _tuckingPamphlet;

        private void TuckPamphletInto(ItemContainer inv)
        {
            if (_pamphletCache == null || _pamphletCache.Count == 0) return;
            // Rotation honors per-print arc gates (beat prints wait their turn).
            var pool = new List<PamphletPrint>();
            for (var i = 0; i < _pamphletCache.Count; i++)
                if (PamphletInRotation(_pamphletCache[i])) pool.Add(_pamphletCache[i]);
            if (pool.Count == 0) return;
            var item = ItemManager.CreateByName("photo", 1);
            if (item == null) return;
            item.name = "pamphlet";
            var photoEnt = ItemModAssociatedEntity<PhotoEntity>.GetAssociatedEntity(item);
            if (photoEnt != null)
                photoEnt.SetImageData(OwnerStamp, pool[UnityEngine.Random.Range(0, pool.Count)].Bytes);
            // Crates spawn with exactly as many slots as vanilla loot rolls —
            // make room rather than silently dropping the print.
            if (inv.itemList.Count >= inv.capacity) inv.capacity = inv.itemList.Count + 1;
            _tuckingPamphlet = true;
            try { if (!item.MoveToContainer(inv)) item.Remove(); }
            finally { _tuckingPamphlet = false; }
        }

        [ConsoleCommand("rq.pamphlet.give")]
        private void CmdPamphletGive(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var player = arg.Player();
            if (player == null) { arg.ReplyWith("In-game only."); return; }
            if (_pamphletCache == null || _pamphletCache.Count == 0)
            { arg.ReplyWith("No pamphlets loaded — put JPGs in oxide/data/RustQuests/pamphlets and reload."); return; }
            TuckPamphletInto(player.inventory.containerMain);
            // The tuck guard suppressed the pickup path — recruit explicitly,
            // so the test command behaves like finding one in a crate.
            var recruited = TryPamphletRecruit(player);
            arg.ReplyWith($"Pamphlet handed over ({_pamphletCache.Count} print(s) in the press){(recruited ? " — and it recruited you." : "")}.");
        }

        #endregion

        #region Quest engine

        private enum QuestState { Locked, Available, Active, Ready, Done }

        private QuestState GetQuestState(BasePlayer player, QuestDef q, PlayerProgress prog)
        {
            if (prog.Completed.Contains(q.Id)) return QuestState.Done;
            if (prog.Active.TryGetValue(q.Id, out var progress))
                return AllObjectivesMet(player, q, progress) ? QuestState.Ready : QuestState.Active;
            if (!PrereqsMet(q, prog)) return QuestState.Locked;
            return QuestState.Available;
        }

        private static bool PrereqsMet(QuestDef q, PlayerProgress prog)
        {
            if (q.RequiresQuest != null && !prog.Completed.Contains(q.RequiresQuest)) return false;
            if (q.RequiresQuests != null)
                for (var i = 0; i < q.RequiresQuests.Count; i++)
                    if (!prog.Completed.Contains(q.RequiresQuests[i])) return false;
            return true;
        }

        private bool AllObjectivesMet(BasePlayer player, QuestDef q, List<int> progress)
        {
            for (var i = 0; i < q.Objectives.Count; i++)
                if (ObjectiveProgress(player, q, i, i < progress.Count ? progress[i] : 0) < EffectiveCount(q, i, q.Objectives[i]))
                    return false;
            return true;
        }

        // Pooled turn-ins (v2 Phase E): the bible's roll wins over the
        // authored Target/Count at every read. No roll recorded (pool-less
        // template, mid-wipe before a sync) = authored values stand.
        private TurninRoll PooledRoll(QuestDef q, int objIdx)
        {
            var b = _state != null ? _state.Bible : null;
            if (b == null || b.TurninRolls.Count == 0) return null;
            TurninRoll r;
            return b.TurninRolls.TryGetValue($"{q.Id}:{objIdx}", out r) ? r : null;
        }

        private string EffectiveTarget(QuestDef q, int objIdx, ObjectiveDef o)
        {
            var r = string.IsNullOrEmpty(o.Pool) ? null : PooledRoll(q, objIdx);
            return r != null && !string.IsNullOrEmpty(r.Item) ? r.Item : o.Target;
        }

        private int EffectiveCount(QuestDef q, int objIdx, ObjectiveDef o)
        {
            var r = string.IsNullOrEmpty(o.Pool) ? null : PooledRoll(q, objIdx);
            return r != null && r.Count > 0 ? r.Count : o.Count;
        }

        // turnin objectives read the player's inventory live; everything else
        // reads the stored counter.
        private int ObjectiveProgress(BasePlayer player, QuestDef q, int objIdx, int stored)
        {
            var o = q.Objectives[objIdx];
            var count = EffectiveCount(q, objIdx, o);
            if (o.Type == "turnin")
            {
                var def = ItemManager.FindItemDefinition(EffectiveTarget(q, objIdx, o));
                if (def == null) return 0;
                return Mathf.Min(player.inventory.GetAmount(def.itemid), count);
            }
            return Mathf.Min(stored, count);
        }

        private bool TryAcceptQuest(BasePlayer player, QuestDef q)
        {
            var prog = GetProgress(player);
            if (GetQuestState(player, q, prog) != QuestState.Available) return false;

            if (q.AcceptCostScrap > 0)
            {
                var scrap = ItemManager.FindItemDefinition("scrap");
                if (player.inventory.GetAmount(scrap.itemid) < q.AcceptCostScrap)
                {
                    // No dialog to open for a faceless giver (shouldn't ship:
                    // validation warns on unpaid quests with a cost).
                    if (q.Giver == UnpaidGiverKey) PrintToChat(player, L("Dialog.NoScrap", player, q.AcceptCostScrap));
                    else OpenDialog(player, q.Giver, L("Dialog.NoScrap", player, q.AcceptCostScrap));
                    return false;
                }
                player.inventory.Take(null, scrap.itemid, q.AcceptCostScrap);
            }

            if (q.GrantOnAccept.Count > 0)
                PrintToChat(player, L("Quest.Granted", player, GiveRewards(player, q.GrantOnAccept)));

            if (q.Objectives.Count == 0)
            {
                // Pure transaction quest (the knife) — completes on the spot.
                prog.Completed.Add(q.Id);
                MarkWipeCompleted(q.Id);
                if (q.Rewards.Count > 0)
                    PrintToChat(player, L("Quest.Rewarded", player, GiveRewards(player, q.Rewards)));
                UnlockTradersFor(player, prog, q.Id); // no shipped instant quest unlocks, but keep the paths equal
                MarkProgressDirty(player);
                SaveDirtyProgress();
                DLog($"{player.displayName} completed '{q.Id}' (instant).");
                return true;
            }

            prog.Active[q.Id] = new List<int>(new int[q.Objectives.Count]);
            MarkProgressDirty(player);
            SaveDirtyProgress(); // scrap may have been taken / items granted
            PrintToChat(player, L("Quest.Accepted", player, q.Name));
            DLog($"{player.displayName} accepted '{q.Id}'.");
            RecomputeEngineHooks();
            return true;
        }

        private bool TryClaimQuest(BasePlayer player, QuestDef q)
        {
            var prog = GetProgress(player);
            if (!prog.Active.TryGetValue(q.Id, out var progress)) return false;
            if (!AllObjectivesMet(player, q, progress)) return false;

            // Take turnin items now (counts re-verified by AllObjectivesMet above).
            for (var i = 0; i < q.Objectives.Count; i++)
            {
                var o = q.Objectives[i];
                if (o.Type != "turnin") continue;
                var def = ItemManager.FindItemDefinition(EffectiveTarget(q, i, o));
                if (def != null) player.inventory.Take(null, def.itemid, EffectiveCount(q, i, o));
            }

            prog.Active.Remove(q.Id);
            prog.Completed.Add(q.Id);
            MarkWipeCompleted(q.Id);
            // Before the save: a finale claim is what opens the next trader's
            // door, and both facts must land in the same write.
            UnlockTradersFor(player, prog, q.Id);
            MarkProgressDirty(player);
            SaveDirtyProgress(); // reward granted — persist now, not in 5 minutes

            if (q.Rewards.Count > 0)
                PrintToChat(player, L("Quest.Rewarded", player, GiveRewards(player, q.Rewards)));

            // The pick's dividend (Phase G): appended at claim, the quest def
            // untouched — the finale stays one QuestDef (decision 0004.5).
            var pickedOpt = PickedOption(prog, q.Id);
            if (pickedOpt != null && pickedOpt.BonusRewards.Count > 0)
                PrintToChat(player, L("Quest.Rewarded", player, GiveRewards(player, pickedOpt.BonusRewards)));

            DLog($"{player.displayName} claimed '{q.Id}'.");
            RecomputeEngineHooks();
            return true;
        }

        // Grants a reward list; returns a human-readable summary for chat.
        private string GiveRewards(BasePlayer player, List<RewardDef> rewards)
        {
            var sb = new StringBuilder();
            foreach (var r in rewards)
            {
                var def = ItemManager.FindItemDefinition(r.Item);
                if (def == null) { PrintWarning($"Reward item '{r.Item}' unknown — skipped."); continue; }
                if (sb.Length > 0) sb.Append(", ");
                // A stocked note announces itself by TITLE — "FIELD NOTES №2
                // (RECOVERED)" in the chat line, not "Note".
                NoteContent noteContent;
                var display = !string.IsNullOrEmpty(r.NoteTag) && _noteContent.TryGetValue(r.NoteTag, out noteContent) && !string.IsNullOrEmpty(noteContent.Title)
                    ? ExpandTokens(noteContent.Title)
                    : def.displayName.english;
                sb.Append(r.Amount > 1 ? $"{display} x{r.Amount}" : display);
                if (r.Water > 0) sb.Append(" (filled)");
                GrantRewardItem(player, def, r);
            }
            return sb.ToString();
        }

        // Hands one reward entry over in STACK-LEGAL pieces. Rewards routinely
        // exceed the stack size (10 solar panels stack in 3s, 3 electric
        // furnaces don't stack at all) and a single over-stacked item is both
        // wrong and useless — deployables in an illegal stack can't be placed
        // until they're split. Each piece is filled separately, which is what
        // makes `waterjug` x4 four FULL jugs rather than one quadruple-stacked
        // empty one. Shared by quest rewards and stash loot.
        private void GrantRewardItem(BasePlayer player, ItemDefinition def, RewardDef r)
        {
            var perStack = Mathf.Max(1, def.stackable);
            var left = Mathf.Max(1, r.Amount);
            while (left > 0)
            {
                var n = Mathf.Min(left, perStack);
                left -= n;
                var item = ItemManager.Create(def, n, r.Skin);
                if (item == null) break;
                if (r.Water > 0) FillWithWater(item, r.Water);
                if (r.Attachments != null) SeatAttachments(item, r.Attachments, player);
                if (!string.IsNullOrEmpty(r.NoteTag)) StockRewardNote(item, r.NoteTag);
                if (!player.inventory.GiveItem(item))
                    item.Drop(player.GetDropPosition(), player.GetDropVelocity());
            }
        }

        // Writes a notes.json entry into a granted note item (H.5) — the
        // reward IS the lore. A missing tag degrades to a blank note with a
        // log line, same philosophy as attachments that won't seat.
        private void StockRewardNote(Item item, string tag)
        {
            NoteContent content;
            if (!_noteContent.TryGetValue(tag, out content) || content == null)
            { PrintWarning($"Reward NoteTag '{tag}' has no notes.json entry — blank note granted."); return; }
            item.text = ExpandTokens(content.Text);
            if (!string.IsNullOrEmpty(content.Title)) item.name = ExpandTokens(content.Title);
        }

        // Seats weapon mods into a freshly created weapon BEFORE it's handed
        // over. Any mod that can't seat (weapon has no mod slots, slots full,
        // incompatible type — MoveToContainer refuses all three) goes to the
        // player loose with a log line, so a bad shortname in a loot table
        // degrades to "attachment in your bag" rather than a silent loss.
        private void SeatAttachments(Item weapon, List<string> mods, BasePlayer player)
        {
            foreach (var shortname in mods)
            {
                var mod = ItemManager.CreateByName(shortname, 1);
                if (mod == null) { PrintWarning($"Attachment '{shortname}' unknown — skipped."); continue; }
                if (weapon.contents == null || !mod.MoveToContainer(weapon.contents))
                {
                    DLog($"'{shortname}' wouldn't seat in '{weapon.info.shortname}' — given loose.");
                    if (!player.inventory.GiveItem(mod))
                        mod.Drop(player.GetDropPosition(), player.GetDropVelocity());
                }
            }
        }

        // Loads water into a container item's contents (verified against the
        // live assembly: ItemModContainer.OnItemCreated builds item.contents
        // server-side, and maxStackSize is the liquid capacity). Over-filling
        // would silently discard the surplus, so clamp and say so in the log.
        private void FillWithWater(Item item, int amount)
        {
            if (item.contents == null)
            { PrintWarning($"Reward item '{item.info.shortname}' holds no liquid — Water {amount} ignored."); return; }
            var water = ItemManager.FindItemDefinition("water");
            if (water == null) return;
            var mod = item.info.GetComponent<ItemModContainer>();
            var cap = mod != null && mod.maxStackSize > 0 ? mod.maxStackSize : amount;
            var give = Mathf.Min(amount, cap);
            item.contents.AddItem(water, give);
            if (give < amount) DLog($"'{item.info.shortname}' holds {cap} ml — Water {amount} clamped to {give} per item.");
        }

        // --- kill tracking -------------------------------------------------
        // OnEntityDeath is subscribed ONLY while an online player has an
        // unfinished kill objective (hot-hook discipline, FakeFriends rule).

        private void RecomputeEngineHooks()
        {
            var killNeeded = false;
            var visitNeeded = false;
            var itemNeeded = false; // readnote, drop or plant — all ride OnItemAddedToContainer
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (!_progress.TryGetValue((ulong)p.userID, out var prog)) continue;
                foreach (var kv in prog.Active)
                {
                    var q = FindQuest(kv.Key);
                    if (q == null) continue;
                    for (var i = 0; i < q.Objectives.Count; i++)
                    {
                        var o = q.Objectives[i];
                        var stored = i < kv.Value.Count ? kv.Value[i] : 0;
                        if (stored >= o.Count) continue;
                        if (o.Type == "kill") killNeeded = true;
                        else if (o.Type == "visit") visitNeeded = true;
                        else if (o.Type == "readnote" || o.Type == "drop" || o.Type == "plant") itemNeeded = true;
                    }
                }
            }

            // Paper that GRANTS work must be caught for players with no quest
            // at all — a placed note or an in-rotation pamphlet with a live
            // grant keeps the item hook armed on its own (v2 Phase B).
            if (!itemNeeded) itemNeeded = AnyPaperGrantLive();

            if (killNeeded) Subscribe(nameof(OnEntityDeath));
            else Unsubscribe(nameof(OnEntityDeath));

            if (itemNeeded) Subscribe(nameof(OnItemAddedToContainer));
            else Unsubscribe(nameof(OnItemAddedToContainer));

            if (visitNeeded && _visitTimer == null) _visitTimer = timer.Every(5f, VisitSweep);
            else if (!visitNeeded) { _visitTimer?.Destroy(); _visitTimer = null; }
        }

        private bool AnyPaperGrantLive()
        {
            for (var i = 0; i < _state.Notes.Count; i++)
            {
                NoteContent c;
                if (_noteContent.TryGetValue(_state.Notes[i].Tag, out c) && c != null
                    && !string.IsNullOrEmpty(c.GrantsQuest) && ArcActive(c.Arc)) return true;
            }
            if (_pamphletCache != null && _pamphletCache.Count > 0)
                foreach (var kv in _pamphletMeta)
                    if (kv.Value != null && !string.IsNullOrEmpty(kv.Value.GrantsQuest) && ArcActive(kv.Value.Arc)) return true;
            return false;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            var killer = info?.InitiatorPlayer;
            if (killer == null || entity == null) return;
            if (killer.IsNpc || !killer.userID.IsSteamId()) return; // real players only (excludes FakeFriends bots)
            var prog = GetProgress(killer); // lazy-load: survives hot reloads mid-quest
            if (prog == null || prog.Active.Count == 0) return;

            var species = SpeciesKey(entity);
            if (string.IsNullOrEmpty(species)) return;

            foreach (var kv in prog.Active)
            {
                var q = FindQuest(kv.Key);
                if (q == null) continue;
                for (var i = 0; i < q.Objectives.Count; i++)
                {
                    var o = q.Objectives[i];
                    if (o.Type != "kill" || !TargetMatches(o, species)) continue;
                    while (kv.Value.Count <= i) kv.Value.Add(0);
                    if (kv.Value[i] >= o.Count) continue;

                    kv.Value[i]++;
                    MarkProgressDirty(killer);
                    PrintToChat(killer, L("Quest.KillProgress", killer, species, kv.Value[i], o.Count, q.Name));
                    if (AllObjectivesMet(killer, q, kv.Value))
                        PrintToChat(killer, L("Quest.Ready", killer, TraderName(q.Giver)));
                    RecomputeEngineHooks();
                    return; // one credit per kill
                }
            }
        }

        // Shared credit path for event-driven tag objectives (code entry,
        // note pickup): completes the matching objective outright.
        private void ProgressTagObjective(BasePlayer player, string type, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            var prog = GetProgress(player);
            if (prog == null || prog.Active.Count == 0) return;
            foreach (var kv in prog.Active)
            {
                var q = FindQuest(kv.Key);
                if (q == null) continue;
                for (var i = 0; i < q.Objectives.Count; i++)
                {
                    var o = q.Objectives[i];
                    if (o.Type != type || !string.Equals(o.Target, key, StringComparison.OrdinalIgnoreCase)) continue;
                    while (kv.Value.Count <= i) kv.Value.Add(0);
                    if (kv.Value[i] >= o.Count) continue;

                    kv.Value[i] = o.Count;
                    MarkProgressDirty(player);
                    if (AllObjectivesMet(player, q, kv.Value))
                    {
                        // NextTick: we're mid-enumeration of prog.Active and
                        // the claim removes from it.
                        if (q.Giver == UnpaidGiverKey) NextTick(() => TryAutoClaimUnpaid(player, q));
                        else PrintToChat(player, L("Quest.Ready", player, TraderName(q.Giver)));
                    }
                    RecomputeEngineHooks();
                    return;
                }
            }
        }

        // Animals report a clean species through Categorize() ("Tiger", "Boar"…).
        // Humanoid NPCs don't — ScientistNPC inherits BasePlayer's "player" — so
        // they're keyed off the prefab name instead.
        // Scientists exist in TWO unrelated hierarchies: the classic
        // scientistnpc_* (ScientistNPC : HumanNPC : BasePlayer, Categorize
        // "player") and the newer scientist2* (ScientistNPC2 : BaseNPC2,
        // Categorize "Scientist2"). Matching on the prefab name covers both,
        // and every variant — patrol, cargo, oilrig, heavy, excavator, arena.
        private static readonly string[] NotAKill =
            { "corpse", "plushie", "youtooz", "sentry", "turret", "ragdoll", "deployed" };

        private static string SpeciesKey(BaseCombatEntity entity)
        {
            var bp = entity as BasePlayer;
            if (bp != null && !bp.IsNpc) return null; // real players are never quest targets

            var prefab = entity.ShortPrefabName ?? "";
            if (prefab.Length > 0)
            {
                var excluded = false;
                for (var i = 0; i < NotAKill.Length; i++)
                    if (prefab.IndexOf(NotAKill[i], StringComparison.OrdinalIgnoreCase) >= 0) { excluded = true; break; }
                if (!excluded)
                {
                    // Turret variants are auto-turrets wearing a scientist name.
                    if (prefab.IndexOf("scientist", StringComparison.OrdinalIgnoreCase) >= 0) return "Scientist";
                    if (prefab.IndexOf("dweller", StringComparison.OrdinalIgnoreCase) >= 0) return "TunnelDweller";
                    if (prefab.IndexOf("bandit", StringComparison.OrdinalIgnoreCase) >= 0) return "BanditGuard";
                    if (prefab.IndexOf("murderer", StringComparison.OrdinalIgnoreCase) >= 0) return "Murderer";
                    if (prefab.IndexOf("bradley", StringComparison.OrdinalIgnoreCase) >= 0) return "Bradley";
                    if (prefab.IndexOf("patrolhelicopter", StringComparison.OrdinalIgnoreCase) >= 0) return "PatrolHelicopter";
                }
            }
            return bp != null ? null : entity.Categorize();
        }

        private static bool TargetMatches(ObjectiveDef o, string species)
        {
            var targets = o.TargetsCached;
            if (targets == null) return string.Equals(o.Target, species, StringComparison.OrdinalIgnoreCase);
            for (var i = 0; i < targets.Length; i++)
                if (string.Equals(targets[i], species, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // --- visit objectives (no v1 content; runs only when one is active) --

        private void VisitSweep()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (!_progress.TryGetValue((ulong)player.userID, out var prog) || prog.Active.Count == 0) continue;
                foreach (var kv in prog.Active)
                {
                    var q = FindQuest(kv.Key);
                    if (q == null) continue;
                    for (var i = 0; i < q.Objectives.Count; i++)
                    {
                        var o = q.Objectives[i];
                        if (o.Type != "visit") continue;
                        while (kv.Value.Count <= i) kv.Value.Add(0);
                        if (kv.Value[i] >= o.Count) continue;
                        if (!TryParseVec(o.Target, out var pos)) continue;
                        if (Vector3.Distance(player.transform.position, pos) > o.Radius) continue;

                        kv.Value[i] = o.Count;
                        MarkProgressDirty(player);
                        if (AllObjectivesMet(player, q, kv.Value))
                            PrintToChat(player, L("Quest.Ready", player, TraderName(q.Giver)));
                        RecomputeEngineHooks();
                    }
                }
            }
        }

        private static bool TryParseVec(string s, out Vector3 pos)
        {
            pos = default(Vector3);
            if (string.IsNullOrEmpty(s)) return false;
            var parts = s.Split(' ');
            if (parts.Length != 3) return false;
            if (!float.TryParse(parts[0], out var x) || !float.TryParse(parts[1], out var y) || !float.TryParse(parts[2], out var z)) return false;
            pos = new Vector3(x, y, z);
            return true;
        }

        #endregion

        #region Interaction (conversation intercept -> dialog CUI)

        // Cancels the vanilla conversation on OUR trader and opens the quest
        // dialog instead (docs/DIAGNOSTICS.md §1). Other NPCTalking NPCs
        // (bandit camp vendors etc.) are untouched.
        private object OnNpcConversationStart(NPCTalking npc, BasePlayer player, ConversationData data)
        {
            if (npc == null || player == null) return null;
            string key = null;
            foreach (var kv in _state.Shops)
                if (ReferenceEquals(kv.Value.Trader, npc)) { key = kv.Key; break; }
            if (key == null) return null; // not one of ours — leave vanilla alone
            OpenGreeting(player, key);
            return true;
        }

        // The caboose cashier cage blocks the client's interact raycast, so the
        // vanilla conversation path never fires through it. This catches the E
        // press by proximity + facing instead — no line-of-sight needed. Cheap:
        // bitmask early-out on every frame that isn't a fresh USE press.
        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            // Per-player, per-frame hook: cheapest possible gate first. The USE
            // bitmask test rejects ~every frame before anything else runs.
            if (!input.WasJustPressed(BUTTON.USE)) return;
            if (_state.Shops.Count == 0) return;
            if (_dialogOpen.Contains((ulong)player.userID)) return;
            // Is the aim ray actually ON a trader? Perpendicular distance from
            // her chest to the ray decides — this works through the cashier cage
            // (whose colliders are child entities, so identity checks fail) and
            // still leaves the flanking slot machines to vanilla: aiming at one
            // of those puts her well off-axis.
            var eye = player.eyes.position;
            var dir = player.eyes.HeadForward();
            foreach (var kv in _state.Shops)
            {
                var t = kv.Value.Trader;
                if (t == null || t.IsDestroyed) continue;
                var chest = t.transform.position + Vector3.up * 1.1f;
                var along = Vector3.Dot(chest - eye, dir);
                if (along <= 0f || along > 4.5f) continue;
                if (Vector3.Distance(chest, eye + dir * along) > 0.75f) continue;
                OpenGreeting(player, kv.Key);
                return;
            }
        }

        private const string UiPanel = "RustQuests.Dialog";
        private const string JournalPanel = "RustQuests.Journal";

        // The trader's chain is linear: the dialog always shows exactly one
        // "current" quest — the first def (file order, active tree) the player
        // hasn't completed.
        // Prerequisites decide the order, not file order — merged-in content is
        // appended to quests.json, so ordering by position would silently place
        // a new mid-chain quest after the finale.
        // `blocked` is the first quest of hers the player can't reach yet — the
        // difference between "I have nothing for you" and "I have something you
        // haven't earned the right to" (Sonia's vault, waiting on three chains).
        private QuestDef CurrentChainQuest(BasePlayer player, PlayerProgress prog, string traderKey, out QuestDef blocked)
        {
            blocked = null;
            foreach (var q in _quests)
            {
                if (q.Giver != traderKey || !QuestActiveThisWipe(q)) continue;
                if (prog.Completed.Contains(q.Id)) continue;
                if (prog.Active.ContainsKey(q.Id)) { blocked = null; return q; } // finish what's started
                if (PrereqsMet(q, prog)) { blocked = null; return q; }
                if (blocked == null) blocked = q;
            }
            return null; // chain done (or only prerequisite-blocked quests remain)
        }

        // Greeting for both interaction paths. A trader nobody has vouched for
        // gives the cold line, withholds her name (hence the anonymous header)
        // and offers no work — only the door, and the machines on the wall.
        private void OpenGreeting(BasePlayer player, string traderKey)
        {
            var p = Profile(traderKey);
            var ov = ActiveOverlay(traderKey); // her rolled role's recolor, if any (v2 Phase C)
            // create:false — a passer-by who never takes a quest gets no record.
            var prog = GetProgress(player, create: false);
            if (!IsTraderUnlocked(prog, traderKey))
            {
                OpenDialog(player, traderKey,
                    TraderLine(traderKey, Ov(ov?.LockedGreetingText, p?.LockedGreetingText), "Dialog.LockedGreeting", player, TraderName(traderKey)),
                    header: L("Dialog.Stranger", player));
                return;
            }
            // The verdict outranks the welcome (Phase I): barred = the
            // eviction, grieving = her one remaining line. No quest button,
            // no chat — just the door.
            GrudgeDef gd;
            var sev = GrudgeAgainst(prog, traderKey, out gd);
            if (sev >= GrudgeGrieving)
            {
                var body = sev >= GrudgeBarred
                    ? TraderLine(traderKey, p?.BarredText, "Grudge.Barred", player)
                    : (gd != null && !string.IsNullOrEmpty(gd.DialogText) ? gd.DialogText : L("Grudge.Grieving", player));
                OpenDialog(player, traderKey, ExpandTokens(body, traderKey, null, prog));
                return;
            }
            OpenDialog(player, traderKey,
                TraderLine(traderKey, Ov(ov?.GreetingText, p?.GreetingText), "Dialog.Greeting", player, TraderName(traderKey)), greeting: true);
        }

        private void ShowQuestDialog(BasePlayer player, string traderKey)
        {
            var prog = GetProgress(player);
            var p = Profile(traderKey);
            var ov = ActiveOverlay(traderKey);
            // Belt and braces: the locked path never draws a quest button, but
            // these commands are client-invokable (see DialogActor).
            if (!IsTraderUnlocked(prog, traderKey)) { OpenGreeting(player, traderKey); return; }
            // Barred/grieving: the greeting path holds the whole conversation.
            GrudgeDef gd;
            var grudge = GrudgeAgainst(prog, traderKey, out gd);
            if (grudge >= GrudgeGrieving) { OpenGreeting(player, traderKey); return; }
            QuestDef blocked;
            var q = CurrentChainQuest(player, prog, traderKey, out blocked);
            if (q == null)
            {
                // Frozen (Phase I): she still serves, but the warm closing is
                // gone — transactions-only, in her voice. A pick happens at
                // the vault claim, so by now the closing IS her whole dialog.
                if (grudge == GrudgeFrozen && !string.IsNullOrEmpty(p?.FrozenText))
                {
                    OpenDialog(player, traderKey, ExpandTokens(p.FrozenText, traderKey, null, prog));
                    return;
                }
                // Something of hers is still prerequisite-locked -> she says so
                // (and points onward); otherwise she's genuinely finished.
                var pending = Ov(ov?.ChainPendingText, p?.ChainPendingText);
                var body = blocked != null && !string.IsNullOrEmpty(pending)
                    ? pending
                    : TraderLine(traderKey, Ov(ov?.ChainDoneText, p?.ChainDoneText), "Dialog.ChainDone", player);
                OpenDialog(player, traderKey, ExpandTokens(body, traderKey));
                return;
            }

            switch (GetQuestState(player, q, prog))
            {
                case QuestState.Available:
                    // The quest NAME heads the on-screen body but is not something she SAYS —
                    // the voice hook gets the offer prose alone (found live 2026-08-14).
                    OpenDialog(player, traderKey, ExpandTokens($"{q.Name}\n\n{q.OfferText}", traderKey, q), acceptQuest: q,
                        speak: ExpandTokens(q.OfferText, traderKey, q));
                    break;
                case QuestState.Active:
                    OpenDialog(player, traderKey,
                        TraderLine(traderKey, Ov(ov?.ProgressText, p?.ProgressText), "Dialog.Progress", player, ObjectiveLines(player, q, prog)));
                    break;
                case QuestState.Ready:
                    // The late-wipe fork (Phase G): the pick comes BEFORE the
                    // claim button ever renders — and once made, the ending
                    // text she reads back is the picked option's.
                    if (ChoicePendingFor(prog, q.Id)) { OpenChoiceDialog(player, traderKey); break; }
                    var picked = PickedOption(prog, q.Id);
                    var completeText = picked != null && !string.IsNullOrEmpty(picked.EndingCompleteText) ? picked.EndingCompleteText : q.CompleteText;
                    OpenDialog(player, traderKey,
                        ExpandTokens(TraderLine(traderKey, Ov(ov?.ReadyText, p?.ReadyText), "Dialog.ReadyBody", player, completeText), traderKey, q, prog),
                        claimQuest: q);
                    break;
                case QuestState.Locked: // shouldn't happen in a linear chain; treat as no work
                default:
                    OpenDialog(player, traderKey,
                        ExpandTokens(TraderLine(traderKey, Ov(ov?.ChainDoneText, p?.ChainDoneText), "Dialog.ChainDone", player), traderKey));
                    break;
            }
        }

        private string ObjectiveLines(BasePlayer player, QuestDef q, PlayerProgress prog)
        {
            prog.Active.TryGetValue(q.Id, out var progress);
            var sb = new StringBuilder();
            for (var i = 0; i < q.Objectives.Count; i++)
            {
                var o = q.Objectives[i];
                var cur = ObjectiveProgress(player, q, i, progress != null && i < progress.Count ? progress[i] : 0);
                if (sb.Length > 0) sb.Append('\n');
                // Pass the quest (and its giver) so {grid}/{code} resolve against THIS
                // quest's stash — without them ExpandTokens falls back to the default
                // trader and every finale objective pointed at Sonia's cellar (found
                // live: Rebecca's finale story said O15 while its objective said G11).
                sb.Append(ExpandTokens(L("Quest.ObjectiveLine", player, string.IsNullOrEmpty(o.Text) ? o.Target : o.Text, cur, EffectiveCount(q, i, o)),
                    string.IsNullOrEmpty(q.Giver) ? DefaultTraderKey : q.Giver, q));
            }
            return sb.ToString();
        }

        // `header` overrides the name on the title bar — a trader who hasn't been
        // introduced isn't going to label herself for you.
        private void OpenDialog(BasePlayer player, string traderKey, string body, bool greeting = false, QuestDef acceptQuest = null, QuestDef claimQuest = null, string header = null, string speak = null)
        {
            CuiHelper.DestroyUi(player, UiPanel);
            CuiHelper.DestroyUi(player, ChatPanel); // dialog and chat panel never coexist
            var ui = new CuiElementContainer();

            ui.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.07 0.96" },
                RectTransform = { AnchorMin = "0.62 0.17", AnchorMax = "0.95 0.83" },
                CursorEnabled = true,
            }, "Overlay", UiPanel);

            ui.Add(new CuiPanel
            {
                Image = { Color = "0.55 0.42 0.20 0.9" },
                RectTransform = { AnchorMin = "0 0.91", AnchorMax = "1 1" },
            }, UiPanel, UiPanel + ".header");

            ui.Add(new CuiLabel
            {
                Text = { Text = header ?? TraderName(traderKey), FontSize = 18, Align = TextAnchor.MiddleLeft, Color = "0.95 0.92 0.85 1" },
                RectTransform = { AnchorMin = "0.05 0", AnchorMax = "1 1" },
            }, UiPanel + ".header");

            // Off-script small talk (v1.9) — only for a trader who's given her
            // name (header==null means unlocked; strangers don't chat), and
            // never once she's done with you (Phase I: barred/grieving).
            GrudgeDef chatGrudge;
            if (SlmEnabled && header == null &&
                GrudgeAgainst(GetProgress(player, create: false), traderKey, out chatGrudge) < GrudgeGrieving)
                ui.Add(new CuiButton
                {
                    Button = { Command = "rq.ui.chat", Color = "0.30 0.32 0.38 0.95" },
                    Text = { Text = L("Chat.BtnChat", player), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.9 0.9 0.85 1" },
                    RectTransform = { AnchorMin = "0.78 0.12", AnchorMax = "0.97 0.88" },
                }, UiPanel + ".header");

            // A CuiLabel clips anything past its bottom edge, and the finale
            // offers run to ~900 characters with the grid/code reveal on the
            // LAST line — at a fixed 13pt that line was the one that vanished
            // (found live: Sonia's stash location never displayed). Step the
            // font down as the body grows instead; 11pt holds ~2k chars in
            // this rect, roughly double the longest authored dialog.
            var fontSize = body.Length <= 500 ? 13 : body.Length <= 800 ? 12 : 11;
            ui.Add(new CuiLabel
            {
                Text = { Text = body, FontSize = fontSize, Align = TextAnchor.UpperLeft, Color = "0.85 0.83 0.78 1" },
                RectTransform = { AnchorMin = "0.05 0.26", AnchorMax = "0.95 0.89" },
            }, UiPanel);

            // Middle button: greeting -> quest flow; offer -> accept; ready -> claim.
            if (greeting)
                AddDialogButton(ui, "rq.ui.quests", L("Dialog.BtnQuests", player), "0.30 0.38 0.22 0.95", 0.15f, 0.24f);
            else if (acceptQuest != null)
                AddDialogButton(ui, $"rq.ui.accept {acceptQuest.Id}",
                    acceptQuest.AcceptCostScrap > 0 ? L("Dialog.BtnAcceptCost", player, acceptQuest.AcceptCostScrap) : L("Dialog.BtnAccept", player),
                    "0.30 0.38 0.22 0.95", 0.15f, 0.24f);
            else if (claimQuest != null)
                AddDialogButton(ui, $"rq.ui.claim {claimQuest.Id}", L("Dialog.BtnClaim", player), "0.45 0.38 0.15 0.95", 0.15f, 0.24f);

            AddDialogButton(ui, "rq.ui.close", L("Dialog.BtnLeave", player), "0.35 0.22 0.18 0.95", 0.04f, 0.13f);

            CuiHelper.AddUi(player, ui);
            _dialogOpen.Add((ulong)player.userID);
            _dialogTrader[(ulong)player.userID] = traderKey;

            // Voice is a separate optional plugin (RustQuestsVoice + its extension DLL) so the
            // core never gains a compile-time dependency — broadcast the resolved line instead.
            // `speak` overrides when the on-screen body carries non-spoken framing (quest titles).
            var vShop = Shop(traderKey);
            Interface.CallHook("OnRustQuestsTraderLine", player, traderKey, speak ?? body, greeting, vShop.TraderPos, vShop.TraderYaw);
        }

        // The fork panel (Phase G): same skin as OpenDialog, but the body sits
        // higher and the middle band stacks one button per option. Picking
        // routes through rq.ui.choice.pick, which acknowledges with the
        // option's ResultText and only then offers the claim.
        private void OpenChoiceDialog(BasePlayer player, string traderKey)
        {
            var c = ActiveChoice();
            if (c == null) { ShowQuestDialog(player, traderKey); return; }
            CuiHelper.DestroyUi(player, UiPanel);
            CuiHelper.DestroyUi(player, ChatPanel);
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.07 0.96" },
                RectTransform = { AnchorMin = "0.62 0.17", AnchorMax = "0.95 0.83" },
                CursorEnabled = true,
            }, "Overlay", UiPanel);
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.55 0.42 0.20 0.9" },
                RectTransform = { AnchorMin = "0 0.91", AnchorMax = "1 1" },
            }, UiPanel, UiPanel + ".header");
            ui.Add(new CuiLabel
            {
                Text = { Text = TraderName(traderKey), FontSize = 18, Align = TextAnchor.MiddleLeft, Color = "0.95 0.92 0.85 1" },
                RectTransform = { AnchorMin = "0.05 0", AnchorMax = "1 1" },
            }, UiPanel + ".header");
            var body = ExpandTokens(c.Prompt, traderKey);
            ui.Add(new CuiLabel
            {
                Text = { Text = body, FontSize = body.Length <= 500 ? 13 : 12, Align = TextAnchor.UpperLeft, Color = "0.85 0.83 0.78 1" },
                RectTransform = { AnchorMin = "0.05 0.46", AnchorMax = "0.95 0.89" },
            }, UiPanel);
            // Options stack upward from the leave button; three fit. A pick is
            // deliberate — no default, no way past without one.
            var shown = 0;
            for (var i = 0; i < c.Options.Count && shown < 3; i++)
            {
                var o = c.Options[i];
                if (o == null || string.IsNullOrEmpty(o.Id) || string.IsNullOrEmpty(o.Label)) continue;
                var yMin = 0.15f + shown * 0.105f;
                AddDialogButton(ui, $"rq.ui.choice.pick {o.Id}", o.Label, "0.45 0.38 0.15 0.95", yMin, yMin + 0.09f);
                shown++;
            }
            AddDialogButton(ui, "rq.ui.close", L("Dialog.BtnLeave", player), "0.35 0.22 0.18 0.95", 0.04f, 0.13f);
            CuiHelper.AddUi(player, ui);
            _dialogOpen.Add((ulong)player.userID);
            _dialogTrader[(ulong)player.userID] = traderKey;
        }

        private void AddDialogButton(CuiElementContainer ui, string command, string label, string color, float yMin, float yMax)
        {
            ui.Add(new CuiButton
            {
                Button = { Command = command, Color = color },
                Text = { Text = label, FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "0.9 0.9 0.85 1" },
                RectTransform = { AnchorMin = $"0.06 {yMin}", AnchorMax = $"0.94 {yMax}" },
            }, UiPanel);
        }

        private void CloseDialog(BasePlayer player)
        {
            var id = (ulong)player.userID;
            CuiHelper.DestroyUi(player, UiPanel);
            CuiHelper.DestroyUi(player, ChatPanel);
            _dialogOpen.Remove(id);
            _dialogTrader.Remove(id);
            // Leaving the counter ends a panel chat; a game-chat session rides
            // on proximity, not the dialog, and survives this.
            ChatSession s;
            if (_chatSessions.TryGetValue(id, out s) && s.PanelMode) EndChatSession(id);
        }

        // Auto-close for players who walk away mid-dialog (2s cadence, only
        // runs while a trader exists; cheap distance check on open dialogs).
        private void DialogRangeSweep()
        {
            // Self-heal: another plugin, an admin `ent kill`, or a cleanup sweep
            // can destroy a trader outright (damage protection doesn't stop
            // Kill()). Without this her quest line is unreachable until restart.
            foreach (var kv in _state.Shops)
            {
                if (!kv.Value.TraderPlaced || kv.Value.Trader != null) continue;
                DLog($"Trader '{kv.Key}' missing from the world — respawning from saved state.");
                SpawnTrader(kv.Key, kv.Value.TraderPos, kv.Value.TraderYaw);
                return; // one per sweep; the next tick catches any others
            }
            ChatSessionSweep();
            if (_dialogOpen.Count == 0 || _config.DialogCloseRange <= 0f) return;
            List<BasePlayer> toClose = null;
            foreach (var p in BasePlayer.activePlayerList)
            {
                var id = (ulong)p.userID;
                if (!_dialogOpen.Contains(id)) continue;
                string key;
                var t = _dialogTrader.TryGetValue(id, out key) ? Shop(key).Trader : null;
                if (t != null && !t.IsDestroyed &&
                    Vector3.Distance(p.transform.position, t.transform.position) <= _config.DialogCloseRange) continue;
                (toClose ?? (toClose = new List<BasePlayer>())).Add(p);
            }
            if (toClose != null) foreach (var p in toClose) CloseDialog(p);
        }

        // A dialog action is only honored when the dialog is genuinely open and
        // the player is still standing at the trader (server-side re-check —
        // the client could forge the console command).
        private readonly Dictionary<ulong, float> _uiCooldown = new Dictionary<ulong, float>();

        // Returns the player AND which trader they're talking to, or null when
        // the action isn't legitimate.
        private BasePlayer DialogActor(ConsoleSystem.Arg arg, out string traderKey)
        {
            traderKey = null;
            var player = arg.Player();
            if (player == null) return null;
            var id = (ulong)player.userID;
            if (!_dialogOpen.Contains(id) || !_dialogTrader.TryGetValue(id, out traderKey)) return null;
            var trader = Shop(traderKey).Trader;
            if (trader == null || trader.IsDestroyed) return null;
            // A dead/sleeping player could otherwise claim into a corpse
            // inventory that's discarded on respawn — quest gone, reward gone.
            if (!player.IsAlive() || player.IsSleeping()) { CloseDialog(player); return null; }
            var maxRange = Mathf.Max(_config.DialogCloseRange, 10f);
            if (Vector3.Distance(player.transform.position, trader.transform.position) > maxRange) return null;
            if (UiThrottled(id)) return null;
            return player;
        }

        // Cheap throttle shared by every client-invokable UI command — each one
        // builds and network-pushes a CUI panel.
        private bool UiThrottled(ulong id)
        {
            var now = UnityEngine.Time.realtimeSinceStartup;
            float next;
            if (_uiCooldown.TryGetValue(id, out next) && now < next) return true;
            _uiCooldown[id] = now + 0.3f;
            return false;
        }

        [ConsoleCommand("rq.ui.close")]
        private void CmdUiClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !_dialogOpen.Contains((ulong)player.userID)) return;
            CloseDialog(player);
        }

        [ConsoleCommand("rq.ui.quests")]
        private void CmdUiQuests(ConsoleSystem.Arg arg)
        {
            string key;
            var player = DialogActor(arg, out key);
            if (player == null) return;
            ShowQuestDialog(player, key);
        }

        [ConsoleCommand("rq.ui.accept")]
        private void CmdUiAccept(ConsoleSystem.Arg arg)
        {
            string key;
            var player = DialogActor(arg, out key);
            if (player == null || !arg.HasArgs()) return;
            var q = FindQuest(arg.GetString(0));
            // Only the trader who gives it may hand it out — and only once she's
            // been vouched for (the button is absent, the command is not).
            if (q == null || q.Giver != key) return;
            if (!IsTraderUnlocked(GetProgress(player), key)) return;
            if (TryAcceptQuest(player, q)) ShowQuestDialog(player, key);
        }

        [ConsoleCommand("rq.ui.claim")]
        private void CmdUiClaim(ConsoleSystem.Arg arg)
        {
            string key;
            var player = DialogActor(arg, out key);
            if (player == null || !arg.HasArgs()) return;
            var q = FindQuest(arg.GetString(0));
            if (q == null || q.Giver != key) return; // claim only at the giver
            var prog = GetProgress(player);
            if (!IsTraderUnlocked(prog, key)) return;
            // The button is absent while a pick is owed, the command is not.
            if (ChoicePendingFor(prog, q.Id)) { OpenChoiceDialog(player, key); return; }
            if (TryClaimQuest(player, q)) ShowQuestDialog(player, key);
        }

        [ConsoleCommand("rq.ui.choice.pick")]
        private void CmdUiChoicePick(ConsoleSystem.Arg arg)
        {
            string key;
            var player = DialogActor(arg, out key);
            if (player == null || !arg.HasArgs()) return;
            var c = ActiveChoice();
            if (c == null) return;
            var prog = GetProgress(player);
            if (prog.Choices.ContainsKey(c.Id)) { ShowQuestDialog(player, key); return; } // picked already — fall through to the claim flow
            var q = FindQuest(c.PromptQuestId);
            // Only at the prompt quest's giver, only with the quest actually
            // ready — the fork is a claim-time moment, not a menu.
            if (q == null || q.Giver != key || !IsTraderUnlocked(prog, key)) return;
            if (GetQuestState(player, q, prog) != QuestState.Ready) { ShowQuestDialog(player, key); return; }
            var opt = FindChoiceOption(c, arg.GetString(0));
            if (opt == null) return;
            RecordChoicePick(player, prog, c, opt);
            // Acknowledge in her voice, then the normal ready flow (now
            // carrying the picked ending) offers the claim.
            if (!string.IsNullOrEmpty(opt.ResultText))
                OpenDialog(player, key, ExpandTokens(opt.ResultText, key, q, prog), claimQuest: q);
            else
                ShowQuestDialog(player, key);
        }

        #endregion

        #region SLM trader chat (v1.9 — brain ported from FakeFriends Phase 7)

        // The whole feature keys off one config value: an empty SLMEndpoint
        // means no chat button, no hook, no webrequest — scripted dialog only.
        private bool SlmEnabled => !string.IsNullOrEmpty(_config.SLMEndpoint);

        private int _slmCallsHour; private DateTime _slmHourStart = DateTime.MinValue;
        private int _slmFailStreak; private DateTime _slmBreakUntil = DateTime.MinValue;
        private int _slmOk, _slmFall; private float _slmLastMs;

        private bool SlmBudgetOk()
        {
            if (_config.SLMHourlyBudget <= 0) return true;
            var now = DateTime.Now;
            if ((now - _slmHourStart).TotalHours >= 1.0) { _slmHourStart = now; _slmCallsHour = 0; }
            return _slmCallsHour < _config.SLMHourlyBudget;
        }

        private void SlmAsk(string system, string user, string echoName, Action<string> done)
            => SlmAsk(system, null, user, echoName, done);

        // done(null) on every failure path — disabled, breaker open, budget
        // gone, transport error, degenerate reply. Oxide webrequest callbacks
        // land on the main thread, so no marshaling is needed.
        private void SlmAsk(string system, List<KeyValuePair<string, string>> turns, string user,
                            string echoName, Action<string> done, int maxTokens = 0, int maxChars = 160)
        {
            if (!SlmEnabled || DateTime.Now < _slmBreakUntil || !SlmBudgetOk()) { done(null); return; }
            _slmCallsHour++; // a failed call still costs budget — that's the point of the ledger
            string body;
            try
            {
                var msgs = new List<Dictionary<string, string>>
                {
                    new Dictionary<string, string> { ["role"] = "system", ["content"] = system }
                };
                if (turns != null)
                    foreach (var t in turns)
                        msgs.Add(new Dictionary<string, string> { ["role"] = t.Key, ["content"] = t.Value });
                msgs.Add(new Dictionary<string, string> { ["role"] = "user", ["content"] = user });
                body = JsonConvert.SerializeObject(new Dictionary<string, object>
                {
                    ["model"] = _config.SLMModel,
                    ["stream"] = false,
                    ["max_tokens"] = maxTokens > 0 ? maxTokens : _config.SLMMaxTokens,
                    ["messages"] = msgs
                });
            }
            catch { done(null); return; }
            var t0 = Time.realtimeSinceStartup;
            webrequest.Enqueue(_config.SLMEndpoint.TrimEnd('/') + "/v1/chat/completions", body, (code, resp) =>
            {
                if (!IsLoaded) return; // unloaded mid-flight — don't touch dead state
                _slmLastMs = (Time.realtimeSinceStartup - t0) * 1000f;
                string content = null;
                if (code == 200 && !string.IsNullOrEmpty(resp))
                    try { content = (string)Newtonsoft.Json.Linq.JObject.Parse(resp)["choices"]?[0]?["message"]?["content"]; } catch { }
                var clean = SlmSanitize(content, echoName, maxChars);
                if (clean == null)
                {
                    _slmFall++;
                    if (++_slmFailStreak >= 3)
                    { _slmBreakUntil = DateTime.Now.AddMinutes(5); _slmFailStreak = 0; DLog($"[slm] 3 straight failures (last code {code}) — script-only for 5 min."); }
                }
                else { _slmFailStreak = 0; _slmOk++; }
                try { done(clean); } catch (Exception e) { DLog($"[slm] callback error: {e.Message}"); }
            }, this, Core.Libraries.RequestMethod.POST, SlmHeaders(), _config.SLMTimeoutSeconds);
        }

        private Dictionary<string, string> SlmHeaders()
        {
            var h = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
            if (!string.IsNullOrEmpty(_config.SLMApiKey))
                h["Authorization"] = "Bearer " + _config.SLMApiKey;
            return h;
        }

        // Bounds untrusted model output before it can touch chat or a CUI label.
        // Ordering matters: the < > strip is the rich-text injection guard, the
        // " -> ' swap dodges Rust's literal-\" CUI rendering bug, and a reply
        // under 2 chars counts as a failure so it feeds the breaker.
        private static string SlmSanitize(string s, string echoName, int maxChars = 160)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Replace("\\\"", "\"").Replace("\\'", "'");
            s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            s = s.Replace("**", "").Replace("`", "").Replace("*", "").Replace("#", "").Trim();
            s = s.Replace("<", "").Replace(">", "");
            // the 3B likes to offer alternatives — keep only the first
            var alt = s.IndexOf("---", StringComparison.Ordinal);
            if (alt < 0) alt = s.IndexOf("Alternative version", StringComparison.OrdinalIgnoreCase);
            if (alt < 0) alt = s.IndexOf("Option 2", StringComparison.OrdinalIgnoreCase);
            if (alt > 10) s = s.Substring(0, alt).Trim();
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0]) s = s.Substring(1, s.Length - 2).Trim();
            if (!string.IsNullOrEmpty(echoName) && s.StartsWith(echoName + ":", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(echoName.Length + 1).Trim();
            // assistant preamble ("Here's a short line for X:") — cut through the
            // early colon only when the head is meta-speak
            var colon = s.IndexOf(':');
            if (colon > 0 && colon < 70)
            {
                var head = s.Substring(0, colon);
                if (head.IndexOf("here's", StringComparison.OrdinalIgnoreCase) >= 0
                    || head.IndexOf("here is", StringComparison.OrdinalIgnoreCase) >= 0
                    || head.IndexOf("sure", StringComparison.OrdinalIgnoreCase) >= 0
                    || head.IndexOf("certainly", StringComparison.OrdinalIgnoreCase) >= 0)
                    s = s.Substring(colon + 1).Trim();
            }
            s = s.Trim('-', ' ');
            s = s.Replace('"', '\'');
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            if (s.Length > maxChars)
            {
                var cut = s.LastIndexOf(' ', maxChars - 3);
                s = s.Substring(0, cut > maxChars / 2 ? cut : maxChars - 3).TrimEnd() + "…";
            }
            return s.Length < 2 ? null : s;
        }

        private static string StripTags(string s) => string.IsNullOrEmpty(s) ? s : s.Replace("<", "").Replace(">", "");
        private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n);

        // --- sessions -----------------------------------------------------

        private const string ChatPanel = "RustQuests.Chat";

        private class ChatSession
        {
            public string TraderKey;
            public string PlayerName;
            public bool PanelMode;          // CUI text box vs listening to game chat
            public bool Busy;               // one question in flight at a time
            public DateTime Until;          // fixed window from session start
            public readonly List<KeyValuePair<string, string>> Turns = new List<KeyValuePair<string, string>>();
            public readonly List<string> Lines = new List<string>(); // rendered transcript (panel mode)
        }

        private readonly Dictionary<ulong, ChatSession> _chatSessions = new Dictionary<ulong, ChatSession>();
        private readonly Dictionary<string, DateTime> _chatCooldowns = new Dictionary<string, DateTime>(); // "id:trader" -> next session allowed

        private static string CooldownKey(ulong id, string traderKey) => id + ":" + traderKey;

        // The game-chat hook exists only while someone is actually in a
        // game-chat session (hot-hook discipline).
        private void RecomputeChatHook()
        {
            var any = false;
            foreach (var kv in _chatSessions) if (!kv.Value.PanelMode) { any = true; break; }
            if (any) Subscribe(nameof(OnPlayerChat)); else Unsubscribe(nameof(OnPlayerChat));
        }

        // Session close: cooldown + one distilled memory, but only if a real
        // exchange happened — opening the panel and leaving costs nothing.
        private void EndChatSession(ulong id)
        {
            ChatSession s;
            if (!_chatSessions.TryGetValue(id, out s)) return;
            _chatSessions.Remove(id);
            RecomputeChatHook();
            if (s.Turns.Count < 2) return;
            _chatCooldowns[CooldownKey(id, s.TraderKey)] = DateTime.Now.AddMinutes(_config.SLMSessionCooldownMinutes);
            if (_chatCooldowns.Count > 400) PruneChatCooldowns();
            DistillChatMemory(id, s);
        }

        private void PruneChatCooldowns()
        {
            List<string> dead = null;
            var now = DateTime.Now;
            foreach (var kv in _chatCooldowns)
                if (kv.Value <= now) (dead ?? (dead = new List<string>())).Add(kv.Key);
            if (dead != null) foreach (var k in dead) _chatCooldowns.Remove(k);
        }

        // Expiry + walk-away, on the existing 2s dialog sweep. Panel-mode range
        // is already handled by the dialog distance check (the panel keeps the
        // player in _dialogOpen); this covers the clock and game-chat drifters.
        private void ChatSessionSweep()
        {
            if (_chatSessions.Count == 0) return;
            List<ulong> toEnd = null;
            var now = DateTime.Now;
            foreach (var kv in _chatSessions)
            {
                var end = now > kv.Value.Until;
                if (!end && !kv.Value.PanelMode)
                {
                    var trader = Shop(kv.Value.TraderKey).Trader;
                    var pl = BasePlayer.FindByID(kv.Key);
                    end = trader == null || trader.IsDestroyed || pl == null || !pl.IsConnected
                          || Vector3.Distance(pl.transform.position, trader.transform.position) > Mathf.Max(_config.DialogCloseRange, 10f) * 1.5f;
                }
                if (end) (toEnd ?? (toEnd = new List<ulong>())).Add(kv.Key);
            }
            if (toEnd == null) return;
            foreach (var id in toEnd)
            {
                ChatSession s;
                var expired = _chatSessions.TryGetValue(id, out s) && now > s.Until;
                var pl = BasePlayer.FindByID(id);
                EndChatSession(id);
                if (pl == null || !pl.IsConnected) continue;
                if (expired) PrintToChat(pl, $"<color=#c9a86a>{TraderName(s.TraderKey)}:</color> {L("Chat.SessionOver", pl)}");
                if (_dialogOpen.Contains(id)) CloseDialog(pl); // panel-mode timeout: drop the whole UI
            }
        }

        // --- prompt -------------------------------------------------------

        // How far this player is into HER story: 0 stranger (no jobs done),
        // 1 warming (some done), 2 trusted (her chain complete), 3 family
        // (every quest in the tree done — the vault is open). Drives the
        // per-trader WarmthTiers line and how long she's allowed to ramble.
        private int WarmthTier(PlayerProgress prog, string traderKey)
        {
            if (prog == null) return 0;
            int hers = 0, hersDone = 0, all = 0, allDone = 0;
            foreach (var q in _quests)
            {
                if (!QuestActiveThisWipe(q)) continue;
                // The faction's side work never gates how SHE treats you —
                // tier 3 means the traders' tree and vault, not BACK PAY
                // (Phase B regression: unpaid quests joined the count).
                if (q.Giver == UnpaidGiverKey) continue;
                all++;
                var isDone = prog.Completed.Contains(q.Id);
                if (isDone) allDone++;
                if (q.Giver != traderKey) continue;
                // A cross-trader finale (the vault) counts toward tier 3 but
                // never toward HER chain — it hangs off whichever trader
                // fronts it (grand_finale is Sonia's), and counting it kept
                // Sonia at tier 1 until the vault opened (decided 2026-08-10:
                // her chain done = tier 2, same as the other three).
                if (q.RequiresQuests != null && q.RequiresQuests.Count > 0) continue;
                hers++;
                if (isDone) hersDone++;
            }
            if (all > 0 && allDone == all) return 3;
            if (hers > 0 && hersDone == hers) return 2;
            return hersDone > 0 ? 1 : 0;
        }

        // player/prog may be null (rq.slm.test) — then she gets no quest state.
        // The shipped v1 canon sentence — the fallback when no template
        // carries CanonFacts (a broken templates.json mid-repair).
        private const string FallbackCanonFacts =
            "True facts about the island: there are exactly four traders — Sonia the jungle hunter, Rebecca the temperate farmer and electrician, Alexa the desert mechanic, and Olivia the arctic munitions maker. The four of you were once on the same payroll, and together you buried one vault that only opens for someone all four of you have paid out. Scrap is the only currency. Monuments are patrolled by hostile scientists; wolves and bears roam the wilds. There are no other towns, factions, shops or named people — do not invent any.";

        // forceTier: rq.slm.prompt preview only — everything else lets the
        // player's real warmth decide.
        private string TraderChatPrompt(string traderKey, string playerName, PlayerProgress prog, BasePlayer player, int forceTier = -1)
        {
            var p = Profile(traderKey);
            var ov = ActiveOverlay(traderKey);
            var tpl = ActiveTemplate();
            var name = p?.Name ?? "the trader";
            var sb = new StringBuilder(1024);
            sb.Append("You are ").Append(name).Append(", a woman who runs a hidden trading post out of a train caboose on a Rust survival island. ");
            if (!string.IsNullOrEmpty(p?.Persona))
            {
                sb.Append("Persona: ").Append(p.Persona).Append(' ');
                if (!string.IsNullOrEmpty(ov?.PersonaAdd)) sb.Append(ov.PersonaAdd).Append(' ');
            }
            // Pinned true facts — data-driven since v2.2 so free chat states
            // the ROLLED story (chain order, whatever is publicly true this
            // wipe). The P7-1 finding still stands: without these a small
            // model invents lore.
            var canon = tpl != null && !string.IsNullOrEmpty(tpl.CanonFacts) ? tpl.CanonFacts : FallbackCanonFacts;
            sb.Append(ExpandTokens(canon, traderKey)).Append(' ');
            // The island's verdict (Phase G): once the FIRST pick lands, every
            // trader's free chat knows what happened — one authored line.
            var choiceDef = ActiveChoice();
            var choiceState = _state != null && _state.Bible != null ? _state.Bible.Choice : null;
            if (choiceDef != null && choiceState != null && choiceState.FirstPick != null)
            {
                var firstOpt = FindChoiceOption(choiceDef, choiceState.FirstPick);
                if (firstOpt != null && !string.IsNullOrEmpty(firstOpt.CanonLine))
                    sb.Append(ExpandTokens(firstOpt.CanonLine, traderKey)).Append(' ');
            }
            // Her rolled part in the story: a public-safe stance, plus the
            // explicit don't-knows that fence invention in. Secrets are kept
            // by omission — what she was never told, she cannot leak.
            string role = null;
            if (_state.Bible != null) _state.Bible.Roles.TryGetValue(traderKey, out role);
            if (role != null && tpl != null)
            {
                string roleCanon;
                if (tpl.RoleCanon.TryGetValue(role, out roleCanon) && !string.IsNullOrEmpty(roleCanon))
                    sb.Append(ExpandTokens(roleCanon, traderKey)).Append(' ');
                string unknowns;
                if (tpl.UnknownFacts.TryGetValue(role, out unknowns) && !string.IsNullOrEmpty(unknowns))
                    sb.Append(unknowns).Append(' ');
            }
            if (prog != null && player != null)
            {
                QuestDef blocked;
                var q = CurrentChainQuest(player, prog, traderKey, out blocked);
                if (q != null && prog.Active.ContainsKey(q.Id))
                    sb.Append("Right now ").Append(playerName).Append(" is working your job '").Append(q.Name)
                      .Append("'. Their progress: ").Append(ObjectiveLines(player, q, prog).Replace("\n", "; "))
                      .Append(". Encourage, needle or advise as fits you. ");
                else if (q != null)
                    sb.Append("You have a job waiting for ").Append(playerName).Append(" called '").Append(q.Name)
                      .Append("' that they have not taken yet — nudge them toward it. ");
                else if (blocked != null)
                    sb.Append(playerName).Append(" has cleared your whole list, but one last thing of yours stays locked until every trader has paid them out. ");
                else
                    sb.Append(playerName).Append(" has finished every job you had — they have earned your respect. ");
                List<string> mem;
                if (prog.TraderMemories != null && prog.TraderMemories.TryGetValue(traderKey, out mem) && mem.Count > 0)
                {
                    sb.Append("You remember about ").Append(playerName).Append(" from earlier talks: ");
                    for (var i = 0; i < mem.Count; i++) { if (i > 0) sb.Append("; "); sb.Append(mem[i]); }
                    sb.Append(". ");
                }
            }
            // Warmth: she loosens up as the player clears her chain — each
            // trader in her own register (traders.json WarmthTiers/SmallTalk).
            var tier = forceTier >= 0 ? Mathf.Min(forceTier, 3) : WarmthTier(prog, traderKey);
            if (p?.WarmthTiers != null && tier < p.WarmthTiers.Count && !string.IsNullOrEmpty(p.WarmthTiers[tier]))
                sb.Append("How you treat them right now: ").Append(p.WarmthTiers[tier]).Append(' ');
            // The verdict outranks warmth (Phase I). Frozen replaces the tone;
            // reserved adds a shadow; the authored grudge line says why in
            // her voice. (Barred/grieving never reach a chat session — this
            // covers frozen, reserved, and rq.slm.test previews.)
            GrudgeDef grudge;
            var grudgeSev = GrudgeAgainst(prog, traderKey, out grudge);
            if (grudgeSev == GrudgeFrozen)
                sb.Append("Regardless of everything above: what this person chose at the vault cost you dearly. You are strictly transactional with them now — civil, brief, no warmth, no stories, no small talk — and you never explain why. ");
            if (grudge != null && !string.IsNullOrEmpty(grudge.Line))
            {
                if (grudgeSev == GrudgeReserved)
                    sb.Append("One shadow on your regard for them, never spoken of directly: ");
                sb.Append(ExpandTokens(grudge.Line, traderKey)).Append(' ');
            }
            if (tier >= 1)
            {
                var smallTalk = p?.SmallTalk ?? "";
                if (!string.IsNullOrEmpty(ov?.SmallTalkAdd))
                    smallTalk = string.IsNullOrEmpty(smallTalk) ? ov.SmallTalkAdd : smallTalk + " " + ov.SmallTalkAdd;
                if (!string.IsNullOrEmpty(smallTalk))
                    sb.Append("Things you enjoy talking about: ").Append(smallTalk).Append(' ');
            }
            // Keep-by-omission (decision 0004.7): a secret's VALUE never
            // enters this prompt at any tier. What warmth unlocks is the
            // overlay's authored slip — a breadcrumb of controlled precision
            // for the deduction game, written by us, not derived by the model.
            if (ov != null && ov.Slips.Count > 0 && tpl != null && _state.Bible != null)
                foreach (var s in tpl.Secrets)
                {
                    if (s == null || string.IsNullOrEmpty(s.Id) || tier < s.SlipTier) continue;
                    if (!_state.Bible.Secrets.ContainsKey(s.Id)) continue;
                    string slip;
                    if (ov.Slips.TryGetValue(s.Id, out slip) && !string.IsNullOrEmpty(slip))
                        sb.Append("Something you might let slip, just once, if the moment feels right: ").Append(slip).Append(' ');
                }
            sb.Append("Never state stash codes, door codes or exact map coordinates — your job offers handle those. ");
            var words = tier >= 3 ? 45 : tier == 2 ? 40 : 30;
            sb.Append("You are talking face to face with ").Append(playerName)
              .Append(". Reply with ONE ").Append(tier >= 2 ? "in-character reply" : "short in-character line")
              .Append(", under ").Append(words)
              .Append(" words, plain text: no quotes, no newlines, no markdown, no narration, and never mention AI, models or instructions.");
            return sb.ToString();
        }

        // --- shared send path (panel input + game chat) -------------------

        private void HandleChatLine(BasePlayer player, ChatSession s, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = Trunc(StripTags(text).Trim(), 160);
            if (text.Length == 0) return;
            if (s.Busy) return; // she answers one thing at a time
            var id = (ulong)player.userID;
            s.Lines.Add($"<color=#8fa3b8>You:</color> {text}");
            s.Busy = true;
            if (s.PanelMode) OpenChatPanel(player, s); // re-render: shows the line + thinking dots
            var traderKey = s.TraderKey;
            var prog = GetProgress(player); // chatting with a trader is engagement enough to create the record
            // A warmed-up trader gets more room — 45 words is ~280 chars /
            // ~70 tokens, so the 220-char and 80-token caps would cut her
            // off mid-aside at tiers 2-3.
            var warm = WarmthTier(prog, traderKey) >= 2;
            SlmAsk(TraderChatPrompt(traderKey, s.PlayerName, prog, player), s.Turns, text, TraderName(traderKey), reply =>
            {
                ChatSession cur;
                if (!_chatSessions.TryGetValue(id, out cur) || cur != s) return; // session ended while she thought
                s.Busy = false;
                var name = TraderName(traderKey);
                if (reply != null)
                {
                    s.Turns.Add(new KeyValuePair<string, string>("user", text));
                    s.Turns.Add(new KeyValuePair<string, string>("assistant", reply));
                    while (s.Turns.Count > 20) s.Turns.RemoveAt(0); // ~10 exchanges is plenty of window
                    s.Lines.Add($"<color=#c9a86a>{name}:</color> {reply}");
                }
                else s.Lines.Add(L("Chat.Fallback", player));
                while (s.Lines.Count > 24) s.Lines.RemoveAt(0);
                var pl = BasePlayer.FindByID(id);
                if (pl == null || !pl.IsConnected) return;
                if (reply != null)
                {
                    // Same optional-voice broadcast as OpenDialog — the chat reply in her voice.
                    var vShop = Shop(traderKey);
                    Interface.CallHook("OnRustQuestsTraderChat", pl, traderKey, reply, vShop.TraderPos, vShop.TraderYaw);
                }
                if (s.PanelMode) { if (_dialogOpen.Contains(id)) OpenChatPanel(pl, s); }
                else if (reply != null) PrintToChat(pl, $"<color=#c9a86a>{name}:</color> {reply}");
                else PrintToChat(pl, L("Chat.Fallback", pl));
            }, maxTokens: warm ? 110 : 0, maxChars: warm ? 320 : 220);
        }

        // Game-chat delivery (SLMChatPanel=false): she listens near her booth.
        // Subscribed only while such a session exists — see RecomputeChatHook.
        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (player == null || string.IsNullOrEmpty(message)) return null;
            if (channel != ConVar.Chat.ChatChannel.Global && channel != ConVar.Chat.ChatChannel.Local) return null;
            ChatSession s;
            if (!_chatSessions.TryGetValue((ulong)player.userID, out s) || s.PanelMode) return null;
            var trader = Shop(s.TraderKey).Trader;
            if (trader == null || trader.IsDestroyed) return null;
            if (Vector3.Distance(player.transform.position, trader.transform.position) > Mathf.Max(_config.DialogCloseRange, 10f)) return null;
            HandleChatLine(player, s, message);
            return null; // the line still shows in chat like normal speech
        }

        // --- chat CUI -----------------------------------------------------

        private void OpenChatPanel(BasePlayer player, ChatSession s)
        {
            CuiHelper.DestroyUi(player, UiPanel);
            CuiHelper.DestroyUi(player, ChatPanel);
            var ui = new CuiElementContainer();

            ui.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.07 0.96" },
                RectTransform = { AnchorMin = "0.62 0.17", AnchorMax = "0.95 0.83" },
                CursorEnabled = true,
            }, "Overlay", ChatPanel);

            ui.Add(new CuiPanel
            {
                Image = { Color = "0.30 0.32 0.38 0.9" },
                RectTransform = { AnchorMin = "0 0.91", AnchorMax = "1 1" },
            }, ChatPanel, ChatPanel + ".header");

            ui.Add(new CuiLabel
            {
                Text = { Text = TraderName(s.TraderKey), FontSize = 18, Align = TextAnchor.MiddleLeft, Color = "0.95 0.92 0.85 1" },
                RectTransform = { AnchorMin = "0.05 0", AnchorMax = "1 1" },
            }, ChatPanel + ".header");

            var sb = new StringBuilder();
            // Budget in WRAPPED lines, not stored lines: tier-2+ replies run
            // to 320 chars (~6 wrapped lines here), and when the label
            // overflows Unity truncates the NEWEST text — a fixed 12-line
            // window cut off her latest reply (found live, Rebecca,
            // 2026-08-10). Walk backwards admitting newest-first while the
            // estimate fits; the oldest clip off the top as designed.
            const int chatCharsPerLine = 50; // conservative at FontSize 12 in this rect (720p reference canvas)
            const int chatLineBudget = 16;
            var used = s.Busy ? 1 : 0;
            var first = s.Lines.Count;
            while (first > 0)
            {
                var cost = 1 + StripTags(s.Lines[first - 1]).Length / chatCharsPerLine;
                if (used + cost > chatLineBudget) break;
                used += cost;
                first--;
            }
            if (first == s.Lines.Count && first > 0) first--; // a single monster line still shows
            for (var i = first; i < s.Lines.Count; i++) { if (sb.Length > 0) sb.Append('\n'); sb.Append(s.Lines[i]); }
            if (s.Busy) { if (sb.Length > 0) sb.Append('\n'); sb.Append(L("Chat.Thinking", player)); }
            ui.Add(new CuiLabel
            {
                // LowerLeft: the newest line hugs the input strip, extra history clips off the TOP
                Text = { Text = sb.ToString(), FontSize = 12, Align = TextAnchor.LowerLeft, Color = "0.85 0.83 0.78 1" },
                RectTransform = { AnchorMin = "0.05 0.27", AnchorMax = "0.95 0.89" },
            }, ChatPanel);

            ui.Add(new CuiPanel
            {
                Image = { Color = "0.16 0.16 0.14 0.95" },
                RectTransform = { AnchorMin = "0.06 0.15", AnchorMax = "0.94 0.245" },
            }, ChatPanel, ChatPanel + ".inputbg");

            // First input field either plugin has shipped — CLAUDE.md wants
            // Enter-submit + refocus spiked live. Autofocus covers the refocus
            // half because we re-render the whole panel after every send.
            ui.Add(new CuiElement
            {
                Parent = ChatPanel + ".inputbg",
                Name = ChatPanel + ".input",
                Components =
                {
                    new CuiInputFieldComponent
                    {
                        Command = "rq.ui.chat.send",
                        FontSize = 12,
                        Align = TextAnchor.MiddleLeft,
                        Color = "0.9 0.9 0.85 1",
                        CharsLimit = 160,
                        NeedsKeyboard = true,
                        Autofocus = true,
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.03 0", AnchorMax = "0.97 1" },
                },
            });

            ui.Add(new CuiButton
            {
                Button = { Command = "rq.ui.chat.back", Color = "0.30 0.38 0.22 0.95" },
                Text = { Text = L("Dialog.BtnBack", player), FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "0.9 0.9 0.85 1" },
                RectTransform = { AnchorMin = "0.06 0.04", AnchorMax = "0.48 0.13" },
            }, ChatPanel);
            ui.Add(new CuiButton
            {
                Button = { Command = "rq.ui.close", Color = "0.35 0.22 0.18 0.95" },
                Text = { Text = L("Dialog.BtnLeave", player), FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "0.9 0.9 0.85 1" },
                RectTransform = { AnchorMin = "0.52 0.04", AnchorMax = "0.94 0.13" },
            }, ChatPanel);

            CuiHelper.AddUi(player, ui);
            _dialogOpen.Add((ulong)player.userID);
            _dialogTrader[(ulong)player.userID] = s.TraderKey;
        }

        // --- commands -----------------------------------------------------

        [ConsoleCommand("rq.ui.chat")]
        private void CmdUiChat(ConsoleSystem.Arg arg)
        {
            string key;
            var player = DialogActor(arg, out key);
            if (player == null || !SlmEnabled) return;
            var chatProg = GetProgress(player, create: false);
            if (!IsTraderUnlocked(chatProg, key)) return; // strangers don't chat
            // The button is absent for barred/grieving; the command is not.
            GrudgeDef gateGrudge;
            if (GrudgeAgainst(chatProg, key, out gateGrudge) >= GrudgeGrieving) { OpenGreeting(player, key); return; }
            var id = (ulong)player.userID;
            ChatSession s;
            if (_chatSessions.TryGetValue(id, out s) && s.TraderKey != key) { EndChatSession(id); s = null; }
            if (s == null && !_chatSessions.TryGetValue(id, out s))
            {
                DateTime next;
                if (_chatCooldowns.TryGetValue(CooldownKey(id, key), out next) && next > DateTime.Now)
                {
                    OpenDialog(player, key, L("Chat.Busy", player)); // she's had her fill of you this hour
                    return;
                }
                s = new ChatSession
                {
                    TraderKey = key,
                    PlayerName = StripTags(player.displayName),
                    PanelMode = _config.SLMChatPanel,
                    Until = DateTime.Now.AddMinutes(_config.SLMSessionMinutes),
                };
                _chatSessions[id] = s;
                s.Lines.Add(L("Chat.Opener", player, TraderName(key)));
                s.Lines.Add(L("Chat.Hint", player, (int)_config.SLMSessionMinutes));
                RecomputeChatHook();
            }
            if (s.PanelMode) OpenChatPanel(player, s);
            else
            {
                CloseDialog(player); // safe: only ends PanelMode sessions, this one rides on proximity
                PrintToChat(player, L("Chat.Listening", player, TraderName(key), (int)_config.SLMSessionMinutes));
            }
        }

        [ConsoleCommand("rq.ui.chat.send")]
        private void CmdUiChatSend(ConsoleSystem.Arg arg)
        {
            string key;
            var player = DialogActor(arg, out key);
            if (player == null || !arg.HasArgs()) return;
            ChatSession s;
            if (!_chatSessions.TryGetValue((ulong)player.userID, out s) || s.TraderKey != key || !s.PanelMode) return;
            HandleChatLine(player, s, arg.FullString.ToString()); // FullString is a Facepunch.StringView
        }

        [ConsoleCommand("rq.ui.chat.back")]
        private void CmdUiChatBack(ConsoleSystem.Arg arg)
        {
            string key;
            var player = DialogActor(arg, out key);
            if (player == null) return;
            OpenGreeting(player, key); // session (if any) stays warm until it expires or they leave
        }

        [ConsoleCommand("rq.slm")]
        private void CmdSlmStatus(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (!SlmEnabled) { arg.ReplyWith("SLM disabled (SLMEndpoint empty) — traders are script-only."); return; }
            var breaker = DateTime.Now < _slmBreakUntil ? $"OPEN for {(int)(_slmBreakUntil - DateTime.Now).TotalSeconds}s" : "closed";
            var budget = _config.SLMHourlyBudget <= 0 ? "unlimited" : $"{_slmCallsHour}/{_config.SLMHourlyBudget}";
            arg.ReplyWith($"SLM: {_config.SLMEndpoint} model={_config.SLMModel} ok={_slmOk} fallbacks={_slmFall} lastMs={_slmLastMs:F0} budget={budget} breaker={breaker} sessions={_chatSessions.Count} panel={_config.SLMChatPanel}");
        }

        // Round-trip a persona line without needing a player at the counter.
        // The Phase C test surface: dump the composed system prompt without
        // spending an SLM call — canon, role stance, unknowns, warmth line and
        // any slips, exactly as the model would see them. Optional tier
        // override previews what higher warmth would reveal.
        [ConsoleCommand("rq.slm.prompt")]
        private void CmdSlmPrompt(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (!arg.HasArgs()) { arg.ReplyWith($"usage: rq.slm.prompt <trader> [tier 0-3]  (traders: {string.Join(", ", _traders.Keys)})"); return; }
            var key = arg.GetString(0);
            if (Profile(key) == null) { arg.ReplyWith($"No trader '{key}'."); return; }
            var tier = arg.HasArgs(2) ? arg.GetInt(1) : -1;
            var player = arg.Player(); // in-game: your real progress colors the preview; RCON: a blank slate
            var prog = player != null ? GetProgress(player, create: false) : null;
            arg.ReplyWith($"--- system prompt for '{key}'{(tier >= 0 ? $" at forced tier {tier}" : "")} ---\n" +
                          TraderChatPrompt(key, player != null ? player.displayName : "Tester", prog, player, tier));
        }

        [ConsoleCommand("rq.slm.test")]
        private void CmdSlmTest(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (!SlmEnabled) { arg.ReplyWith("SLM disabled (SLMEndpoint empty)."); return; }
            if (!arg.HasArgs(2)) { arg.ReplyWith("Usage: rq.slm.test <trader> <message...>"); return; }
            var key = arg.GetString(0).ToLowerInvariant();
            if (Profile(key) == null) { arg.ReplyWith($"No trader '{key}'."); return; }
            var msg = arg.FullString.ToString().Substring(arg.GetString(0).Length).Trim();
            var t0 = Time.realtimeSinceStartup;
            SlmAsk(TraderChatPrompt(key, "a passing scavenger", null, null), msg, TraderName(key), reply =>
                Puts($"[slm.test] {TraderName(key)}: {(reply ?? "(null — fallback fired)")} ({(Time.realtimeSinceStartup - t0) * 1000f:F0} ms)"));
        }

        // --- memory -------------------------------------------------------

        // One SLM call to distill the session into a single remembered
        // sentence (conclusions, not transcripts — FakeFriends decision). The
        // player may already be gone: their progress was flushed on disconnect
        // BEFORE this callback lands, so the offline branch re-reads the file
        // and writes it again itself.
        private void DistillChatMemory(ulong id, ChatSession s)
        {
            var traderKey = s.TraderKey;
            var name = TraderName(traderKey);
            var sys = $"You are {name}, a trader on a Rust survival island. The chat below is a conversation you just had with {s.PlayerName}, a scavenger who runs jobs for you. State in ONE plain-text sentence, under 20 words, the most important thing you learned about {s.PlayerName}. No quotes, no preamble.";
            SlmAsk(sys, s.Turns, "Summarize now.", null, memory =>
            {
                if (string.IsNullOrEmpty(memory)) return;
                PlayerProgress prog;
                var online = _progress.TryGetValue(id, out prog);
                if (!online)
                {
                    try
                    {
                        prog = Interface.Oxide.DataFileSystem.ExistsDatafile($"{DataRoot}/players/{id}")
                            ? Interface.Oxide.DataFileSystem.ReadObject<PlayerProgress>($"{DataRoot}/players/{id}")
                            : null;
                    }
                    catch { prog = null; }
                    if (prog == null || prog.WipeId != _state.WipeId) return; // gone or wiped — let it fade
                    if (prog.TraderMemories == null) prog.TraderMemories = new Dictionary<string, List<string>>();
                }
                List<string> mem;
                if (!prog.TraderMemories.TryGetValue(traderKey, out mem)) prog.TraderMemories[traderKey] = mem = new List<string>();
                mem.Add(Trunc(memory, 120));
                while (mem.Count > 4) mem.RemoveAt(0); // the oldest impression fades
                if (online) _progressDirty.Add(id);
                else Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/players/{id}", prog);
                DLog($"[slm] {name} will remember about {s.PlayerName}: {memory}");
            }, maxTokens: 40, maxChars: 120);
        }

        #endregion

        #region Journal CUI

        [ChatCommand("quests")]
        private void CmdQuests(BasePlayer player, string command, string[] args) => OpenJournal(player);

        // Alias — the welcome note teaches "/quest"; both open the journal.
        [ChatCommand("quest")]
        private void CmdQuest(BasePlayer player, string command, string[] args) => OpenJournal(player);

        // Trader keys in hand-off order (Sonia → Rebecca → Alexa → Olivia),
        // derived by walking RequiresTrader so a re-wired chain re-orders the
        // journal for free. Anything outside the chain is appended at the end.
        private List<string> ChainOrder()
        {
            var order = new List<string>();
            // The bible's rolled order is authoritative when it exists.
            var bible = _state != null ? _state.Bible : null;
            if (bible != null && bible.ChainOrder != null)
                foreach (var k in bible.ChainOrder)
                    if (_traders.ContainsKey(k) && !order.Contains(k)) order.Add(k);
            if (order.Count == 0)
            {
                string head = _traders.ContainsKey(DefaultTraderKey) ? DefaultTraderKey : null;
                if (head == null)
                    foreach (var kv in _traders)
                        if (string.IsNullOrEmpty(EffectiveRequiresTrader(kv.Key))) { head = kv.Key; break; }
                for (var k = head; k != null && !order.Contains(k) && order.Count < _traders.Count; k = NextTraderKey(k))
                    order.Add(k);
            }
            foreach (var kv in _traders) if (!order.Contains(kv.Key)) order.Add(kv.Key);
            return order;
        }

        // Everything the player has actually lived through with this trader,
        // oldest first: her greeting, then each taken quest (offer prose, and
        // completion prose once claimed — tokens expanded, so a forgotten
        // stash code can be re-read), then her chain-done referral. Built
        // fresh from progress + authored content; nothing is stored for this,
        // and SLM chat sessions are deliberately NOT part of it.
        private List<string> StoryEntries(BasePlayer player, PlayerProgress prog, string traderKey)
        {
            var list = new List<string>();
            var p = Profile(traderKey);
            if (p == null || prog == null) return list;
            var ov = ActiveOverlay(traderKey); // the story re-reads in her rolled voice too
            var greet = Ov(ov?.GreetingText, p.GreetingText);
            if (!string.IsNullOrEmpty(greet))
                list.Add($"<b>{p.Name}</b>\n\n{string.Format(greet, p.Name)}");
            foreach (var q in _quests)
            {
                if (q.Giver != traderKey || !QuestActiveThisWipe(q)) continue;
                var done = prog.Completed.Contains(q.Id);
                if (!done && !prog.Active.ContainsKey(q.Id)) continue; // never taken — not their story yet
                var sb = new StringBuilder();
                sb.Append("<b>").Append(q.Name).Append("</b>");
                // The story re-reads the way it ENDED for this player — the
                // picked option's text stands in for the authored fork (G.3).
                var pickedOpt = PickedOption(prog, q.Id);
                var offerText = pickedOpt != null && !string.IsNullOrEmpty(pickedOpt.EndingOfferText) ? pickedOpt.EndingOfferText : q.OfferText;
                var completeText = pickedOpt != null && !string.IsNullOrEmpty(pickedOpt.EndingCompleteText) ? pickedOpt.EndingCompleteText : q.CompleteText;
                if (!string.IsNullOrEmpty(offerText)) sb.Append("\n\n").Append(ExpandTokens(offerText, traderKey, q, prog));
                if (done && !string.IsNullOrEmpty(completeText))
                    sb.Append("\n\n").Append(L("Journal.CompletedSep", player)).Append("\n\n").Append(ExpandTokens(completeText, traderKey, q, prog));
                else if (!done)
                    sb.Append("\n\n").Append(ObjectiveLines(player, q, prog));
                list.Add(sb.ToString());
            }
            QuestDef blocked;
            if (CurrentChainQuest(player, prog, traderKey, out blocked) == null)
            {
                var pendingOv = Ov(ov?.ChainPendingText, p.ChainPendingText);
                var closing = blocked != null && !string.IsNullOrEmpty(pendingOv) ? pendingOv : Ov(ov?.ChainDoneText, p.ChainDoneText);
                if (!string.IsNullOrEmpty(closing))
                    list.Add($"<b>{p.Name}</b>\n\n{ExpandTokens(closing, traderKey)}");
            }
            return list;
        }

        private const string TabChains = "chains";
        private const string TabStory = "story";

        private void OpenJournal(BasePlayer player, string tab = TabChains, string storyTrader = null, int storyPage = -1)
        {
            CuiHelper.DestroyUi(player, JournalPanel);
            var prog = GetProgress(player, create: false);
            var view = prog ?? new PlayerProgress(); // render-only — never stored
            var order = ChainOrder();
            var ui = new CuiElementContainer();

            ui.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.07 0.96" },
                RectTransform = { AnchorMin = "0.28 0.2", AnchorMax = "0.72 0.8" },
                CursorEnabled = true,
            }, "Overlay", JournalPanel);

            ui.Add(new CuiPanel
            {
                Image = { Color = "0.25 0.30 0.38 0.9" },
                RectTransform = { AnchorMin = "0 0.92", AnchorMax = "1 1" },
            }, JournalPanel, JournalPanel + ".header");

            ui.Add(new CuiLabel
            {
                Text = { Text = L("Journal.Title", player), FontSize = 16, Align = TextAnchor.MiddleLeft, Color = "0.95 0.92 0.85 1" },
                RectTransform = { AnchorMin = "0.04 0", AnchorMax = "1 1" },
            }, JournalPanel + ".header");

            // Tab row.
            AddJournalTab(ui, player, L("Journal.TabChains", player), $"rq.ui.journal.tab {TabChains}", tab == TabChains, 0.02f, 0.24f);
            AddJournalTab(ui, player, L("Journal.TabStory", player), $"rq.ui.journal.tab {TabStory}", tab == TabStory, 0.26f, 0.48f);

            if (tab == TabStory) BuildStoryTab(ui, player, view, order, storyTrader, storyPage);
            else BuildChainsTab(ui, player, view, order);

            ui.Add(new CuiButton
            {
                Button = { Command = "rq.ui.journal.close", Color = "0.35 0.22 0.18 0.95" },
                Text = { Text = L("Journal.Close", player), FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "0.9 0.9 0.85 1" },
                RectTransform = { AnchorMin = "0.35 0.03", AnchorMax = "0.65 0.1" },
            }, JournalPanel);

            CuiHelper.AddUi(player, ui);
        }

        private void AddJournalTab(CuiElementContainer ui, BasePlayer player, string label, string command, bool active, float xMin, float xMax)
        {
            ui.Add(new CuiButton
            {
                Button = { Command = active ? "" : command, Color = active ? "0.55 0.42 0.20 0.9" : "0.18 0.18 0.16 0.9" },
                Text = { Text = label, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = active ? "0.95 0.92 0.85 1" : "0.7 0.68 0.62 1" },
                RectTransform = { AnchorMin = $"{xMin} 0.845", AnchorMax = $"{xMax} 0.91" },
            }, JournalPanel);
        }

        // One block per trader in chain order: strangers stay anonymous, known
        // traders show jobs-done, where she is, and what's on the hook now.
        private void BuildChainsTab(CuiElementContainer ui, BasePlayer player, PlayerProgress prog, List<string> order)
        {
            var sb = new StringBuilder();
            foreach (var key in order)
            {
                var p = Profile(key);
                if (p == null) continue;
                if (sb.Length > 0) sb.Append("\n\n");
                if (!IsTraderUnlocked(prog, key))
                {
                    sb.Append($"<b>{L("Journal.Stranger", player)}</b>\n<color=#8a8578>{L("Journal.StrangerHint", player)}</color>");
                    continue;
                }
                int total = 0, done = 0;
                foreach (var q in _quests)
                {
                    if (q.Giver != key || !QuestActiveThisWipe(q)) continue;
                    total++;
                    if (prog.Completed.Contains(q.Id)) done++;
                }
                sb.Append($"<b>{p.Name}</b> — {L("Journal.ChainLine", player, done, total)}");
                var shop = Shop(key);
                if (shop.ShopPlaced) sb.Append($"  <color=#8a8578>({L("Journal.ShopAt", player, MapHelper.PositionToString(shop.ShopPos))})</color>");
                QuestDef blocked;
                var cur = CurrentChainQuest(player, prog, key, out blocked);
                if (cur != null && prog.Active.ContainsKey(cur.Id))
                {
                    sb.Append($"\n<b>{cur.Name}</b>");
                    if (AllObjectivesMet(player, cur, prog.Active[cur.Id]))
                        sb.Append($"  —  {L("Journal.Ready", player, p.Name)}");
                    else
                        sb.Append('\n').Append(ObjectiveLines(player, cur, prog));
                }
                else if (cur != null) sb.Append('\n').Append(L("Journal.WaitingJob", player, cur.Name));
                else if (blocked != null) sb.Append('\n').Append(L("Journal.VaultWaiting", player));
                else sb.Append('\n').Append(L("Journal.ChainClear", player));
            }

            // THE UNPAID (v2 Phase B) — only once they've made contact. No
            // shop line, no vouch hint: they find you, not the other way.
            var unpaidDone = 0;
            var unpaidActive = new StringBuilder();
            foreach (var q in _quests)
            {
                if (q.Giver != UnpaidGiverKey || !QuestActiveThisWipe(q)) continue;
                if (prog.Completed.Contains(q.Id)) { unpaidDone++; continue; }
                if (!prog.Active.ContainsKey(q.Id)) continue;
                unpaidActive.Append($"\n<b>{q.Name}</b>\n").Append(ObjectiveLines(player, q, prog));
            }
            if (unpaidDone > 0 || unpaidActive.Length > 0)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append($"<b>THE UNPAID</b> — <color=#8a8578>{L("Journal.UnpaidLine", player, unpaidDone)}</color>");
                sb.Append(unpaidActive);
            }

            if (sb.Length == 0) sb.Append(L("Journal.Empty", player));
            var body = sb.ToString();
            ui.Add(new CuiLabel
            {
                Text = { Text = body, FontSize = body.Length <= 700 ? 13 : body.Length <= 1100 ? 12 : 11, Align = TextAnchor.UpperLeft, Color = "0.85 0.83 0.78 1" },
                RectTransform = { AnchorMin = "0.05 0.12", AnchorMax = "0.95 0.83" },
            }, JournalPanel);
        }

        // Trader selector + one story entry per page, newest first on entry.
        private void BuildStoryTab(CuiElementContainer ui, BasePlayer player, PlayerProgress prog, List<string> order, string storyTrader, int storyPage)
        {
            // Default trader: the deepest one they've been introduced to.
            var known = new List<string>();
            foreach (var key in order) if (IsTraderUnlocked(prog, key)) known.Add(key);
            if (storyTrader == null || !known.Contains(storyTrader))
                storyTrader = known.Count > 0 ? known[known.Count - 1] : null;

            // Selector row — strangers are dark, unclickable, and nameless.
            var slots = Mathf.Max(order.Count, 1);
            var width = 0.96f / slots;
            for (var i = 0; i < order.Count; i++)
            {
                var key = order[i];
                var unlocked = known.Contains(key);
                var selected = key == storyTrader;
                ui.Add(new CuiButton
                {
                    Button = { Command = unlocked && !selected ? $"rq.ui.journal.tab {TabStory} {key}" : "", Color = selected ? "0.30 0.38 0.22 0.95" : unlocked ? "0.18 0.18 0.16 0.9" : "0.11 0.11 0.10 0.9" },
                    Text = { Text = unlocked ? TraderName(key) : "???", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = unlocked ? (selected ? "0.95 0.92 0.85 1" : "0.7 0.68 0.62 1") : "0.35 0.34 0.31 1" },
                    RectTransform = { AnchorMin = $"{0.02f + i * width} 0.77", AnchorMax = $"{0.02f + (i + 1) * width - 0.01f} 0.835" },
                }, JournalPanel);
            }

            var entries = storyTrader != null ? StoryEntries(player, prog, storyTrader) : new List<string>();
            if (entries.Count == 0)
            {
                ui.Add(new CuiLabel
                {
                    Text = { Text = L("Journal.NoStory", player), FontSize = 13, Align = TextAnchor.UpperLeft, Color = "0.85 0.83 0.78 1" },
                    RectTransform = { AnchorMin = "0.05 0.12", AnchorMax = "0.95 0.755" },
                }, JournalPanel);
                return;
            }
            if (storyPage < 0 || storyPage >= entries.Count) storyPage = entries.Count - 1; // land on the latest chapter
            var body = entries[storyPage];
            ui.Add(new CuiLabel
            {
                Text = { Text = body, FontSize = body.Length <= 500 ? 13 : body.Length <= 800 ? 12 : 11, Align = TextAnchor.UpperLeft, Color = "0.85 0.83 0.78 1" },
                RectTransform = { AnchorMin = "0.05 0.12", AnchorMax = "0.95 0.755" },
            }, JournalPanel);

            ui.Add(new CuiLabel
            {
                Text = { Text = L("Journal.PageOf", player, storyPage + 1, entries.Count), FontSize = 12, Align = TextAnchor.MiddleRight, Color = "0.7 0.68 0.62 1" },
                RectTransform = { AnchorMin = "0.6 0.845", AnchorMax = "0.96 0.91" },
            }, JournalPanel);
            if (storyPage > 0)
                ui.Add(new CuiButton
                {
                    Button = { Command = $"rq.ui.journal.tab {TabStory} {storyTrader} {storyPage - 1}", Color = "0.18 0.18 0.16 0.9" },
                    Text = { Text = L("Journal.BtnPrev", player), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.78 0.72 1" },
                    RectTransform = { AnchorMin = "0.06 0.03", AnchorMax = "0.26 0.1" },
                }, JournalPanel);
            if (storyPage < entries.Count - 1)
                ui.Add(new CuiButton
                {
                    Button = { Command = $"rq.ui.journal.tab {TabStory} {storyTrader} {storyPage + 1}", Color = "0.18 0.18 0.16 0.9" },
                    Text = { Text = L("Journal.BtnNext", player), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.78 0.72 1" },
                    RectTransform = { AnchorMin = "0.74 0.03", AnchorMax = "0.94 0.1" },
                }, JournalPanel);
        }

        // Client-invokable: tab and trader are validated (an unknown trader key
        // falls back to the default; a locked trader can't be selected because
        // BuildStoryTab re-checks IsTraderUnlocked), page is clamped.
        [ConsoleCommand("rq.ui.journal.tab")]
        private void CmdJournalTab(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || UiThrottled((ulong)player.userID)) return;
            var tab = arg.HasArgs() && arg.GetString(0) == TabStory ? TabStory : TabChains;
            var trader = arg.HasArgs(2) ? arg.GetString(1).ToLowerInvariant() : null;
            var page = arg.HasArgs(3) ? arg.GetInt(2, -1) : -1;
            OpenJournal(player, tab, trader, page);
        }

        [ConsoleCommand("rq.ui.journal.close")]
        private void CmdJournalClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null) CuiHelper.DestroyUi(player, JournalPanel);
        }

        #endregion

        #region Admin commands

        private bool Allowed(ConsoleSystem.Arg arg)
        {
            if (arg.Connection == null || arg.Connection.authLevel >= 2) return true;
            var player = arg.Player();
            if (player != null && permission.UserHasPermission(player.UserIDString, _config.AdminPermission)) return true;
            arg.ReplyWith(lang.GetMessage("Admin.NoPermission", this, player?.UserIDString));
            return false;
        }

        [ConsoleCommand("rq.status")]
        private void CmdStatus(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var sb = new StringBuilder($"RustQuests v{Version}\n");
            sb.Append($"  wipe id: {_state.WipeId}   map: {_state.MapKey}   quest tree: {_state.QuestTree}\n");
            sb.Append($"  {SeasonStatusLine()}\n");
            sb.Append($"  quests:  {_quests.Count} defs loaded, dialogs open: {_dialogOpen.Count}, progress cached: {_progress.Count}\n");
            foreach (var kv in _traders)
            {
                var key = kv.Key;
                var p = kv.Value;
                var shop = Shop(key);
                sb.Append($"  [{key}] {p.Name} ({p.Biome} biome, {p.ShopMode})\n");
                sb.Append(string.IsNullOrEmpty(EffectiveRequiresTrader(kv.Key))
                    ? "      open from the start\n"
                    : $"      unlocked by {EffectiveRequiresTrader(kv.Key)} on claim of '{EffectiveUnlockQuest(kv.Key)}'\n");
                sb.Append(shop.ShopPlaced
                    ? $"      shop at {MapHelper.PositionToString(shop.ShopPos)} {shop.ShopPos}, marker {(shop.Marker != null && !shop.Marker.IsDestroyed ? "up" : "off")}\n"
                    : "      shop not placed\n");
                sb.Append(shop.Trader != null && !shop.Trader.IsDestroyed
                    ? $"      trader alive, hp {shop.Trader.health:0}/{shop.Trader.MaxHealth():0}, face {p.FaceSeed}\n"
                    : shop.TraderPlaced ? "      trader MISSING (placed in data, not in world)\n" : "      trader not placed\n");
                var st = p.FinaleStashTag != null ? FindStash(p.FinaleStashTag) : null;
                if (p.FinaleStashTag != null)
                    sb.Append(st != null
                        ? $"      finale stash at {MapHelper.PositionToString(new Vector3(st.X, st.Y, st.Z))}, code {st.Code}, claimed by {st.ClaimedBy.Count}\n"
                        : "      finale stash NOT placed\n");
            }
            sb.Append($"  world:   {_state.Notes.Count} note(s), {_state.Stashes.Count} stash(es); god mode {_config.TraderGodMode}");
            arg.ReplyWith(sb.ToString());
        }

        [ConsoleCommand("rq.quest.list")]
        private void CmdQuestList(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var sb = new StringBuilder($"Quest definitions (active tree: {_state.QuestTree}, template: {_state.Bible?.TemplateId ?? "none"}):\n");
            foreach (var q in _quests)
            {
                var inactive = QuestActiveThisWipe(q) ? "" : QuestInTree(q) ? "  [inactive arc]" : "  [inactive tree]";
                sb.Append($"  {q.Id} ({q.Tree ?? "any"}/{q.Arc ?? "any"}) '{q.Name}' — {q.Objectives.Count} obj, {q.Rewards.Count} rewards{inactive}\n");
            }
            arg.ReplyWith(sb.ToString());
        }

        // Pulls the shipped dialog (Name/OfferText/CompleteText/objective Text)
        // back over what's in quests.json, leaving objectives, rewards, costs
        // and prerequisites exactly as tuned. The merge on load only ADDS
        // quests, so this is how revised wording reaches an existing file.
        [ConsoleCommand("rq.quest.synctext")]
        private void CmdQuestSyncText(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var only = arg.HasArgs() ? arg.GetString(0) : null; // one id, or all
            var defaults = DefaultQuests();
            var synced = 0;
            foreach (var def in defaults)
            {
                if (only != null && def.Id != only) continue;
                var live = _quests.Find(q => q.Id == def.Id && q.Tree == def.Tree && q.Arc == def.Arc);
                if (live == null) continue;
                live.Name = def.Name;
                live.OfferText = def.OfferText;
                live.CompleteText = def.CompleteText;
                // Objective wording only — counts and targets stay as tuned.
                for (var i = 0; i < live.Objectives.Count && i < def.Objectives.Count; i++)
                    live.Objectives[i].Text = def.Objectives[i].Text;
                synced++;
            }
            if (synced > 0)
            {
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/quests", _quests);
                BuildQuestIndex();
            }
            arg.ReplyWith(synced == 0
                ? $"No matching quest{(only != null ? $" '{only}'" : "")} found."
                : $"Re-synced dialog for {synced} quest(s) from the plugin's authored text. Rewards, objectives, costs and prerequisites untouched.");
        }

        // The other half of synctext: pulls shipped OBJECTIVES, REWARDS, accept
        // cost and on-accept grants over quests.json. This deliberately discards
        // live reward tuning for the quests it touches — that's the point, and
        // it's why it's a separate command from synctext. The filter takes a
        // quest id OR a trader key (all of hers), so reworking one chain can't
        // reach across and flatten another trader's tuning; no filter = all.
        // Prerequisites, giver and tree are structural and untouched.
        [ConsoleCommand("rq.quest.syncrewards")]
        private void CmdQuestSyncRewards(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var only = arg.HasArgs() ? arg.GetString(0) : null; // quest id, trader key, or all
            var byGiver = only != null && _traders.ContainsKey(only);
            var synced = 0;
            foreach (var def in DefaultQuests())
            {
                if (only != null && !(byGiver ? def.Giver == only : def.Id == only)) continue;
                var live = _quests.Find(q => q.Id == def.Id && q.Tree == def.Tree && q.Arc == def.Arc);
                if (live == null) continue;
                live.AcceptCostScrap = def.AcceptCostScrap;
                live.GrantOnAccept = def.GrantOnAccept;
                live.Objectives = def.Objectives;
                live.Rewards = def.Rewards;
                synced++;
            }
            if (synced > 0)
            {
                Interface.Oxide.DataFileSystem.WriteObject($"{DataRoot}/quests", _quests);
                BuildQuestIndex();
            }
            arg.ReplyWith(synced == 0
                ? $"No matching quest{(only != null ? $" '{only}'" : "")} found."
                : $"Re-synced objectives, rewards, cost and grants for {synced} quest(s) from the plugin. Any live tuning of those fields is gone; prerequisites and dialog untouched.");
        }

        [ConsoleCommand("rq.quest.reset")]
        private void CmdQuestReset(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (!arg.HasArgs()) { arg.ReplyWith("usage: rq.quest.reset <name-or-steamid>"); return; }
            var targetQuery = arg.GetString(0);
            var target = BasePlayer.FindAwakeOrSleeping(targetQuery);
            if (target == null) { arg.ReplyWith($"Player '{targetQuery}' not found."); return; }
            var id = (ulong)target.userID;
            _progress[id] = new PlayerProgress { WipeId = _state.WipeId };
            _progressDirty.Add(id);
            SaveDirtyProgress();
            RecomputeEngineHooks();
            arg.ReplyWith($"Quest progress reset for {target.displayName}.");
        }

        [ConsoleCommand("rq.tree")]
        private void CmdTree(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var tree = arg.HasArgs() ? arg.GetString(0) : null;
            if (tree != "jungle" && tree != "temperate")
            { arg.ReplyWith($"Current tree: {_state.QuestTree}. usage: rq.tree <jungle|temperate>"); return; }
            _state.QuestTree = tree;
            BuildQuestIndex(); // the active tree decides which defs are visible
            SaveState();
            arg.ReplyWith($"Quest tree set to {_state.QuestTree} (dev override — mid-wipe chains may straddle trees).");
        }

        [ConsoleCommand("rq.trader.spawn")]
        private void CmdTraderSpawn(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var key = arg.HasArgs() ? arg.GetString(0) : DefaultTraderKey;
            if (!_traders.ContainsKey(key)) { arg.ReplyWith(UnknownTraderReply(arg)); return; }
            var player = arg.Player();
            if (player == null) { arg.ReplyWith("In-game only: rq.trader.spawn [trader] spawns her in front of you."); return; }

            // Spawn 2m in front of the admin, facing them (talk-test ready).
            var fwd = player.eyes.BodyForward();
            fwd.y = 0f;
            fwd.Normalize();
            var pos = player.transform.position + fwd * 2f;
            var yaw = Quaternion.LookRotation(-fwd).eulerAngles.y;

            SpawnTrader(key, pos, yaw);
            var shop = Shop(key);
            if (shop.Trader == null) { arg.ReplyWith("Spawn failed — see server log."); return; }

            shop.TraderPlaced = true;
            shop.TraderX = pos.x; shop.TraderY = pos.y; shop.TraderZ = pos.z; shop.TraderYaw = yaw;
            SaveState();
            arg.ReplyWith($"Trader '{TraderName(key)}' spawned at {pos}.");
        }

        // Re-reads traders.json (kit/name/prefab/face edits) and respawns the
        // trader in place — the live iteration loop for outfit tweaking.
        [ConsoleCommand("rq.trader.reload")]
        private void CmdTraderReload(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            _traders = null;
            LoadTraders();
            LoadOverlays(); // same live-tuning loop: hand-edit overlays.json, reload, talk to her
            var sb = new StringBuilder("traders.json + overlays.json reloaded:\n");
            foreach (var kv in _traders)
            {
                var key = kv.Key;
                var p = kv.Value;
                var shop = Shop(key);
                if (shop.ShopPlaced)
                {
                    // Recompute her spot from the (possibly hand-edited) profile
                    // offsets so traders.json tweaks apply live.
                    var tpos = shop.ShopPos + Quaternion.Euler(0f, shop.ShopYawDeg, 0f) * new Vector3(p.TraderOffsetX, 0f, p.TraderOffsetZ);
                    tpos.y = shop.ShopY + shop.ShopHeightApplied + p.TraderOffsetY;
                    SpawnTrader(key, tpos, shop.ShopYawDeg + p.TraderYawOffset);
                    shop.TraderPlaced = true;
                    shop.TraderX = tpos.x; shop.TraderY = tpos.y; shop.TraderZ = tpos.z;
                    shop.TraderYaw = shop.ShopYawDeg + p.TraderYawOffset;
                }
                else if (shop.TraderPlaced) SpawnTrader(key, shop.TraderPos, shop.TraderYaw);
                sb.Append($"  [{key}] {p.Name}, face {p.FaceSeed}, offset Y {p.TraderOffsetY:0.00}, kit: [{string.Join(", ", p.Kit)}]\n");
            }
            SaveState();
            arg.ReplyWith(sb.ToString());
        }

        // Pulls the shipped DIALOG back over what's in traders.json, leaving the
        // calibrated half of the profile (face, kit, offsets, biome, shop mode,
        // stash tag, unlock wiring) exactly as tuned. The merge on load only
        // fills BLANK fields, so this is the only way revised trader wording
        // reaches a live file — same contract as rq.quest.synctext.
        [ConsoleCommand("rq.trader.synctext")]
        private void CmdTraderSyncText(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var only = arg.HasArgs() ? ResolveTraderKey(arg, 0) : null; // one trader, or all
            if (arg.HasArgs() && only == null) { arg.ReplyWith(UnknownTraderReply(arg)); return; }
            var synced = 0;
            foreach (var kv in DefaultTraders())
            {
                if (only != null && kv.Key != only) continue;
                TraderProfile live;
                if (!_traders.TryGetValue(kv.Key, out live)) continue;
                var def = kv.Value;
                live.GreetingText = def.GreetingText;
                live.LockedGreetingText = def.LockedGreetingText;
                live.ProgressText = def.ProgressText;
                live.ReadyText = def.ReadyText;
                live.ChainDoneText = def.ChainDoneText;
                live.ChainPendingText = def.ChainPendingText;
                live.LocatorHint = def.LocatorHint;
                live.Persona = def.Persona;
                live.WarmthTiers = def.WarmthTiers != null ? new List<string>(def.WarmthTiers) : new List<string>();
                live.SmallTalk = def.SmallTalk;
                live.BarredText = def.BarredText;
                live.FrozenText = def.FrozenText;
                synced++;
            }
            if (synced > 0) SaveTraders();
            arg.ReplyWith(synced == 0
                ? "No matching trader found."
                : $"Re-synced dialog for {synced} trader(s) from the plugin's authored text. Face, kit, offsets, biome, shop and unlock wiring untouched.");
        }

        // Progression override: inspect or hand out the introductions a player
        // would normally have to earn. `all` opens every door.
        [ConsoleCommand("rq.unlock")]
        private void CmdUnlock(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (!arg.HasArgs()) { arg.ReplyWith("usage: rq.unlock <name-or-steamid> [trader|all]   (no trader = show state)"); return; }
            var target = BasePlayer.FindAwakeOrSleeping(arg.GetString(0));
            if (target == null) { arg.ReplyWith($"Player '{arg.GetString(0)}' not found."); return; }
            var prog = GetProgress(target);

            if (arg.Args.Length < 2)
            {
                var sb = new StringBuilder($"{target.displayName} — trader progression:\n");
                foreach (var kv in _traders)
                    sb.Append($"  [{kv.Key}] {kv.Value.Name}: {(IsTraderUnlocked(prog, kv.Key) ? "OPEN" : "locked")}")
                      .Append(string.IsNullOrEmpty(EffectiveRequiresTrader(kv.Key))
                          ? " (open to everyone)\n"
                          : $" — needs {EffectiveRequiresTrader(kv.Key)} / {EffectiveUnlockQuest(kv.Key)}\n");
                arg.ReplyWith(sb.ToString());
                return;
            }

            var which = arg.GetString(1);
            var opened = 0;
            foreach (var kv in _traders)
            {
                if (which != "all" && kv.Key != which.ToLowerInvariant()) continue;
                if (prog.UnlockedTraders.Contains(kv.Key)) continue;
                prog.UnlockedTraders.Add(kv.Key);
                opened++;
            }
            if (opened == 0) { arg.ReplyWith($"Nothing to open — '{which}' is unknown or already unlocked for {target.displayName}."); return; }
            MarkProgressDirty(target);
            SaveDirtyProgress();
            arg.ReplyWith($"Opened {opened} trader(s) for {target.displayName}.");
            DLog($"ADMIN: unlocked {opened} trader(s) for {target.displayName} ({which}).");
        }

        // Face cycling: no args = random new face, a number = pin that seed
        // (small numbers are mapped into the steam-ID range for convenience,
        // so rq.trader.face 1 / 2 / 3 ... walks a stable, repeatable set).
        private const ulong SteamIdBase = 76561197960265728UL;

        [ConsoleCommand("rq.trader.face")]
        private void CmdTraderFace(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            ulong n;
            var hasSeedArg = arg.HasArgs() && ulong.TryParse(arg.GetString(0), out n);
            n = hasSeedArg ? ulong.Parse(arg.GetString(0)) : 0UL;
            // The seed is arg 0 when numeric, so the trader name is arg 0 or 1.
            var key = ResolveTraderKey(arg, hasSeedArg ? 1 : 0);
            if (key == null) { arg.ReplyWith(UnknownTraderReply(arg)); return; }
            var shop = Shop(key);
            if (!shop.TraderPlaced || shop.Trader == null) { arg.ReplyWith($"Spawn {TraderName(key)} first (rq.trader.spawn {key})."); return; }

            var seed = hasSeedArg
                ? (n >= SteamIdBase ? n : SteamIdBase + n)
                : SteamIdBase + (ulong)UnityEngine.Random.Range(1, int.MaxValue);

            Profile(key).FaceSeed = seed;
            SaveTraders();
            SpawnTrader(key, shop.TraderPos, shop.TraderYaw);
            arg.ReplyWith($"{TraderName(key)} face seed: {seed} (offset {seed - SteamIdBase}). Run again to cycle, 'rq.trader.face <n> [trader]' to pin — saved to traders.json either way.");
        }

        [ConsoleCommand("rq.trader.remove")]
        private void CmdTraderRemove(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var key = ResolveTraderKey(arg, 0);
            if (key == null) { arg.ReplyWith(UnknownTraderReply(arg)); return; }
            DespawnTrader(key);
            Shop(key).TraderPlaced = false;
            SaveState();
            arg.ReplyWith($"{TraderName(key)} removed.");
        }

        [ConsoleCommand("rq.trader.tp")]
        private void CmdTraderTp(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var player = arg.Player();
            if (player == null) { arg.ReplyWith("In-game only."); return; }
            var key = arg.HasArgs() ? arg.GetString(0) : DefaultTraderKey;
            if (!_traders.ContainsKey(key)) { arg.ReplyWith(UnknownTraderReply(arg)); return; }
            var t = Shop(key).Trader;
            if (t == null || t.IsDestroyed) { arg.ReplyWith($"{TraderName(key)} is not spawned."); return; }
            player.Teleport(t.transform.position + t.transform.forward * 1.5f);
            arg.ReplyWith($"Teleported to {TraderName(key)}.");
        }

        // Dev probe: is a prefab spawnable as a networked entity? Spawns it 5m
        // in front of the admin and reports the entity class (null = static
        // geometry, not plugin-spawnable). rq.dev.kill removes the last one.
        private BaseEntity _devEnt;

        // Owner-only: spawns ANY prefab and stamps it protected, so it must not
        // be reachable through the rustquests.admin permission.
        private bool OwnerOnly(ConsoleSystem.Arg arg)
        {
            if (arg.Connection == null || arg.Connection.authLevel >= 2) return true;
            arg.ReplyWith("Owner (authlevel 2) only.");
            return false;
        }

        [ConsoleCommand("rq.dev.spawn")]
        private void CmdDevSpawn(ConsoleSystem.Arg arg)
        {
            if (!OwnerOnly(arg)) return;
            var player = arg.Player();
            if (player == null || !arg.HasArgs()) { arg.ReplyWith("usage (in-game): rq.dev.spawn <prefab path>"); return; }
            var prefab = arg.GetString(0);
            var fwd = player.eyes.BodyForward(); fwd.y = 0f; fwd.Normalize();
            var pos = player.transform.position + fwd * 5f;
            var yaw = Quaternion.LookRotation(-fwd).eulerAngles.y;
            var ent = GameManager.server.CreateEntity(prefab, pos, Quaternion.Euler(0f, yaw, 0f));
            if (ent == null) { arg.ReplyWith($"NOT SPAWNABLE: '{prefab}' has no entity component (static geometry)."); return; }
            ent.EnableSaving(false);
            ent.OwnerID = OwnerStamp;
            ent.Spawn();
            _devEnt = ent;
            arg.ReplyWith($"SPAWNED: {ent.GetType().Name} ({ent.ShortPrefabName}) at {pos}. rq.dev.kill removes it.");
            DLog($"[dev] spawn probe: {prefab} -> {ent.GetType().Name}");
        }

        [ConsoleCommand("rq.dev.kill")]
        private void CmdDevKill(ConsoleSystem.Arg arg)
        {
            if (!OwnerOnly(arg)) return;
            if (_devEnt == null || _devEnt.IsDestroyed) { arg.ReplyWith("Nothing to remove."); return; }
            _devEnt.Kill();
            _devEnt = null;
            arg.ReplyWith("Dev entity removed.");
        }

        [ConsoleCommand("rq.wipe.reset")]
        private void CmdWipeReset(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            DespawnAllTraders();
            DespawnWorldObjects();
            foreach (var kv in _state.Shops)
            {
                if (kv.Value.ShopEnt != null && !kv.Value.ShopEnt.IsDestroyed) kv.Value.ShopEnt.Kill();
                if (kv.Value.Marker != null && !kv.Value.Marker.IsDestroyed) kv.Value.Marker.Kill();
            }
            var counter = _state.WipeCounter + 1; // bumping the counter is what actually invalidates player progress
            _state = FreshWipeState(counter);     // reads the outgoing state — must run before the fields below
            _state.WipeId = WipeIdFor(counter);
            _state.QuestTree = DetectQuestTree();
            try { GenerateWipeBible(true); }
            catch (Exception e)
            {
                PrintError($"Wipe bible generation failed ({e.Message}) — running four_keys with no remix.\n{e}");
                _state.Bible = FallbackBible();
            }
            ApplyBible();
            SaveState();
            _progress.Clear();
            _progressDirty.Clear();
            EnsureAllFinaleStashes();
            EnsureAllDrops();
            EnsurePlacedNotes();
            RecomputeEngineHooks();
            RecomputeWorldHooks();
            RecomputeProtectionHook();
            BeatTick();
            RecomputeBeatTimer();
            RecomputePieceTimer();
            if (_crewActive.Count > 0) EndVisit(false);
            RecomputeCrewTimer();
            // A real wipe's boot path schedules this 60s out; the dev reset
            // used to schedule nothing — shops and traders stayed gone until
            // the next reload (found live 2026-08-12). The world is already
            // up here, so place immediately.
            if (_config.AutoPlaceOnWipe) AutoPlaceMissingShops();
            arg.ReplyWith($"RustQuests state reset (dev wipe {counter}). Tree: {_state.QuestTree}, template: {_state.Bible.TemplateId}. Every player's quest progress is now invalidated; shops re-place{(_config.AutoPlaceOnWipe ? "d" : " only via rq.shop.autoplace (AutoPlaceOnWipe is off)")}; pasted shop entities are NOT removed (run rq.shop.remove first if needed).");
        }

        // Phase S drill lever: simulate a CARRIED wipe boundary on the running
        // server — the story and every progress file stand, the world is torn
        // down and re-placed as on a fresh map, monuments re-bind. Ignores the
        // gate and the cap (it's a drill); "force" is accepted for symmetry
        // but not required.
        [ConsoleCommand("rq.wipe.carry")]
        private void CmdWipeCarry(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (_state.Bible == null) { arg.ReplyWith("No wipe bible to carry — roll one first (rq.bible.reroll) or use rq.wipe.reset."); return; }
            DespawnAllTraders();
            DespawnWorldObjects();
            foreach (var kv in _state.Shops)
            {
                if (kv.Value.ShopEnt != null && !kv.Value.ShopEnt.IsDestroyed) kv.Value.ShopEnt.Kill();
                if (kv.Value.Marker != null && !kv.Value.Marker.IsDestroyed) kv.Value.Marker.Kill();
            }
            var wipeId = _state.WipeId;
            CarrySeason(_state.WipeCounter + 1);
            try { RebindMonumentsForCarry(); }
            catch (Exception e) { PrintWarning($"Monument rebind failed ({e.Message}) — bindings empty; rq.bible.reroll to recover."); }
            _carriedThisBoot = false;
            ApplyBible();
            SaveState();
            // Progress stays: WipeId is unchanged, so nothing in _progress is
            // invalid — but stash/drop ledgers in state are gone, same as a
            // real carried boot.
            try { EnsureAllFinaleStashes(); EnsureAllDrops(); EnsurePlacedNotes(); }
            catch (Exception e) { PrintWarning($"World placement after carry failed ({e.Message}) — rq.stash.reroll to retry."); }
            RecomputeEngineHooks();
            RecomputeWorldHooks();
            RecomputeProtectionHook();
            BeatTick();
            RecomputeBeatTimer();
            RecomputePieceTimer();
            RecomputeCrewTimer();
            if (_config.AutoPlaceOnWipe) AutoPlaceMissingShops();
            arg.ReplyWith($"Season CARRIED (drill): wipe {_state.WipeCounter}, carried {_state.CarriedWipes}/{_config.SeasonCarryOverMaxWipes}, wipe id unchanged '{wipeId}' (progress intact), template '{_state.Bible.TemplateId}', tree {_state.QuestTree}, {_state.Bible.Monuments.Count} monument binding(s) re-bound. " +
                          $"Shops re-place{(_config.AutoPlaceOnWipe ? "d" : " only via rq.shop.autoplace")}; stashes/drops/notes re-hidden; welcome notes hand out again. Pasted shop entities are NOT removed (rq.shop.remove first if needed).");
        }

        #endregion
    }
}

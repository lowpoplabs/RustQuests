using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using Oxide.Core;
using Oxide.Ext.RustQuestsVoice;

namespace Oxide.Plugins
{
    [Info("RustQuests Voice", "LowPopLabs", "1.3.1")]
    [Description("Optional voice for the RustQuests traders: dialog lines and SLM chat replies rendered by TTS and played from a radio in each shop. Requires Oxide.Ext.RustQuestsVoice.dll in Managed.")]
    public class RustQuestsVoice : RustPlugin
    {
        // Matches RustQuests' stamp so RemoveShopEntities' 40 m sweep cleans our radios
        // automatically whenever a shop is re-placed.
        private const ulong OwnerStamp = 1000733001UL;
        private const string DataRoot = "RustQuests";

        #region Config

        private ConfigData _config;

        private class ConfigData
        {
            [JsonProperty("TTSEndpoint (OpenAI-compatible speech endpoint; local Kokoro-FastAPI: http://localhost:8880/v1/audio/speech, OpenRouter: https://openrouter.ai/api/v1/audio/speech - EMPTY disables trader voice everywhere)")]
            public string TTSEndpoint = "";

            [JsonProperty("TTSModel (model the endpoint expects; local Kokoro-FastAPI: kokoro, OpenRouter: hexgrad/kokoro-82m - parity rule: only use models that also exist on OpenRouter so other servers reproduce the same voices without a GPU)")]
            public string TTSModel = "kokoro";

            [JsonProperty("TTSApiKey (Bearer token for hosted endpoints like OpenRouter; EMPTY sends no Authorization header - local Kokoro needs none)")]
            public string TTSApiKey = "";

            [JsonProperty("TTSTimeoutSeconds (per-render cap; a cold hosted endpoint can take a while)")]
            public float TTSTimeoutSeconds = 30f;

            [JsonProperty("TTSFormat (response_format requested upstream; wav for local Kokoro-FastAPI, pcm for OpenRouter - their speech endpoint only accepts mp3 or pcm)")]
            public string TTSFormat = "wav";

            [JsonProperty("Voices (trader key -> TTS voice id; a trader missing here stays silent)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> Voices = new Dictionary<string, string>
            {
                ["sonia"] = "af_jessica",
                ["olivia"] = "af_aoede",
                ["alexa"] = "af_river",
                ["rebecca"] = "bf_lily",
            };

            [JsonProperty("VoiceSpeed (playback rate the endpoint applies, 1.0 = natural)")]
            public float VoiceSpeed = 1f;

            [JsonProperty("LoudnessDbfs (target RMS loudness of rendered lines; -13 is conversational-hot, raise toward -10 for louder, lower toward -16 for softer)")]
            public float LoudnessDbfs = -13f;

            [JsonProperty("MaxSpokenChars (cassette-sized chunk: a longer line is split at sentence boundaries into chunks of this size and played back to back, so each fits the 30s tape and the story is never cut short)")]
            public int MaxSpokenChars = 400;

            [JsonProperty("RadioOffset (radio position relative to the trader, in her facing frame: right, up, back; the default sinks it into the counter behind her)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public float[] RadioOffset = { 0f, -0.4f, 0.5f };
        }

        protected override void LoadDefaultConfig() => _config = new ConfigData();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null) throw new Exception("config read null");
                // 1.3.0 key rename (truncate -> chunk): carry a tuned live value forward.
                var oldMax = Config["MaxSpokenChars (lines are cut at a sentence boundary near this length so they fit the 30s long-tape cap; the text on screen is never cut)"];
                if (oldMax != null) _config.MaxSpokenChars = Convert.ToInt32(oldMax);
                SaveConfig();
            }
            catch (Exception e)
            {
                PrintWarning($"Config unreadable ({e.Message}) — running on defaults, file left untouched.");
                _config = new ConfigData();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        private bool VoiceOn => !string.IsNullOrEmpty(_config.TTSEndpoint);

        #endregion

        #region State

        private class RadioRig
        {
            public DeployableBoomBox Box;
            public Cassette Cass;
            public Vector3 At;
            public Timer StopTimer;
            public string PlayingHash;
            public DateTime PlayingUntil;
            public bool Rendering;
            public List<string> Seq;      // cassette-sized chunks of the line being spoken
            public int SeqIndex;
            public int SeqGen;            // bumps on every new line — stale continuations bail
            public string SeqHash;        // hash of the FULL line, for the reopen guard
            public string QueuedText;     // one-deep: latest line requested while a render was in flight
            public Vector3 QueuedPos;
            public float QueuedYaw;
        }

        private readonly Dictionary<string, RadioRig> _rigs = new Dictionary<string, RadioRig>();
        private Dictionary<string, double> _cacheIndex; // hash -> seconds
        private bool _cacheDirty;

        // Breaker: mirrors the SLM plugin's 3-strike / 5-minute pattern.
        private int _failStreak;
        private DateTime _breakUntil = DateTime.MinValue;
        private int _rendered, _cacheHits, _failed;

        private string CacheDir => System.IO.Path.Combine(Interface.Oxide.DataDirectory, DataRoot, "voice", "cache");
        private const string CacheIndexFile = DataRoot + "/voice-cache-index";

        #endregion

        #region Lifecycle

        private void OnServerInitialized()
        {
            try { _cacheIndex = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, double>>(CacheIndexFile); }
            catch { _cacheIndex = null; }
            if (_cacheIndex == null) _cacheIndex = new Dictionary<string, double>();
            try { System.IO.Directory.CreateDirectory(CacheDir); }
            catch (Exception e) { PrintWarning($"voice cache dir unavailable: {e.Message}"); }
            if (VoiceOn) Puts($"Trader voice on: {_config.TTSEndpoint} model={_config.TTSModel} ({_cacheIndex.Count} cached line(s))");
            Puts($"RustQuests Voice v{Version} loaded - by LowPopLabs - ko-fi.com/lowpoplabs");
        }

        private void Unload()
        {
            foreach (var rig in _rigs.Values) KillRig(rig);
            _rigs.Clear();
            SaveCacheIndex(force: true);
        }

        private void SaveCacheIndex(bool force = false)
        {
            if (!_cacheDirty && !force) return;
            Interface.Oxide.DataFileSystem.WriteObject(CacheIndexFile, _cacheIndex);
            _cacheDirty = false;
        }

        #endregion

        #region RustQuests hooks

        private void OnRustQuestsTraderLine(BasePlayer player, string traderKey, string body, bool greeting, Vector3 traderPos, float traderYaw)
            => Speak(traderKey, body, traderPos, traderYaw);

        private void OnRustQuestsTraderChat(BasePlayer player, string traderKey, string reply, Vector3 traderPos, float traderYaw)
            => Speak(traderKey, reply, traderPos, traderYaw);

        #endregion

        #region Speak pipeline

        private void Speak(string traderKey, string text, Vector3 pos, float yaw)
        {
            if (!VoiceOn || pos == Vector3.zero) return;
            if (DateTime.Now < _breakUntil) return;
            string voice;
            if (!_config.Voices.TryGetValue(traderKey ?? "", out voice) || string.IsNullOrEmpty(voice)) return;

            var full = SpeakableText(text);
            if (full.Length < 2) return;

            var seqHash = LineHash(voice, full);
            RadioRig rig;
            if (!_rigs.TryGetValue(traderKey, out rig)) _rigs[traderKey] = rig = new RadioRig();

            // Reopening the same dialog while she's still saying it — don't restart her mid-word.
            if (rig.PlayingHash == seqHash && DateTime.Now < rig.PlayingUntil) return;

            if (rig.Rendering)
            {
                // One-deep queue: the latest line wins, older pending lines are stale.
                rig.QueuedText = text;
                rig.QueuedPos = pos;
                rig.QueuedYaw = yaw;
                return;
            }

            rig.SeqGen++;
            rig.Seq = SplitChunks(full);
            rig.SeqIndex = 0;
            rig.SeqHash = seqHash;
            PlayCurrentChunk(traderKey, rig, pos, yaw);
        }

        // Plays rig.Seq[rig.SeqIndex] — from cache when it's there, rendering first when not.
        // Chunks after the first arrive here from the previous chunk's stop timer.
        private void PlayCurrentChunk(string traderKey, RadioRig rig, Vector3 pos, float yaw)
        {
            if (rig.Seq == null || rig.SeqIndex >= rig.Seq.Count) { rig.Seq = null; return; }
            if (DateTime.Now < _breakUntil) { rig.Seq = null; return; }
            string voice;
            if (!_config.Voices.TryGetValue(traderKey, out voice) || string.IsNullOrEmpty(voice)) { rig.Seq = null; return; }
            var line = rig.Seq[rig.SeqIndex];
            var hash = LineHash(voice, line);

            double seconds;
            var cached = _cacheIndex.TryGetValue(hash, out seconds) ? ReadCachedOgg(hash) : null;
            if (cached != null)
            {
                _cacheHits++;
                Play(traderKey, rig, cached, seconds, pos, yaw);
                return;
            }

            var gen = rig.SeqGen;
            rig.Rendering = true;
            VoiceTts.Render(new VoiceTts.Request
            {
                Endpoint = _config.TTSEndpoint,
                ApiKey = _config.TTSApiKey,
                Model = _config.TTSModel,
                Voice = voice,
                Speed = _config.VoiceSpeed,
                Text = line,
                TargetRmsDbfs = _config.LoudnessDbfs,
                Format = _config.TTSFormat,
                TimeoutSeconds = (int)_config.TTSTimeoutSeconds,
            }, result => NextTick(() =>
            {
                if (!IsLoaded) return;
                RadioRig cur;
                if (!_rigs.TryGetValue(traderKey, out cur) || cur != rig) return;
                rig.Rendering = false;

                if (!result.Ok)
                {
                    _failed++;
                    if (++_failStreak >= 3)
                    {
                        _breakUntil = DateTime.Now.AddMinutes(5);
                        PrintWarning($"voice breaker tripped for 5 min ({result.Error})");
                    }
                    else PrintWarning($"voice render failed: {result.Error}");
                    if (rig.SeqGen == gen) rig.Seq = null; // drop the rest of this line
                }
                else
                {
                    _failStreak = 0;
                    _rendered++;
                    WriteCachedOgg(hash, result.Ogg, result.Seconds);
                    if (rig.SeqGen == gen) Play(traderKey, rig, result.Ogg, result.Seconds, pos, yaw);
                }

                // Serve the line that queued up behind this render, if any.
                var queued = rig.QueuedText;
                if (queued != null)
                {
                    rig.QueuedText = null;
                    Speak(traderKey, queued, rig.QueuedPos, rig.QueuedYaw);
                }
            }));
        }

        // The dialog body is written for the screen; her voice gets the prose only —
        // no markup, no objective bullet lists, no "12/100" counters read aloud,
        // and no stage directions spoken in her own voice.
        private string SpeakableText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = StripLeadingNarration(text);
            var sb = new StringBuilder(text.Length);
            foreach (var rawLine in text.Replace("\r", "").Split('\n'))
            {
                var l = StripTags(rawLine).Trim();
                if (l.Length == 0) continue;
                if (l.StartsWith("-") || l.StartsWith("•")) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(l);
            }
            var s = sb.ToString().Trim();
            // Sanity ceiling only (4 tapes ≈ 100s of speech) — real lines never get here.
            var totalMax = _config.MaxSpokenChars * 4;
            if (s.Length > totalMax)
            {
                var cut = s.LastIndexOfAny(SentenceEnds, totalMax - 1);
                s = cut > 40 ? s.Substring(0, cut + 1) : s.Substring(0, totalMax);
            }
            return s;
        }

        private static readonly char[] SentenceEnds = { '.', '!', '?' };

        // A line longer than one tape holds is split at sentence boundaries into
        // cassette-sized chunks the stop timer chains back to back. The first chunk
        // hashes identically to the pre-1.3.0 truncated render, so old cache entries
        // of long lines stay warm.
        private List<string> SplitChunks(string s)
        {
            var max = Math.Max(80, _config.MaxSpokenChars);
            var chunks = new List<string>();
            while (s.Length > max)
            {
                var cut = s.LastIndexOfAny(SentenceEnds, max - 1);
                if (cut <= 40) cut = s.LastIndexOf(' ', max - 1); // no sentence end — break between words
                if (cut <= 40) cut = max - 1;
                chunks.Add(s.Substring(0, cut + 1).Trim());
                s = s.Substring(cut + 1).Trim();
            }
            if (s.Length > 0) chunks.Add(s);
            return chunks;
        }

        // Some authored texts open with a third-person stage direction ("She doesn't look up
        // from the bench.", "The woman by the door doesn't step aside.") — narration for the
        // reader, not words she says. Drop the FIRST paragraph when it reads as third person
        // with no first/second-person markers; her actual speech ("I ...", "you ...") survives.
        private static readonly string[] NarrationOpeners = { "She ", "He ", "The woman", "The man", "Her ", "His " };

        private static string StripLeadingNarration(string text)
        {
            var cut = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (cut <= 0) return text;
            var first = text.Substring(0, cut).Trim();
            var opens = false;
            foreach (var o in NarrationOpeners)
                if (first.StartsWith(o, StringComparison.Ordinal)) { opens = true; break; }
            if (!opens) return text;
            if (first.Contains(" I ") || first.StartsWith("I ") || first.Contains("you") || first.Contains("You") || first.Contains("{0}"))
                return text;
            return text.Substring(cut + 2);
        }

        private static string StripTags(string s)
        {
            if (s.IndexOf('<') < 0) return s;
            var sb = new StringBuilder(s.Length);
            var depth = 0;
            foreach (var c in s)
            {
                if (c == '<') depth++;
                else if (c == '>') { if (depth > 0) depth--; }
                else if (depth == 0) sb.Append(c);
            }
            return sb.ToString();
        }

        private string LineHash(string voice, string line)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes($"{_config.TTSModel}|{voice}|{_config.VoiceSpeed:0.##}|{_config.LoudnessDbfs:0.#}|{line}"));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private byte[] ReadCachedOgg(string hash)
        {
            try
            {
                var path = System.IO.Path.Combine(CacheDir, hash + ".ogg");
                if (!System.IO.File.Exists(path)) return null;
                var bytes = System.IO.File.ReadAllBytes(path);
                return bytes.Length >= 64 ? bytes : null;
            }
            catch { return null; }
        }

        private void WriteCachedOgg(string hash, byte[] ogg, double seconds)
        {
            try
            {
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(CacheDir, hash + ".ogg"), ogg);
                _cacheIndex[hash] = seconds;
                _cacheDirty = true;
                SaveCacheIndex();
            }
            catch (Exception e) { PrintWarning($"voice cache write failed: {e.Message}"); }
        }

        #endregion

        #region Radio rig

        private void Play(string traderKey, RadioRig rig, byte[] ogg, double seconds, Vector3 pos, float yaw)
        {
            if (!EnsureRig(traderKey, rig, pos, yaw)) { rig.Seq = null; return; }

            rig.StopTimer?.Destroy();
            rig.Box.BoxController.ServerTogglePlay(false);
            FileStorage.server.RemoveAllByEntity(rig.Cass.net.ID); // one live line per radio — the DB never grows
            var crc = FileStorage.server.Store(ogg, FileStorage.Type.ogg, rig.Cass.net.ID);
            rig.Cass.SetAudioId(crc, 0UL);
            rig.PlayingHash = rig.SeqHash;
            var lastChunk = rig.Seq == null || rig.SeqIndex >= rig.Seq.Count - 1;
            // While more chunks wait, hold the reopen guard across the inter-chunk gap
            // (stop pad + a possible sub-second render) so the same dialog can't restart her.
            rig.PlayingUntil = DateTime.Now.AddSeconds(seconds + (lastChunk ? 0.8 : 5.0));

            var box = rig.Box;
            timer.Once(0.25f, () =>
            {
                if (box == null || box.IsDestroyed) return;
                box.BoxController.ServerTogglePlay(true);
            });
            // `seconds` is speech only; renders carry a trailing second of silence (extension
            // 0.2.0+), so stopping just inside the pad clips nothing and the loop point —
            // cassette playback loops! — is never audible.
            var gen = rig.SeqGen;
            rig.StopTimer = timer.Once((float)seconds + 0.45f, () =>
            {
                if (box != null && !box.IsDestroyed) box.BoxController.ServerTogglePlay(false);
                // Long line: the next cassette-sized chunk picks up where this one stopped.
                if (rig.SeqGen != gen || rig.Seq == null) return;
                rig.SeqIndex++;
                if (rig.SeqIndex < rig.Seq.Count) PlayCurrentChunk(traderKey, rig, pos, yaw);
                else rig.Seq = null;
            });
        }

        private bool EnsureRig(string traderKey, RadioRig rig, Vector3 traderPos, float yaw)
        {
            var o = _config.RadioOffset;
            var at = traderPos + Quaternion.Euler(0f, yaw, 0f) *
                new Vector3(o.Length > 0 ? o[0] : 0.4f, o.Length > 1 ? o[1] : 0f, -(o.Length > 2 ? o[2] : 0.5f));

            if (rig.Box != null && !rig.Box.IsDestroyed && rig.Cass != null && !rig.Cass.IsDestroyed &&
                (rig.At - at).sqrMagnitude < 4f)
                return true;

            KillRig(rig);

            var boomDef = ItemManager.FindItemDefinition("boombox");
            var deployMod = boomDef != null ? boomDef.GetComponent<ItemModDeployable>() : null;
            if (deployMod == null) { PrintWarning("could not resolve the boombox prefab — voice disabled for this line"); return false; }

            var ent = GameManager.server.CreateEntity(deployMod.entityPrefab.resourcePath, at, Quaternion.Euler(0f, yaw, 0f));
            var box = ent as DeployableBoomBox;
            if (box == null) { if (ent != null) ent.Kill(); return false; }
            box.EnableSaving(false); // never persists — rebuilt on demand, no orphans after a crash
            box.OwnerID = OwnerStamp;
            box.Spawn();
            using (var flags = box.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
                flags.Set(BaseEntity.Flags.Reserved8, true); // self-powered, the static-boombox way
            // Hiding tricks both fail on the client: limitNetworking = no entity = no audio,
            // and Flags.Disabled mutes it too (proven live 2026-08-14). Out-of-sight is
            // GEOMETRY's job — RadioOffset sinks the box into the counter.

            var item = ItemManager.CreateByName("cassette", 1); // the long tape: 30s cap
            var cass = item != null ? ItemModAssociatedEntity<Cassette>.GetAssociatedEntity(item) : null;
            if (cass == null)
            {
                item?.Remove();
                box.Kill();
                PrintWarning("could not create the cassette — voice disabled for this line");
                return false;
            }
            if (!item.MoveToContainer(box.inventory))
            {
                box.inventory.canAcceptItem = null;
                if (!item.MoveToContainer(box.inventory)) { item.Remove(); box.Kill(); return false; }
            }

            rig.Box = box;
            rig.Cass = cass;
            rig.At = at;
            return true;
        }

        private void KillRig(RadioRig rig)
        {
            rig.StopTimer?.Destroy();
            rig.StopTimer = null;
            if (rig.Box != null && !rig.Box.IsDestroyed)
            {
                if (rig.Box.inventory != null)
                    for (var i = rig.Box.inventory.itemList.Count - 1; i >= 0; i--)
                        rig.Box.inventory.itemList[i].Remove(); // kills the cassette entity, clears its FileStorage rows
                rig.Box.Kill();
            }
            rig.Box = null;
            rig.Cass = null;
            rig.PlayingHash = null;
            rig.Seq = null;
        }

        #endregion

        #region Admin commands

        private bool Allowed(ConsoleSystem.Arg arg)
        {
            if (arg.Connection == null || arg.Connection.authLevel >= 2) return true;
            arg.ReplyWith("No permission.");
            return false;
        }

        [ConsoleCommand("rq.voice.status")]
        private void CmdStatus(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var sb = new StringBuilder();
            sb.Append(VoiceOn
                ? $"Voice ON — {_config.TTSEndpoint} model={_config.TTSModel} speed={_config.VoiceSpeed}\n"
                : "Voice OFF (TTSEndpoint is empty)\n");
            sb.Append($"  cache: {_cacheIndex.Count} line(s) | session: {_rendered} rendered, {_cacheHits} cache hits, {_failed} failed");
            if (DateTime.Now < _breakUntil) sb.Append($" | BREAKER until {_breakUntil:HH:mm:ss}");
            sb.Append('\n');
            foreach (var kv in _config.Voices)
            {
                RadioRig rig;
                var live = _rigs.TryGetValue(kv.Key, out rig) && rig.Box != null && !rig.Box.IsDestroyed;
                sb.Append($"  {kv.Key}: {kv.Value}{(live ? " (radio up)" : "")}\n");
            }
            arg.ReplyWith(sb.ToString().TrimEnd());
        }

        // rq.voice.test <trader> [text...] — render + play at that trader's radio (or at the admin if her shop is unplaced).
        [ConsoleCommand("rq.voice.test")]
        private void CmdTest(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            if (!VoiceOn) { arg.ReplyWith("Voice is off — set TTSEndpoint first."); return; }
            var key = arg.GetString(0, "").ToLowerInvariant();
            if (!_config.Voices.ContainsKey(key)) { arg.ReplyWith($"Unknown trader '{key}'. Configured: {string.Join(", ", _config.Voices.Keys)}"); return; }
            var text = arg.Args != null && arg.Args.Length > 1
                ? string.Join(" ", arg.Args, 1, arg.Args.Length - 1)
                : "If you can hear this, the wire works. Come closer, I do not shout twice.";

            var pos = Vector3.zero;
            float yaw = 0f;
            var shopInfo = Interface.CallHook("GetQuestShopPositions") as List<Vector3>;
            var player = arg.Player();
            if (player != null) { pos = player.transform.position; yaw = player.viewAngles.y; }
            else if (shopInfo != null && shopInfo.Count > 0) pos = shopInfo[0];
            if (pos == Vector3.zero) { arg.ReplyWith("Nowhere to play — run in-game or place a shop first."); return; }

            Speak(key, text, pos, yaw);
            var chunks = SplitChunks(SpeakableText(text));
            var firstCached = chunks.Count > 0 && _cacheIndex.ContainsKey(LineHash(_config.Voices[key], chunks[0]));
            arg.ReplyWith($"Speaking as {key} ({_config.Voices[key]}) — {chunks.Count} chunk(s), first {(firstCached ? "cache HIT" : "miss, rendering")}.");
        }

        [ConsoleCommand("rq.voice.rebuild")]
        private void CmdRebuild(ConsoleSystem.Arg arg)
        {
            if (!Allowed(arg)) return;
            var n = 0;
            try
            {
                foreach (var f in System.IO.Directory.GetFiles(CacheDir, "*.ogg")) { System.IO.File.Delete(f); n++; }
            }
            catch (Exception e) { arg.ReplyWith($"Cache clear failed: {e.Message}"); return; }
            _cacheIndex.Clear();
            _cacheDirty = true;
            SaveCacheIndex();
            arg.ReplyWith($"Voice cache cleared ({n} file(s)) — lines re-render on next play.");
        }

        #endregion
    }
}

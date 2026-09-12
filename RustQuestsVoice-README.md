# RustQuests Voice

Optional voice for the **RustQuests** traders: every scripted dialog line and every SLM chat
reply is rendered by TTS and played from a radio hidden in each trader's shop counter — on
**completely vanilla clients**. No client mod, no voice-chat relay: lines are rendered to Ogg
Vorbis server-side and delivered through Rust's own cassette/boombox mechanism.

By LowPopLabs.

## What's in this bundle

| File | What | Where it goes |
|---|---|---|
| `RustQuestsVoice.cs` | The voice plugin (v1.3.1) | `oxide/plugins/` |
| `Oxide.Ext.RustQuestsVoice.dll` | The render engine — an Oxide extension (v0.3.0) | `RustDedicated_Data/Managed/` |

## Requirements

- **RustQuests 2.16.0 or newer** installed and running (the core plugin fires the
  `OnRustQuestsTraderLine` / `OnRustQuestsTraderChat` hooks this plugin listens for).
  Core works fine without voice; voice does nothing without core.
- An **OpenAI-compatible TTS endpoint** (`/v1/audio/speech`). Two proven options:
  - **Local GPU:** [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI) — free,
    fast (sub-second renders), private.
  - **Hosted, no GPU:** [OpenRouter](https://openrouter.ai) with model `hexgrad/kokoro-82m` —
    same model, identical voices, just an API key.

## Install

1. Copy `Oxide.Ext.RustQuestsVoice.dll` into `RustDedicated_Data/Managed/` (next to Oxide's
   own DLLs — the same way `Oxide.Ext.Discord` installs).
2. **Restart the server.** Extensions load at boot only; they do not hot-load.
3. Copy `RustQuestsVoice.cs` into `oxide/plugins/`. It hot-compiles; with no endpoint
   configured yet it loads silent.
4. Edit `oxide/config/RustQuestsVoice.json` (generated on first load) and set the endpoint,
   then `oxide.reload RustQuestsVoice`.

The boot/load log line to look for: `Trader voice on: <endpoint> model=...`.

## Configure

Local Kokoro-FastAPI:

```json
"TTSEndpoint": "http://localhost:8880/v1/audio/speech",
"TTSModel": "kokoro",
"TTSApiKey": "",
"TTSFormat": "wav"
```

OpenRouter (no GPU needed):

```json
"TTSEndpoint": "https://openrouter.ai/api/v1/audio/speech",
"TTSModel": "hexgrad/kokoro-82m",
"TTSApiKey": "sk-or-...",
"TTSFormat": "pcm"
```

(OpenRouter's speech endpoint accepts only `mp3` or `pcm` — use `pcm`. Local
Kokoro-FastAPI uses `wav`.)

Everything else has sensible defaults:

- `Voices` — trader key → Kokoro voice id. Shipped casting: sonia=`af_jessica`,
  olivia=`af_aoede`, alexa=`af_river`, rebecca=`bf_lily`. A trader missing from the
  map stays silent.
- `LoudnessDbfs` (−13) — render loudness; part of the cache key, so retuning
  re-renders automatically.
- `MaxSpokenChars` (400) — cassette-sized chunk. Longer lines are split at sentence
  boundaries and played back to back, so long story dialogs are never cut short.
- `RadioOffset` — where the radio sits relative to the trader (default sinks it into
  the counter behind her, out of sight).
- Setting `TTSEndpoint` to `""` disables voice entirely.

## Commands (admin / server console)

- `rq.voice.status` — endpoint, cache size, session stats, per-trader rig state.
- `rq.voice.test <trader> [text...]` — render + play a line at that trader's radio.
- `rq.voice.rebuild` — clear the render cache (lines re-render on next play).

## How it behaves

- Rendered lines are cached on disk (sha1 of model+voice+speed+loudness+text) — each
  line renders once, then plays instantly forever.
- Only what she'd actually say is spoken: markup, objective bullet lists, progress
  counters, and leading third-person stage directions are stripped from the audio.
  On-screen text is never altered.
- Three consecutive render failures trip a 5-minute circuit breaker; traders fall
  back to silent (scripted text still shows). Nothing breaks if the endpoint is down.
- Radios are unsaved entities, cleaned automatically on unload and shop re-placement.

## License / source

Vendored Ogg Vorbis encoder:
[SteveLillis/.NET-Ogg-Vorbis-Encoder](https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder) (MIT),
compiled into the extension DLL.

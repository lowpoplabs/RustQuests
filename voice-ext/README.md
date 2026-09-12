# Oxide.Ext.RustQuestsVoice

Oxide **extension** (plain DLL, no Docker, no sidecar) that gives the RustQuests plugin its
voice pipeline: text → any OpenAI-compatible `/v1/audio/speech` endpoint → loudness-normalized
**mono Ogg Vorbis** — the one audio format vanilla Rust clients decode, delivered in-game via
the cassette mechanism (decision 0005).

Why an extension: the plugin sandbox can't do binary HTTP, PCM math at native speed, or ship a
vorbis encoder — but extensions run unsandboxed and install the same way `Oxide.Ext.Discord`
does on every hosted Rust platform: **upload one DLL next to Oxide's own and restart.**

```
plugin ── VoiceTts.Render(req, cb) ──> worker thread ── POST (wav) ──> Kokoro-FastAPI (local GPU)
             <── {ogg bytes, seconds} ──┘  parse → normalize → vorbis      or OpenRouter (hosted)
```

Model parity rule: whatever model the config names locally must also exist on OpenRouter
(kokoro-82m does) so servers without a GPU keep identical voices by changing two config values.

## Install (server)

1. Copy `Oxide.Ext.RustQuestsVoice.dll` into `RustDedicated_Data/Managed/`.
2. Restart the server (extensions load at boot — they do not hot-load like plugins).

## Build

`./build.sh [Managed-dir] [out-dir]` — compiles `src/` + the vendored encoder against the live
server assemblies (Roslyn csc, `-nostdlib`, excluding `Oxide.References.dll`).

## API (from a plugin)

```csharp
using Oxide.Ext.RustQuestsVoice;

VoiceTts.Render(new VoiceTts.Request
{
    Endpoint = "http://localhost:8880/v1/audio/speech",
    ApiKey   = "",            // Bearer token for hosted endpoints
    Model    = "kokoro",
    Voice    = "af_heart",
    Text     = "If you can hear this, the wire works.",
}, result => {
    // Fires on a WORKER THREAD — NextTick before touching game state.
    if (result.Ok) { /* result.Ogg → FileStorage; result.Seconds → off-timer */ }
    else Puts($"tts failed: {result.Error}");
});
```

Requests `response_format: "wav"` upstream (no lossy decode needed server-side); parses
16/24/32-bit int + 32-bit float WAV, downmixes to mono; normalizes to −16 dBFS RMS with a
tanh soft limiter at −1 dBFS (the fix for the quiet PoC playback); encodes VBR vorbis q0.4
with a deterministic stream serial so identical text re-renders byte-identically.

`vendor/OggVorbisEncoder/` is [SteveLillis/.NET-Ogg-Vorbis-Encoder](https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder)
(MIT, license included), compiled into the single output DLL.
